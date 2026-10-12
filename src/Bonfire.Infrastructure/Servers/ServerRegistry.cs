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
                var text = File.ReadAllText(paths.RecordFile);
                if (NewerSchema(text) is { } newer)
                {
                    // Checked before the typed read: a newer record may use values this version cannot even parse.
                    entries.Add(new RegistryEntry(id, paths, null, $"Record schema {newer} is newer than supported."));
                    continue;
                }

                var record = JsonSerializer.Deserialize<ServerRecord>(text, Json);
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
        if (record is null || !IsComplete(d))
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
        if (errors.Count > 0)
        {
            return $"Record is invalid: {string.Join(", ", errors)}";
        }

        // Repair turns the pending definition into a container, so it must be complete and valid too.
        if (record.PendingChange is { } change
            && (!IsComplete(change.NewDefinition) || !IsComplete(change.OldDefinition) || ServerDefinitionValidator.Validate(change.NewDefinition!).Count > 0))
        {
            return "Record has an invalid pending settings change.";
        }

        return null;
    }

    /// <summary>Every member the validator dereferences is present (valid JSON can still be <c>{}</c>).</summary>
    private static bool IsComplete([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] ServerDefinition? d) =>
        d is not null && d.Image is not null && d.Resources is not null && d.Game is not null
        && d.Game.Whitelist is not null && d.DisplayName is not null && d.MinecraftVersion is not null
        && d.Image.Repository is not null && d.Image.Tag is not null && d.Image.Digest is not null;

    /// <summary>The schema version from the raw JSON when it is newer than this version supports; otherwise null.</summary>
    private static int? NewerSchema(string text)
    {
        using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 64 });
        return document.RootElement.ValueKind == JsonValueKind.Object
               && document.RootElement.TryGetProperty(nameof(ServerRecord.SchemaVersion), out var version)
               && version.ValueKind == JsonValueKind.Number
               && version.TryGetInt32(out var schema)
               && schema > ServerRecord.CurrentSchemaVersion
            ? schema
            : null;
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
