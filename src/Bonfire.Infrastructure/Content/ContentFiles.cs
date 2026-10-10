using System.Security.Cryptography;
using Bonfire.Core.Content;
using Bonfire.Core.Servers;
using Bonfire.Infrastructure.Linux;

namespace Bonfire.Infrastructure.Content;

/// <summary>A JAR found on disk (live or disabled).</summary>
public sealed record ContentFile(string FileName, bool Disabled, string Sha512, long Size);

/// <summary>Paths of one server's add-on locations. All derived from validated names.</summary>
public sealed class ContentFiles
{
    public const string DisabledRoot = "disabled-content";

    private readonly ServerPaths _paths;

    public ContentFiles(ServerPaths paths, string directory)
    {
        if (directory is not ("mods" or "plugins"))
        {
            throw new ArgumentException("Unknown content directory", nameof(directory));
        }

        _paths = paths;
        Directory = directory;
    }

    public string Directory { get; }

    public string ServerDir => _paths.ServerDir;

    public string LiveDir => Path.Combine(_paths.DataDir, Directory);

    public string DisabledDir => Path.Combine(_paths.ServerDir, DisabledRoot, Directory);

    public string TxDir(Guid txId) => Path.Combine(_paths.ServerDir, PendingContent.TxDirPrefix + txId.ToString("N"));

    /// <param name="archive">The history entry whose preimages <see cref="ContentLocation.Archive"/> means (undo only).</param>
    public string DirFor(ContentLocation location, Guid txId, Guid? archive = null) => location switch
    {
        ContentLocation.Live => LiveDir,
        ContentLocation.Disabled => DisabledDir,
        ContentLocation.Staging => Path.Combine(TxDir(txId), "in"),
        ContentLocation.Trash => Path.Combine(TxDir(txId), "out"),
        ContentLocation.Archive when archive is { } a => Path.Combine(ContentHistoryStore.EntryDir(_paths, a), "files"),
        _ => throw new ArgumentOutOfRangeException(nameof(location)),
    };

    public string PathFor(ContentLocation location, Guid txId, string fileName, Guid? archive = null) =>
        ContentRules.IsSafeJarName(fileName)
            ? Path.Combine(DirFor(location, txId, archive), fileName)
            : throw new ArgumentException("Unsafe file name", nameof(fileName));

    /// <summary>
    /// Every directory on the way must be a real directory: the container owns data/ and could have
    /// left a link (e.g. data/mods → somewhere in the user's home) while it was running.
    /// </summary>
    public void EnsureLayout(Guid? txId = null, Guid? archive = null)
    {
        SafeFiles.EnsureRealDirectory(_paths.ServerDir);
        SafeFiles.EnsureRealDirectory(_paths.DataDir);
        SafeFiles.EnsureRealDirectory(LiveDir);
        SafeFiles.EnsureRealDirectory(Path.Combine(_paths.ServerDir, DisabledRoot));
        SafeFiles.EnsureRealDirectory(DisabledDir);
        if (txId is { } tx)
        {
            SafeFiles.EnsureRealDirectory(TxDir(tx));
            SafeFiles.EnsureRealDirectory(DirFor(ContentLocation.Staging, tx));
            SafeFiles.EnsureRealDirectory(DirFor(ContentLocation.Trash, tx));
        }

        if (archive is { } a)
        {
            // The archive must already exist (it holds the preimages); only its type is checked here.
            foreach (var dir in new[] { ContentHistoryStore.Root(_paths), ContentHistoryStore.EntryDir(_paths, a), DirFor(ContentLocation.Archive, Guid.Empty, a) })
            {
                var info = new DirectoryInfo(dir);
                if (!info.Exists || info.LinkTarget is not null)
                {
                    throw new UnsupportedEntryException(info.Name, "history archive missing or not a directory");
                }
            }
        }
    }

    /// <summary>JARs in the live and disabled directories. Anything else (links, odd names, folders) is reported, not read.</summary>
    public (List<ContentFile> Files, List<string> Skipped) Scan()
    {
        var files = new List<ContentFile>();
        var skipped = new List<string>();
        foreach (var (dir, disabled) in new[] { (LiveDir, false), (DisabledDir, true) })
        {
            var info = new DirectoryInfo(dir);
            if (!info.Exists)
            {
                continue;
            }

            if (info.LinkTarget is not null)
            {
                skipped.Add(Directory); // the whole directory is a link: nothing in it is trusted
                continue;
            }

            foreach (var entry in info.EnumerateFileSystemInfos().OrderBy(e => e.Name, StringComparer.Ordinal))
            {
                if (!entry.Name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
                {
                    continue; // config folders, .disabled files etc. are not add-ons
                }

                if (!ContentRules.IsSafeJarName(entry.Name) || entry.LinkTarget is not null || entry is DirectoryInfo)
                {
                    skipped.Add(entry.Name);
                    continue;
                }

                try
                {
                    var (sha, size) = Hash(entry.FullName, entry.Name);
                    files.Add(new ContentFile(entry.Name, disabled, sha, size));
                }
                catch (IOException)
                {
                    skipped.Add(entry.Name);
                }
            }
        }

        return (files, skipped);
    }

    /// <summary>SHA-512 of a regular file, opened without following links.</summary>
    public static (string Sha512, long Size) Hash(string path, string nameForErrors)
    {
        using var stream = SafeFiles.OpenRegularFile(path, nameForErrors);
        return (Convert.ToHexStringLower(SHA512.HashData(stream)), stream.Length);
    }

    /// <summary>Hash of the file at the location, or null if nothing is there. A link or special file throws.</summary>
    public string? HashAt(ContentLocation location, Guid txId, string fileName, Guid? archive = null)
    {
        var path = PathFor(location, txId, fileName, archive);
        return SafeFiles.EntryExists(path) ? Hash(path, fileName).Sha512 : null;
    }
}
