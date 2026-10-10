using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bonfire.Core;
using Bonfire.Core.Servers;
using Bonfire.Infrastructure.Linux;

namespace Bonfire.Infrastructure.Servers;

/// <summary>
/// Persists server records (server.json) and prepares per-server directories. The registry is
/// the source of truth for which servers Bonfire manages; Docker state is reconciled against it.
/// </summary>
public sealed class ServerRegistry
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly AppPaths _app;

    public ServerRegistry(AppPaths app) => _app = app;

    public string ServersDir => Path.Combine(_app.DataDir, "servers");

    public ServerPaths PathsFor(Guid serverId) => ServerPaths.For(_app, serverId);

    /// <summary>Loads every server directory; unreadable records are reported, never dropped silently.</summary>
    public IReadOnlyList<RegistryEntry> LoadAll()
    {
        if (!Directory.Exists(ServersDir))
        {
            return [];
        }

        var entries = new List<RegistryEntry>();
        foreach (var dir in Directory.EnumerateDirectories(ServersDir))
        {
            if (new DirectoryInfo(dir).LinkTarget is not null
                || !Guid.TryParseExact(Path.GetFileName(dir), "N", out var id))
            {
                continue;
            }

            var paths = new ServerPaths(dir);
            try
            {
                var record = JsonSerializer.Deserialize<ServerRecord>(File.ReadAllText(paths.RecordFile), Json);
                var problem = Check(record, id);
                entries.Add(problem is null
                    ? new RegistryEntry(id, paths, record, null)
                    : new RegistryEntry(id, paths, null, problem));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                entries.Add(new RegistryEntry(id, paths, null, ex.Message));
            }
        }

        return entries;
    }

    /// <summary>Loads one server fresh from disk (never from a cached copy).</summary>
    public RegistryEntry? Load(Guid serverId) => LoadAll().FirstOrDefault(e => e.ServerId == serverId);

    /// <summary>
    /// Structural check of a deserialized record: valid JSON can still miss required parts
    /// (e.g. <c>{}</c>), and a record that fails validation can never be turned into a container.
    /// </summary>
    private static string? Check(ServerRecord? record, Guid directoryId)
    {
        var d = record?.Definition;
        if (record is null || d is null || d.Image is null || d.Resources is null || d.Game is null
            || d.Game.Whitelist is null || d.DisplayName is null || d.MinecraftVersion is null
            || d.Image.Repository is null || d.Image.Tag is null || d.Image.Digest is null)
        {
            return "Record is incomplete.";
        }

        if (d.Id != directoryId)
        {
            return "Record does not match its directory.";
        }

        if (record.SchemaVersion > ServerRecord.CurrentSchemaVersion)
        {
            return $"Record schema {record.SchemaVersion} is newer than supported.";
        }

        if (record.PendingContent is { } content && !content.IsWellFormed())
        {
            return "Record has a malformed add-on transaction.";
        }

        var errors = ServerDefinitionValidator.Validate(d);
        return errors.Count > 0 ? $"Record is invalid: {string.Join(", ", errors)}" : null;
    }

    /// <summary>
    /// Creates the server's directories (owner-only) and a random RCON password file, then
    /// stores the record. Fails if the server directory already exists.
    /// </summary>
    public async Task<ServerPaths> CreateAsync(ServerRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        var paths = PathsFor(record.Definition.Id);

        AppDirectories.EnsureOwnedDirectory(ServersDir);
        Directory.CreateDirectory(paths.ServerDir, AppDirectories.OwnerOnly);
        if (Directory.EnumerateFileSystemEntries(paths.ServerDir).Any())
        {
            throw new IOException($"Server directory {paths.ServerDir} is not empty.");
        }

        Directory.CreateDirectory(paths.DataDir, AppDirectories.OwnerOnly);
        await WriteSecretAsync(paths.RconPasswordFile, NewPassword(), cancellationToken);
        await SaveAsync(record, cancellationToken);
        return paths;
    }

    public Task SaveAsync(ServerRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        // Every write uses the current format, so an older Bonfire refuses records it cannot fully understand.
        record = record with { SchemaVersion = ServerRecord.CurrentSchemaVersion };
        var paths = PathsFor(record.Definition.Id);
        return AtomicFile.WriteAllTextAsync(paths.RecordFile, JsonSerializer.Serialize(record, Json), cancellationToken);
    }

    /// <summary>
    /// The server's RCON password, read only to redact it from displayed logs. Never logged or shown.
    /// Returns null when the secret file is missing or unreadable.
    /// </summary>
    public string? ReadRconSecret(Guid serverId)
    {
        var path = PathsFor(serverId).RconPasswordFile;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.LinkTarget is not null || info.Length > 1024)
            {
                return null;
            }

            var secret = File.ReadAllText(path).Trim();
            return secret.Length == 0 ? null : secret;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>32 random bytes, URL-safe base64: never shown in the UI or written to logs.</summary>
    private static string NewPassword() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static async Task WriteSecretAsync(string path, string secret, CancellationToken cancellationToken)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        };
        await using var stream = new FileStream(path, options);
        await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(secret), cancellationToken);
        stream.Flush(flushToDisk: true);
    }
}
