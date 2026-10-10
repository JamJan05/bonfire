using Bonfire.Infrastructure.Content;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bonfire.Core;
using Bonfire.Core.Backups;
using Bonfire.Core.Servers;
using Bonfire.Infrastructure.Linux;

namespace Bonfire.Infrastructure.Backups;

/// <summary>
/// Backups of a server's data directory as verified tar.gz archives, stored outside anything
/// mounted into containers: $XDG_DATA_HOME/bonfire/backups/&lt;server&gt;/&lt;backup&gt;/
/// {archive.tar.gz, backup.json}. A backup becomes visible only after it was fully verified.
/// </summary>
public sealed class BackupStore
{
    public const string ArchiveName = "archive.tar.gz";
    public const string RecordName = "backup.json";
    private const string DataPrefix = "data/";
    private const string ExtraPrefix = "bonfire/";
    private const string ManifestEntry = "metadata/manifest.json";

    // Independent limits: the manifest is checked against these, never trusted alone.
    public const int MaxEntries = 2_000_000;
    public const int MaxPathLength = 1024;
    public const int MaxDepth = 64;
    public const long MaxManifestBytes = 256L * 1024 * 1024;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    private readonly AppPaths _app;

    public BackupStore(AppPaths app) => _app = app;

    public string BackupsDirFor(Guid serverId) => Path.Combine(_app.DataDir, "backups", serverId.ToString("N"));

    // ---------------------------------------------------------------- listing

    public IReadOnlyList<BackupInfo> List(Guid serverId)
    {
        var dir = BackupsDirFor(serverId);
        if (!Directory.Exists(dir))
        {
            return [];
        }

        var result = new List<BackupInfo>();
        foreach (var backupDir in Directory.EnumerateDirectories(dir).Where(d => !Path.GetFileName(d).StartsWith('.')))
        {
            result.Add(Read(backupDir));
        }

        return result.OrderByDescending(b => b.CreatedAt).ToList();
    }

    public BackupInfo? Find(Guid serverId, Guid backupId) => List(serverId).FirstOrDefault(b => b.BackupId == backupId);

    private static BackupInfo Read(string backupDir)
    {
        try
        {
            var record = JsonSerializer.Deserialize<BackupRecord>(File.ReadAllText(Path.Combine(backupDir, RecordName)), Json);
            var m = record?.Manifest;
            if (record is null || m is null || m.Files is null || m.Directories is null || m.ServerName is null || m.MinecraftVersion is null
                || m.BackupId == Guid.Empty || record.ArchiveBytes < 0 || record.ArchiveSha256 is not { Length: 64 }
                || m.Files.Count + m.Directories.Count > MaxEntries
                || m.Files.Any(f => f is null || !IsSafeRelativePath(f.Path) || !IsSha256(f.Sha256) || f.Size < 0)
                || m.Directories.Any(d => d is null || !IsSafeRelativePath(d))
                || !IsSha256(record.ArchiveSha256)
                || !ExtrasValid(m)
                || m.Files.Concat(m.Extras ?? []).Aggregate(0L, (sum, f) => sum > long.MaxValue - f.Size ? throw new InvalidDataException("sizes overflow") : sum + f.Size) < 0)
            {
                throw new InvalidDataException("incomplete backup record");
            }

            var archive = new FileInfo(Path.Combine(backupDir, ArchiveName));
            var error = !archive.Exists || archive.LinkTarget is not null ? "archive missing"
                : archive.Length != record.ArchiveBytes ? "archive size does not match"
                : m.FormatVersion > BackupManifest.CurrentFormatVersion ? "made by a newer Bonfire"
                : null;
            return new BackupInfo(m.BackupId, m.Kind, m.CreatedAt, record.ArchiveBytes, m.TotalBytes, m.Files.Count, backupDir, record, error);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or NotSupportedException)
        {
            return new BackupInfo(Guid.Empty, BackupKind.Manual, Directory.GetCreationTimeUtc(backupDir), 0, 0, 0, backupDir, null, ex.Message);
        }
    }

    /// <summary>v2 must declare its add-on state completely; v1 has none.</summary>
    private static bool ExtrasValid(BackupManifest m)
    {
        if (m.FormatVersion < 2)
        {
            return m.Extras is null or { Count: 0 } && m.ExtraDirectories is null or { Count: 0 } && !m.DisabledContentIncluded;
        }

        return m.Extras is not null && m.ExtraDirectories is not null && m.DisabledContentIncluded
               && m.Files.Count + m.Directories.Count + m.Extras.Count + m.ExtraDirectories.Count <= MaxEntries
               && m.Extras.All(f => f is not null && IsSafeRelativePath(f.Path) && BackupManifest.IsAllowedExtra(f.Path)
                                    && f.Path != BackupManifest.DisabledPath && IsSha256(f.Sha256) && f.Size >= 0)
               && m.ExtraDirectories.All(d => d is not null && IsSafeRelativePath(d) && BackupManifest.IsAllowedExtra(d) && d != BackupManifest.LockPath)
               && m.Extras.Select(f => f.Path).Concat(m.ExtraDirectories).Distinct(StringComparer.Ordinal).Count() == m.Extras.Count + m.ExtraDirectories.Count;
    }

    // ---------------------------------------------------------------- create

    public async Task<BackupInfo> CreateAsync(
        ServerDefinition definition,
        ServerPaths paths,
        BackupKind kind,
        TimeProvider time,
        IProgress<BackupProgress>? progress,
        CancellationToken cancellationToken)
    {
        var serverDir = BackupsDirFor(definition.Id);
        AppDirectories.EnsureOwnedDirectory(Path.GetDirectoryName(serverDir)!);
        AppDirectories.EnsureOwnedDirectory(serverDir);
        RemoveLeftovers(serverDir);

        var tree = SafeFiles.Walk(paths.DataDir);
        var extras = ContentExtras(paths);
        var sizes = tree.Concat(extras).Where(e => !e.IsDirectory).Sum(e => new FileInfo(e.FullPath).Length);
        EnsureSpace(serverDir, sizes + (64L * 1024 * 1024));

        var backupId = Guid.NewGuid();
        var temp = Path.Combine(serverDir, $".tmp-{backupId:N}");
        Directory.CreateDirectory(temp, AppDirectories.OwnerOnly);
        try
        {
            var archivePath = Path.Combine(temp, ArchiveName);
            var manifest = await WriteArchiveAsync(archivePath, definition, kind, backupId, time, tree, extras, sizes, progress, cancellationToken);

            // Verify what was actually written, from the file on disk.
            var (archiveSha, archiveBytes) = await VerifyAsync(archivePath, manifest, extractTo: null, progress: null, cancellationToken);
            var record = new BackupRecord(manifest, archiveSha, archiveBytes);
            await AtomicFile.WriteAllTextAsync(Path.Combine(temp, RecordName), JsonSerializer.Serialize(record, Json), cancellationToken);
            SafeFiles.FsyncDirectory(temp);

            // Publishing is one rename of the whole directory: never a half-visible backup.
            var final = Path.Combine(serverDir, $"{manifest.CreatedAt:yyyyMMdd-HHmmss}-{kind.ToString().ToLowerInvariant()}-{backupId.ToString("N")[..8]}");
            Directory.Move(temp, final);
            SafeFiles.FsyncDirectory(serverDir);
            return Read(final);
        }
        catch
        {
            SafeFiles.DeleteTree(temp, serverDir);
            throw;
        }
    }

    /// <summary>
    /// The add-on state outside /data, as tree entries relative to "bonfire/": the lock (only if it
    /// is valid — a damaged lock fails the backup) and the disabled files (real files only).
    /// </summary>
    private static List<TreeEntry> ContentExtras(ServerPaths paths)
    {
        var extras = new List<TreeEntry>();
        var lockPath = ContentLockStore.PathFor(paths);
        if (File.Exists(lockPath) || new FileInfo(lockPath).LinkTarget is not null)
        {
            using (var stream = SafeFiles.OpenRegularFile(lockPath, BackupManifest.LockPath))
            using (var reader = new StreamReader(stream))
            {
                if (ContentLockStore.Deserialize(reader.ReadToEnd()) is null)
                {
                    throw new UnsupportedEntryException(BackupManifest.LockPath, "damaged");
                }
            }

            extras.Add(new TreeEntry(BackupManifest.LockPath, lockPath, false));
        }

        var disabled = Path.Combine(paths.ServerDir, ContentFiles.DisabledRoot);
        if (Directory.Exists(disabled) || new DirectoryInfo(disabled).LinkTarget is not null)
        {
            extras.Add(new TreeEntry(BackupManifest.DisabledPath, disabled, true));
            extras.AddRange(SafeFiles.Walk(disabled).Select(e =>
                e with { RelativePath = $"{BackupManifest.DisabledPath}/{e.RelativePath}" }));
        }

        return extras;
    }

    private static async Task<BackupManifest> WriteArchiveAsync(
        string archivePath, ServerDefinition d, BackupKind kind, Guid backupId, TimeProvider time,
        List<TreeEntry> tree, List<TreeEntry> extrasTree, long totalBytes, IProgress<BackupProgress>? progress, CancellationToken cancellationToken)
    {
        var files = new List<BackupFile>();
        var directories = new List<string>();
        var extras = new List<BackupFile>();
        var extraDirectories = new List<string>();
        long done = 0;

        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        };
        await using (var file = new FileStream(archivePath, options))
        {
            await using (var gzip = new GZipStream(file, CompressionLevel.Fastest, leaveOpen: true))
            await using (var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
            {
                foreach (var (entry, prefix) in tree.Select(e => (e, DataPrefix)).Concat(extrasTree.Select(e => (e, ExtraPrefix))))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var isExtra = prefix == ExtraPrefix;
                    if (entry.IsDirectory)
                    {
                        (isExtra ? extraDirectories : directories).Add(entry.RelativePath);
                        await tar.WriteEntryAsync(new PaxTarEntry(TarEntryType.Directory, prefix + entry.RelativePath + "/")
                        {
                            Mode = AppDirectories.OwnerOnly,
                        }, cancellationToken);
                        continue;
                    }

                    // Hash first, then archive the same (stopped, unchanging) file; verification
                    // afterwards re-hashes the archive, so any difference is caught.
                    string sha;
                    long size;
                    await using (var source = SafeFiles.OpenRegularFile(entry.FullPath, entry.RelativePath))
                    {
                        size = source.Length;
                        sha = Convert.ToHexStringLower(await SHA256.HashDataAsync(source, cancellationToken));
                        source.Position = 0;
                        await tar.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, prefix + entry.RelativePath)
                        {
                            Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
                            DataStream = source,
                        }, cancellationToken);
                    }

                    (isExtra ? extras : files).Add(new BackupFile(entry.RelativePath, size, sha));
                    done += size;
                    progress?.Report(new BackupProgress(done, totalBytes));
                }

                var manifest = new BackupManifest(
                    BackupManifest.CurrentFormatVersion, backupId, d.Id, d.DisplayName, kind, time.GetUtcNow(), d.Type,
                    d.MinecraftVersion, d.FabricLoaderVersion, d.FabricLauncherVersion, d.PaperBuild, directories, files,
                    extras, extraDirectories, DisabledContentIncluded: true);
                var json = JsonSerializer.SerializeToUtf8Bytes(manifest, Json);
                await tar.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, ManifestEntry)
                {
                    Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
                    DataStream = new MemoryStream(json),
                }, cancellationToken);

                // Disposing finishes the tar trailer and the gzip footer before syncing.
                await tar.DisposeAsync();
                await gzip.DisposeAsync();
                file.Flush(flushToDisk: true);
                return manifest;
            }
        }
    }

    // ---------------------------------------------------------------- verify / extract

    /// <summary>
    /// One pass over the archive: every entry is validated and its contents hashed against the
    /// manifest, the whole file is hashed, and (with <paramref name="extractTo"/>) files are written
    /// to a fresh directory at the same time — verification and extraction read the same bytes.
    /// </summary>
    public static async Task<(string Sha256, long Bytes)> VerifyAsync(
        string archivePath, BackupManifest manifest, string? extractTo, IProgress<BackupProgress>? progress, CancellationToken cancellationToken,
        string? extrasTo = null)
    {
        if (manifest.Files.Count + manifest.Directories.Count + (manifest.Extras?.Count ?? 0) + (manifest.ExtraDirectories?.Count ?? 0) > MaxEntries
            || !ExtrasValid(manifest))
        {
            throw new BackupException(BackupError.Corrupt, "too many entries or invalid add-on state");
        }

        var expectedExtras = (manifest.Extras ?? []).ToDictionary(f => f.Path, StringComparer.Ordinal);
        var expectedExtraDirs = new HashSet<string>(manifest.ExtraDirectories ?? [], StringComparer.Ordinal);
        var seenExtras = new HashSet<string>(StringComparer.Ordinal);
        var seenExtraDirs = new HashSet<string>(StringComparer.Ordinal);

        var expectedFiles = new Dictionary<string, BackupFile>(StringComparer.Ordinal);
        foreach (var f in manifest.Files)
        {
            if (!IsSafeRelativePath(f.Path) || !expectedFiles.TryAdd(f.Path, f) || f.Size < 0)
            {
                throw new BackupException(BackupError.Corrupt, $"manifest entry {f.Path}");
            }
        }

        var expectedDirs = new HashSet<string>(manifest.Directories, StringComparer.Ordinal);
        if (expectedDirs.Any(d => !IsSafeRelativePath(d) || expectedFiles.ContainsKey(d)))
        {
            throw new BackupException(BackupError.Corrupt, "manifest directories");
        }

        var seenFiles = new HashSet<string>(StringComparer.Ordinal);
        var seenDirs = new HashSet<string>(StringComparer.Ordinal);
        var createdDirs = new List<string>();
        var total = manifest.TotalBytes;
        long done = 0;
        var manifestSeen = false;

        using var raw = new HashingReadStream(SafeFiles.OpenRegularFile(archivePath, ArchiveName));
        try
        {
            await using var gzip = new GZipStream(raw, CompressionMode.Decompress, leaveOpen: true);
            using var tar = new TarReader(gzip, leaveOpen: true);
            if (extractTo is not null)
            {
                Directory.CreateDirectory(extractTo, AppDirectories.OwnerOnly);
                createdDirs.Add(extractTo);
            }

            if (extrasTo is not null)
            {
                if (Path.Exists(extrasTo))
                {
                    throw new IOException($"{extrasTo} already exists");
                }

                Directory.CreateDirectory(extrasTo, AppDirectories.OwnerOnly);
                createdDirs.Add(extrasTo);
            }

            TarEntry? entry;
            while ((entry = await tar.GetNextEntryAsync(copyData: false, cancellationToken)) is not null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (seenFiles.Count + seenDirs.Count > MaxEntries)
                {
                    throw new BackupException(BackupError.Corrupt, "too many entries");
                }

                var name = entry.Name;
                if (name == ManifestEntry && entry.EntryType == TarEntryType.RegularFile && !manifestSeen)
                {
                    manifestSeen = true;
                    if (entry.Length > MaxManifestBytes)
                    {
                        throw new BackupException(BackupError.Corrupt, "manifest too large");
                    }

                    continue; // the sidecar copy (already parsed) is what we verify against
                }

                if (name.StartsWith(ExtraPrefix, StringComparison.Ordinal))
                {
                    var extra = name[ExtraPrefix.Length..].TrimEnd('/');
                    switch (entry.EntryType)
                    {
                        case TarEntryType.Directory when expectedExtraDirs.Contains(extra) && seenExtraDirs.Add(extra):
                            if (extrasTo is not null)
                            {
                                var dir = Path.Combine(extrasTo, extra);
                                Directory.CreateDirectory(dir, AppDirectories.OwnerOnly);
                                createdDirs.Add(dir);
                            }

                            break;
                        case TarEntryType.RegularFile or TarEntryType.V7RegularFile
                            when expectedExtras.TryGetValue(extra, out var expectedExtra) && seenExtras.Add(extra) && entry.Length == expectedExtra.Size:
                            var extraSha = await CopyAndHashAsync(entry.DataStream, expectedExtra.Size,
                                extrasTo is null ? null : Path.Combine(extrasTo, extra), cancellationToken);
                            if (extraSha != expectedExtra.Sha256)
                            {
                                throw new BackupException(BackupError.Corrupt, $"contents of {extra} differ");
                            }

                            done += expectedExtra.Size;
                            progress?.Report(new BackupProgress(done, total));
                            break;
                        default:
                            throw new BackupException(BackupError.Corrupt, $"unexpected entry {name}");
                    }

                    continue;
                }

                if (!name.StartsWith(DataPrefix, StringComparison.Ordinal))
                {
                    throw new BackupException(BackupError.Corrupt, $"unexpected entry {name}");
                }

                var rel = name[DataPrefix.Length..].TrimEnd('/');
                switch (entry.EntryType)
                {
                    case TarEntryType.Directory:
                        if (!expectedDirs.Contains(rel) || !seenDirs.Add(rel))
                        {
                            throw new BackupException(BackupError.Corrupt, $"unexpected directory {rel}");
                        }

                        if (extractTo is not null)
                        {
                            var target = Path.Combine(extractTo, rel);
                            Directory.CreateDirectory(target, AppDirectories.OwnerOnly); // archived modes/owners ignored
                            createdDirs.Add(target);
                        }

                        break;

                    case TarEntryType.RegularFile:
                    case TarEntryType.V7RegularFile:
                        if (!expectedFiles.TryGetValue(rel, out var expected) || !seenFiles.Add(rel) || entry.Length != expected.Size)
                        {
                            throw new BackupException(BackupError.Corrupt, $"unexpected file {rel}");
                        }

                        var sha = await CopyAndHashAsync(entry.DataStream, expected.Size, extractTo is null ? null : Path.Combine(extractTo, rel), cancellationToken);
                        if (sha != expected.Sha256)
                        {
                            throw new BackupException(BackupError.Corrupt, $"contents of {rel} differ");
                        }

                        done += expected.Size;
                        progress?.Report(new BackupProgress(done, total));
                        break;

                    default:
                        // Links, devices, FIFOs, hard links: never part of a Bonfire backup.
                        throw new BackupException(BackupError.Corrupt, $"{entry.EntryType} entry {rel}");
                }
            }

            if (seenFiles.Count != expectedFiles.Count || seenDirs.Count != expectedDirs.Count || !manifestSeen
                || seenExtras.Count != expectedExtras.Count || seenExtraDirs.Count != expectedExtraDirs.Count)
            {
                throw new BackupException(BackupError.Corrupt, "archive is incomplete");
            }

            if (extrasTo is not null && manifest.HasContent)
            {
                // An empty snapshot of the disabled files is an empty directory, never "nothing".
                var disabled = Path.Combine(extrasTo, BackupManifest.DisabledPath);
                if (!Directory.Exists(disabled))
                {
                    Directory.CreateDirectory(disabled, AppDirectories.OwnerOnly);
                    createdDirs.Add(disabled);
                }
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or FormatException or EndOfStreamException or ArgumentException)
        {
            throw new BackupException(BackupError.Corrupt, ex.Message, ex);
        }

        await raw.DrainAsync(cancellationToken); // hash the whole file, including trailers
        if (extractTo is not null)
        {
            foreach (var dir in createdDirs.AsEnumerable().Reverse())
            {
                SafeFiles.FsyncDirectory(dir);
            }
        }

        return (raw.HashHex(), raw.BytesRead);
    }

    /// <summary>Extracts a usable backup into a new directory after checking the whole-archive hash too.</summary>
    public static async Task ExtractVerifiedAsync(
        BackupInfo backup, string target, IProgress<BackupProgress>? progress, CancellationToken cancellationToken, string? extrasTarget = null)
    {
        if (!backup.IsUsable)
        {
            throw new BackupException(BackupError.Corrupt, backup.Error ?? "unusable backup");
        }

        if (Path.Exists(target))
        {
            throw new IOException($"{target} already exists");
        }

        var (sha, bytes) = await VerifyAsync(Path.Combine(backup.Directory, ArchiveName), backup.Record!.Manifest, target, progress, cancellationToken, extrasTarget);
        if (sha != backup.Record.ArchiveSha256 || bytes != backup.Record.ArchiveBytes)
        {
            throw new BackupException(BackupError.Corrupt, "archive does not match its record");
        }
    }

    private static async Task<string> CopyAndHashAsync(Stream? data, long size, string? targetPath, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        FileStream? output = null;
        if (targetPath is not null)
        {
            output = new FileStream(targetPath, new FileStreamOptions
            {
                Mode = FileMode.CreateNew, // never overwrite (duplicates, collisions)
                Access = FileAccess.Write,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            });
        }

        try
        {
            var buffer = new byte[81920];
            long copied = 0;
            if (data is not null)
            {
                int n;
                while ((n = await data.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    copied += n;
                    if (copied > size)
                    {
                        throw new BackupException(BackupError.Corrupt, "file longer than declared");
                    }

                    hash.AppendData(buffer, 0, n);
                    if (output is not null)
                    {
                        await output.WriteAsync(buffer.AsMemory(0, n), cancellationToken);
                    }
                }
            }

            if (copied != size)
            {
                throw new BackupException(BackupError.Corrupt, "file shorter than declared");
            }

            output?.Flush(flushToDisk: true);
            return Convert.ToHexStringLower(hash.GetCurrentHash());
        }
        finally
        {
            if (output is not null)
            {
                await output.DisposeAsync();
            }
        }
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    internal static bool IsSafeRelativePath(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > MaxPathLength || path[0] == '/' || path.Contains('\0') || path.Contains('\\'))
        {
            return false;
        }

        var segments = path.Split('/');
        return segments.Length <= MaxDepth && segments.All(s => s.Length > 0 && s != "." && s != "..");
    }

    // ---------------------------------------------------------------- delete / retention / space

    public void Delete(BackupInfo backup)
    {
        var parent = Path.GetDirectoryName(backup.Directory)!;
        SafeFiles.DeleteTree(backup.Directory, parent);
        SafeFiles.FsyncDirectory(parent);
    }

    /// <summary>
    /// Keeps the newest <paramref name="keep"/> usable backups of a kind. The newest usable backup
    /// of the server (any kind) is never deleted. Call only after a new backup was verified.
    /// </summary>
    public IReadOnlyList<BackupInfo> ApplyRetention(Guid serverId, BackupKind kind, int keep)
    {
        var all = List(serverId);
        var newestUsable = all.FirstOrDefault(b => b.IsUsable);
        var victims = all.Where(b => b.IsUsable && b.Kind == kind).Skip(Math.Max(1, keep))
            .Where(b => b.BackupId != newestUsable?.BackupId)
            .ToList();
        foreach (var victim in victims)
        {
            Delete(victim);
        }

        return victims;
    }

    public static void EnsureSpace(string directory, long neededBytes)
    {
        var available = new DriveInfo(directory).AvailableFreeSpace;
        if (available < neededBytes)
        {
            throw new BackupException(BackupError.NotEnoughSpace, $"{neededBytes} bytes needed, {available} available");
        }
    }

    /// <summary>Removes unfinished backups (operations are serialized per server, so none is running).</summary>
    private static void RemoveLeftovers(string serverDir)
    {
        foreach (var temp in Directory.EnumerateDirectories(serverDir, ".tmp-*"))
        {
            SafeFiles.DeleteTree(temp, serverDir);
        }
    }
}
