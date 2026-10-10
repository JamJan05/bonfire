namespace Bonfire.Core.Content;

public sealed record ModrinthFile(string Url, string FileName, string Sha1, string Sha512, long Size, bool Primary, string? FileType);

public enum DependencyType
{
    Required,
    Optional,
    Incompatible,
    Embedded,
}

/// <param name="ProjectId">May be null when only <paramref name="VersionId"/> or <paramref name="FileName"/> is given.</param>
public sealed record ModrinthDependency(string? ProjectId, string? VersionId, string? FileName, DependencyType Type);

public sealed record ModrinthVersion(
    string Id,
    string ProjectId,
    string Name,
    string VersionNumber,
    string VersionType,
    IReadOnlyList<string> GameVersions,
    IReadOnlyList<string> Loaders,
    string? Environment,
    DateTimeOffset Published,
    IReadOnlyList<ModrinthFile> Files,
    IReadOnlyList<ModrinthDependency> Dependencies)
{
    public bool IsRelease => VersionType == "release";
}

public sealed record ModrinthProject(
    string Id,
    string Slug,
    string Title,
    string Description,
    string? Author,
    string? License,
    long Downloads,
    DateTimeOffset Updated,
    string? ClientSide,
    string? ServerSide,
    string? Environment,
    IReadOnlyList<string> GameVersions,
    IReadOnlyList<string> Loaders);

public enum SearchSort
{
    Relevance,
    Downloads,
    Updated,
}

public sealed record SearchPage(IReadOnlyList<ModrinthProject> Hits, int Total, int Offset);

/// <summary>Read-only access to Modrinth (public API, no token).</summary>
public interface IModrinth
{
    Task<SearchPage> SearchAsync(string query, ContentProfile profile, SearchSort sort, int offset, CancellationToken cancellationToken);

    Task<ModrinthProject> GetProjectAsync(string projectId, CancellationToken cancellationToken);

    /// <summary>Versions of a project compatible with the profile's game version and loaders, newest first.</summary>
    Task<IReadOnlyList<ModrinthVersion>> GetCompatibleVersionsAsync(string projectId, ContentProfile profile, CancellationToken cancellationToken);

    Task<ModrinthVersion> GetVersionAsync(string versionId, CancellationToken cancellationToken);

    /// <summary>
    /// Identifies files by SHA-512. A hash missing from the result means "no match" (distinct from a
    /// failed lookup, which throws).
    /// </summary>
    Task<IReadOnlyDictionary<string, ModrinthVersion>> GetVersionsByHashAsync(IReadOnlyList<string> sha512, CancellationToken cancellationToken);
}
