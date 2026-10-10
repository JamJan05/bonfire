using Bonfire.Core.Servers;

namespace Bonfire.Core.Metadata;

public enum MetadataError
{
    /// <summary>The provider could not be reached or answered with an error.</summary>
    ProviderUnavailable,

    /// <summary>The provider asked us to slow down (HTTP 429).</summary>
    RateLimited,

    /// <summary>The answer was malformed, too large, or contained values we do not accept.</summary>
    InvalidResponse,

    /// <summary>This combination is not offered (e.g. no stable Paper build, no Java image).</summary>
    Unsupported,
}

public sealed class MetadataException(MetadataError error, string detail, Exception? inner = null)
    : Exception($"{error}: {detail}", inner)
{
    public MetadataError Error { get; } = error;

    public string Detail { get; } = detail;

    public TimeSpan? RetryAfter { get; init; }
}

/// <summary>A Minecraft release from Mojang's manifest.</summary>
public sealed record GameVersion(string Id, DateTimeOffset ReleaseTime);

/// <summary>Concrete, pinned versions for one server type and Minecraft version.</summary>
public sealed record ResolvedServerVersion(
    ServerType Type,
    string MinecraftVersion,
    int JavaMajor,
    string? FabricLoaderVersion,
    string? FabricLauncherVersion,
    int? PaperBuild,
    bool PaperVersionSupported);

/// <summary>Looks up versions from Mojang, Fabric and Paper. Results are cached until <see cref="Invalidate"/>.</summary>
public interface IServerMetadata
{
    /// <summary>Minecraft releases (no snapshots), newest first.</summary>
    Task<IReadOnlyList<GameVersion>> GetReleasesAsync(CancellationToken cancellationToken);

    /// <summary>Resolves the newest stable loader/build and the required Java for a type and version.</summary>
    Task<ResolvedServerVersion> ResolveAsync(ServerType type, string minecraftVersion, CancellationToken cancellationToken);

    void Invalidate();
}
