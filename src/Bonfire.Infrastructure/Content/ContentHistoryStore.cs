using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Bonfire.Core.Content;
using Bonfire.Core.Servers;
using Bonfire.Infrastructure.Linux;

namespace Bonfire.Infrastructure.Content;

/// <summary>
/// Add-on history: one directory per committed change (content-history/&lt;txId&gt;/ with entry.json
/// and files/). No separate index — the directories are the history. Outside the container mount.
/// </summary>
public static partial class ContentHistoryStore
{
    public const string RootName = "content-history";
    public const int KeepEntries = 10;
    public const long MaxBytes = 512L * 1024 * 1024;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    [GeneratedRegex(@"^[0-9a-f]{32}\z")]
    private static partial Regex EntryName();

    public static string Root(ServerPaths paths) => Path.Combine(paths.ServerDir, RootName);

    public static string EntryDir(ServerPaths paths, Guid txId) => Path.Combine(Root(paths), txId.ToString("N"));

    /// <summary>Valid entries, newest (highest sequence) first. Damaged or foreign directories are ignored.</summary>
    public static List<HistoryEntry> List(ServerPaths paths)
    {
        var root = new DirectoryInfo(Root(paths));
        if (!root.Exists || root.LinkTarget is not null)
        {
            return [];
        }

        var entries = new List<HistoryEntry>();
        foreach (var dir in root.EnumerateDirectories())
        {
            if (EntryName().IsMatch(dir.Name) && dir.LinkTarget is null && Read(dir.FullName) is { } entry && entry.TxId.ToString("N") == dir.Name)
            {
                entries.Add(entry);
            }
        }

        return entries.OrderByDescending(e => e.Sequence).ToList();
    }

    public static long NextSequence(ServerPaths paths) => List(paths).Select(e => e.Sequence).DefaultIfEmpty(0).Max() + 1;

    private static HistoryEntry? Read(string dir)
    {
        try
        {
            var path = Path.Combine(dir, "entry.json");
            if (new FileInfo(path) is not { Exists: true, LinkTarget: null, Length: < 4 * 1024 * 1024 })
            {
                return null;
            }

            var entry = JsonSerializer.Deserialize<HistoryEntry>(File.ReadAllText(path), Json);
            return entry is not null && entry.IsWellFormed() ? entry : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The complete add-on state: the server's compatibility identity, the lock (canonical; absent =
    /// empty) and every file with its hash, live and disabled, plus ignored entries. Undo requires
    /// the state to be exactly what the change left.
    /// </summary>
    public static string StateHash(ServerDefinition definition, ServerPaths paths, string directory) =>
        StateHash(definition, ContentLockStore.Load(paths), paths, directory);

    /// <summary>The same hash with a given lock (the one a transaction is about to write) and the files on disk.</summary>
    public static string StateHash(ServerDefinition definition, ContentLock contentLock, ServerPaths paths, string directory)
    {
        var text = new StringBuilder();
        text.Append(JsonSerializer.Serialize(new
        {
            definition.Type, definition.MinecraftVersion, definition.FabricLoaderVersion, definition.FabricLauncherVersion,
            definition.PaperBuild, definition.PaperExperimentalBuild, definition.JavaMajor,
        })).Append('\n');
        var canonical = contentLock with { Entries = contentLock.Entries.OrderBy(e => e.Disabled).ThenBy(e => e.FileName, StringComparer.Ordinal).ToList() };
        text.Append(ContentLockStore.Serialize(canonical)).Append('\n');
        var (files, skipped) = new ContentFiles(paths, directory).Scan();
        foreach (var f in files.OrderBy(f => f.Disabled).ThenBy(f => f.FileName, StringComparer.Ordinal))
        {
            text.Append(f.Disabled ? 'D' : 'L').Append(' ').Append(f.FileName).Append(' ').Append(f.Sha512).Append('\n');
        }

        foreach (var s in skipped.Order(StringComparer.Ordinal))
        {
            text.Append("S ").Append(s).Append('\n');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    /// <summary>
    /// Publishes the history entry of a committed transaction. Resumable at every step: the removed
    /// files move from the transaction's out/ into hist/files/, every preimage is verified, entry.json
    /// is written, and one directory rename makes the entry visible. An existing entry must match.
    /// </summary>
    public static async Task PublishAsync(ServerPaths paths, ContentFiles files, PendingContent pending, DateTimeOffset now)
    {
        var stateAfter = pending.StateAfter ?? throw new IOException("The committed change has no recorded state");
        var target = EntryDir(paths, pending.TxId);
        var trashed = pending.Moves.Where(m => m.To == ContentLocation.Trash).ToList();
        if (Directory.Exists(target))
        {
            // Everything derived from the transaction must match, not just the id.
            if (Read(target) is not { } existing || !Matches(existing, pending) || !PreimagesIntact(Path.Combine(target, "files"), trashed))
            {
                throw new IOException("A published history entry does not match its transaction");
            }

            return;
        }

        var txDir = files.TxDir(pending.TxId);
        var hist = Path.Combine(txDir, "hist");
        var histFiles = Path.Combine(hist, "files");
        var outDir = files.DirFor(ContentLocation.Trash, pending.TxId);
        SafeFiles.EnsureRealDirectory(hist);
        if (!Directory.Exists(histFiles))
        {
            if (Directory.Exists(outDir))
            {
                SafeFiles.MoveNoReplace(outDir, histFiles);
                SafeFiles.FsyncDirectory(txDir);
                SafeFiles.FsyncDirectory(hist);
            }
            else if (trashed.Count == 0)
            {
                SafeFiles.EnsureRealDirectory(histFiles); // nothing was removed: an empty archive is correct
            }
            else
            {
                throw new IOException("The removed files of a committed change are missing"); // never publish an unrecoverable entry
            }
        }

        if (!PreimagesIntact(histFiles, trashed))
        {
            throw new IOException("An archived file does not match its recorded hash");
        }

        var entry = new HistoryEntry(pending.TxId, pending.Sequence, now, pending.Kind, pending.Summary ?? [], pending.Directory,
            pending.Moves, pending.LockBefore ?? ContentLock.Empty, stateAfter);
        // Leftover temporary files of an interrupted attempt do not belong in the entry.
        foreach (var stray in Directory.EnumerateFiles(hist, ".*.tmp"))
        {
            File.Delete(stray);
        }

        await AtomicFile.WriteAllTextAsync(Path.Combine(hist, "entry.json"), JsonSerializer.Serialize(entry, Json));
        SafeFiles.FsyncDirectory(histFiles);
        SafeFiles.FsyncDirectory(hist);

        SafeFiles.EnsureRealDirectory(Root(paths));
        SafeFiles.MoveNoReplace(hist, target);
        SafeFiles.FsyncDirectory(Root(paths));
    }

    private static bool Matches(HistoryEntry e, PendingContent p) =>
        e.TxId == p.TxId && e.Sequence == p.Sequence && e.Kind == p.Kind && e.Directory == p.Directory && e.StateAfter == p.StateAfter
        && e.Moves.SequenceEqual(p.Moves) && e.Summary.SequenceEqual(p.Summary ?? [])
        && ContentLockStore.Serialize(e.LockBefore) == ContentLockStore.Serialize(p.LockBefore ?? ContentLock.Empty);

    private static bool PreimagesIntact(string dir, IEnumerable<ContentMove> trashed)
    {
        foreach (var m in trashed)
        {
            var path = Path.Combine(dir, m.FileName);
            try
            {
                if (!File.Exists(path) || ContentFiles.Hash(path, m.FileName).Sha512 != m.Sha512)
                {
                    return false;
                }
            }
            catch (IOException)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Deletes an entry: first renamed out of the listing (so a half-deleted entry is never offered),
    /// then removed. Idempotent.
    /// </summary>
    public static void Delete(ServerPaths paths, Guid txId)
    {
        var dir = EntryDir(paths, txId);
        if (Directory.Exists(dir) || new DirectoryInfo(dir).LinkTarget is not null)
        {
            var doomed = Path.Combine(Root(paths), $".deleted-{txId:N}-{Guid.NewGuid():N}");
            SafeFiles.MoveNoReplace(dir, doomed);
            SafeFiles.FsyncDirectory(Root(paths));
        }

        SweepDeleted(paths);
    }

    /// <summary>Keeps the newest entries within the limits (the newest one always stays). Best effort: errors propagate to a warning.</summary>
    public static void Prune(ServerPaths paths, Guid? protect)
    {
        var entries = List(paths);
        long bytes = 0;
        for (var i = 0; i < entries.Count; i++)
        {
            bytes += Size(EntryDir(paths, entries[i].TxId));
            if (i > 0 && entries[i].TxId != protect && (i >= KeepEntries || bytes > MaxBytes))
            {
                Delete(paths, entries[i].TxId);
            }
        }
    }

    /// <summary>Removes the whole history (after a restore, nothing in it describes the files any more).</summary>
    public static void Clear(ServerPaths paths)
    {
        var root = Root(paths);
        if (Directory.Exists(root) || new DirectoryInfo(root).LinkTarget is not null)
        {
            var doomed = Path.Combine(paths.ServerDir, $"{RootName}.cleared-{Guid.NewGuid():N}");
            SafeFiles.MoveNoReplace(root, doomed);
            SafeFiles.FsyncDirectory(paths.ServerDir);
        }

        foreach (var old in Directory.EnumerateDirectories(paths.ServerDir, RootName + ".cleared-*"))
        {
            SafeFiles.DeleteTree(old, paths.ServerDir);
        }
    }

    private static void SweepDeleted(ServerPaths paths)
    {
        var root = Root(paths);
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var old in Directory.EnumerateDirectories(root, ".deleted-*"))
        {
            SafeFiles.DeleteTree(old, root);
        }
    }

    private static long Size(string dir) =>
        Directory.Exists(dir) ? new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) : 0;
}
