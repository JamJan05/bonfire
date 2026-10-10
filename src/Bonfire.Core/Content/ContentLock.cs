namespace Bonfire.Core.Content;

public enum ContentProvider
{
    Modrinth,

    /// <summary>A local file the user installed; compatibility and dependencies are unverified.</summary>
    Local,
}

public enum ContentOrigin
{
    /// <summary>Installed by Bonfire.</summary>
    Installed,

    /// <summary>Found on disk by a scan (not installed by Bonfire).</summary>
    Detected,
}

/// <summary>A dependency as declared by the installed version (kept for later checks and updates).</summary>
public sealed record LockDependency(string? ProjectId, string? VersionId, DependencyType Type)
{
    /// <summary>
    /// The dependency names this artifact: by project (and exact version, if given), or — when
    /// Modrinth only gave a version id — by that globally unique version id.
    /// </summary>
    public bool Names(string? projectId, string? versionId) =>
        (ProjectId is not null || VersionId is not null)
        && (ProjectId is null || ProjectId == projectId)
        && (VersionId is null || VersionId == versionId);

    /// <summary>The dependency is about this project at all (any version), or about exactly this version.</summary>
    public bool Concerns(string? projectId, string? versionId) =>
        ProjectId is not null ? ProjectId == projectId : VersionId is not null && VersionId == versionId;
}

/// <summary>One add-on file of a server, as Bonfire knows it.</summary>
public sealed record LockEntry(
    ContentProvider Provider,
    string? ProjectId,
    string? VersionId,
    string Title,
    string? VersionNumber,
    string FileName,
    string Sha512,
    long Size,
    string GameVersion,
    string ServerLoader,
    IReadOnlyList<string> ArtifactLoaders,
    IReadOnlyList<LockDependency> Dependencies,
    DateTimeOffset InstalledAt,
    bool Pinned,
    ContentOrigin Origin,
    bool Disabled)
{
    /// <summary>Structural check for entries read from disk (lock file or journal).</summary>
    public bool IsWellFormed() =>
        ContentRules.IsSafeJarName(FileName) && ContentRules.IsSha512(Sha512) && Title is not null && GameVersion is not null
        && ServerLoader is not null && ArtifactLoaders is not null && ArtifactLoaders.All(l => l is not null)
        && Dependencies is not null && Dependencies.All(d => d is not null && Enum.IsDefined(d.Type)
                                                             && (d.ProjectId is null || ContentRules.IsSafeId(d.ProjectId))
                                                             && (d.VersionId is null || ContentRules.IsSafeId(d.VersionId)))
        && Enum.IsDefined(Provider) && Enum.IsDefined(Origin)
        && (ProjectId is null || ContentRules.IsSafeId(ProjectId))
        && (VersionId is null || ContentRules.IsSafeId(VersionId));
}

/// <summary>
/// content-lock.json: what Bonfire knows about a server's mods or plugins. Kept next to server.json,
/// outside the container mount; never in a format that belongs to a loader.
/// </summary>
public sealed record ContentLock(int SchemaVersion, IReadOnlyList<LockEntry> Entries)
{
    public const int CurrentSchemaVersion = 1;

    public static ContentLock Empty { get; } = new(CurrentSchemaVersion, []);

    public bool IsWellFormed() =>
        SchemaVersion is >= 1 and <= CurrentSchemaVersion && Entries is not null && Entries.All(e => e is not null && e.IsWellFormed());

    [System.Text.Json.Serialization.JsonIgnore]
    public IEnumerable<LockEntry> Active => Entries.Where(e => !e.Disabled);

    public LockEntry? ActiveProject(string projectId) =>
        Active.FirstOrDefault(e => e.Provider == ContentProvider.Modrinth && e.ProjectId == projectId);
}
