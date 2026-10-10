using System.Text.Json;
using System.Text.Json.Serialization;
using Bonfire.Core.Content;
using Bonfire.Core.Servers;
using Bonfire.Infrastructure.Linux;

namespace Bonfire.Infrastructure.Content;

/// <summary>Reads and writes content-lock.json atomically, keeping the previous version as .bak.</summary>
public static class ContentLockStore
{
    public const string FileName = "content-lock.json";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public static string PathFor(ServerPaths paths) => Path.Combine(paths.ServerDir, FileName);

    /// <summary>Missing file → empty lock. A damaged file falls back to the .bak copy; if both are damaged this throws.</summary>
    public static ContentLock Load(ServerPaths paths)
    {
        var path = PathFor(paths);
        if (!File.Exists(path))
        {
            return ContentLock.Empty;
        }

        return TryRead(path) ?? TryRead(path + ".bak")
            ?? throw new InvalidDataException("content-lock.json and its backup are damaged");
    }

    public const string BeforeRestoreFileName = "content-lock.before-restore.json";

    /// <summary>
    /// After a restore the files come from the backup, so the lock no longer describes them. It is
    /// set aside; the inventory then rebuilds identities from the files' hashes. Idempotent.
    /// </summary>
    public static void ResetAfterRestore(ServerPaths paths)
    {
        var path = PathFor(paths);
        if (File.Exists(path))
        {
            File.Move(path, Path.Combine(paths.ServerDir, BeforeRestoreFileName), overwrite: true);
        }

        File.Delete(path + ".bak");
        SafeFiles.FsyncDirectory(paths.ServerDir);
    }

    public static async Task SaveAsync(ServerPaths paths, ContentLock contentLock)
    {
        var path = PathFor(paths);
        if (File.Exists(path) && TryRead(path) is not null)
        {
            // Keep the last good version before replacing it.
            await AtomicFile.WriteAllTextAsync(path + ".bak", await File.ReadAllTextAsync(path));
        }

        await AtomicFile.WriteAllTextAsync(path, Serialize(contentLock));
    }

    public static string Serialize(ContentLock contentLock) =>
        JsonSerializer.Serialize(contentLock with { SchemaVersion = ContentLock.CurrentSchemaVersion }, Json);

    public static ContentLock? Deserialize(string json)
    {
        try
        {
            var value = JsonSerializer.Deserialize<ContentLock>(json, Json);
            return value is not null && value.IsWellFormed() ? value : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ContentLock? TryRead(string path)
    {
        try
        {
            return File.Exists(path) ? Deserialize(File.ReadAllText(path)) : null;
        }
        catch (IOException)
        {
            return null;
        }
    }
}
