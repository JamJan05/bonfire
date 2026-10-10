using Bonfire.Core.Servers;

namespace Bonfire.Core.Backups;

public enum BackupKind
{
    /// <summary>Made by the user.</summary>
    Manual,

    /// <summary>Made automatically before mod/plugin changes (stage 6).</summary>
    RestorePoint,
}

/// <summary>One file in a backup, with the hash of its contents.</summary>
public sealed record BackupFile(string Path, long Size, string Sha256);

/// <summary>
/// What a backup contains. Stored inside the archive (metadata/manifest.json) and, together with
/// the archive hash, next to it (backup.json). Only data under /data is backed up; the RCON secret
/// file is not — but server.properties inside the data does contain the RCON password, so backups
/// are private files.
/// </summary>
public sealed record BackupManifest(
    int FormatVersion,
    Guid BackupId,
    Guid ServerId,
    string ServerName,
    BackupKind Kind,
    DateTimeOffset CreatedAt,
    ServerType Type,
    string MinecraftVersion,
    string? FabricLoaderVersion,
    string? FabricLauncherVersion,
    int? PaperBuild,
    IReadOnlyList<string> Directories,
    IReadOnlyList<BackupFile> Files,
    IReadOnlyList<BackupFile>? Extras = null,
    IReadOnlyList<string>? ExtraDirectories = null,
    bool DisabledContentIncluded = false)
{
    /// <summary>2 added the add-on state outside /data: content-lock.json and disabled-content/ ("bonfire/" in the archive).</summary>
    public const int CurrentFormatVersion = 2;

    public const string LockPath = "content-lock.json";
    public const string DisabledPath = "disabled-content";

    public long TotalBytes => Files.Sum(f => f.Size) + (Extras?.Sum(f => f.Size) ?? 0);

    /// <summary>The backup also restores the add-on lock and the disabled files (older backups cannot).</summary>
    public bool HasContent => FormatVersion >= 2 && DisabledContentIncluded;

    /// <summary>Extras may only be the lock file or things under disabled-content/.</summary>
    public static bool IsAllowedExtra(string path) =>
        path == LockPath || path == DisabledPath || path.StartsWith(DisabledPath + "/", StringComparison.Ordinal);

    /// <summary>MVP restores only into a server running the same game and loader versions.</summary>
    public bool MatchesVersionsOf(ServerDefinition definition) =>
        Type == definition.Type
        && MinecraftVersion == definition.MinecraftVersion
        && FabricLoaderVersion == definition.FabricLoaderVersion
        && FabricLauncherVersion == definition.FabricLauncherVersion
        && PaperBuild == definition.PaperBuild;
}

/// <summary>The sidecar backup.json: the manifest plus the hash of the finished archive.</summary>
public sealed record BackupRecord(BackupManifest Manifest, string ArchiveSha256, long ArchiveBytes);

/// <summary>A backup as listed for the UI.</summary>
public sealed record BackupInfo(
    Guid BackupId,
    BackupKind Kind,
    DateTimeOffset CreatedAt,
    long ArchiveBytes,
    long DataBytes,
    int FileCount,
    string Directory,
    BackupRecord? Record,
    string? Error)
{
    /// <summary>Complete and verified when written (problems found later are reported on restore).</summary>
    public bool IsUsable => Record is not null && Error is null;
}

public sealed record BackupProgress(long BytesDone, long BytesTotal);

public enum BackupError
{
    /// <summary>The server must be stopped for a consistent backup or a restore.</summary>
    ServerRunning,

    /// <summary>The data contains something that cannot be backed up safely (link, device, unreadable file).</summary>
    UnsupportedEntry,

    /// <summary>Not enough free disk space.</summary>
    NotEnoughSpace,

    /// <summary>The archive is damaged, incomplete or does not match its record.</summary>
    Corrupt,

    /// <summary>The backup was made for a different game/loader version.</summary>
    VersionMismatch,

    /// <summary>The last usable backup cannot be deleted.</summary>
    LastGoodBackup,

    /// <summary>An interrupted restore must be finished first (Repair).</summary>
    RestorePending,
}

public sealed class BackupException(BackupError error, string detail, Exception? inner = null)
    : Exception($"{error}: {detail}", inner)
{
    public BackupError Error { get; } = error;

    public string Detail { get; } = detail;
}
