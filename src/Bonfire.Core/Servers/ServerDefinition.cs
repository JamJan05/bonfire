namespace Bonfire.Core.Servers;

public enum ServerType
{
    Vanilla,
    Fabric,
    Paper,
}

/// <summary>Which host addresses the game port is published on.</summary>
public enum PortExposure
{
    /// <summary>127.0.0.1 only — reachable from this computer.</summary>
    Localhost,

    /// <summary>0.0.0.0 — every IPv4 interface of the host, possibly including public ones.</summary>
    AllInterfaces,
}

/// <summary>
/// Docker restart policies offered to the user. "always" is deliberately absent: it would
/// restart a server Bonfire stopped on purpose when the Docker daemon restarts.
/// </summary>
public enum RestartPolicyKind
{
    No,
    OnFailure,
    UnlessStopped,
}

public enum GameMode
{
    Survival,
    Creative,
    Adventure,
    Spectator,
}

public enum Difficulty
{
    Peaceful,
    Easy,
    Normal,
    Hard,
}

/// <summary>A container image pinned by digest; the tag is kept only as provenance.</summary>
public sealed record ImageReference(string Repository, string Tag, string Digest)
{
    public const string ItzgRepository = "itzg/minecraft-server";

    public string PinnedReference => $"{Repository}@{Digest}";
}

/// <summary>Heap sizes for the JVM and the CPU limit of the container.</summary>
public sealed record ResourceLimits(int HeapMinMb, int HeapMaxMb, double CpuLimit)
{
    /// <summary>Non-heap headroom: 25 % of the heap (itzg guidance) plus a fixed 256 MiB.</summary>
    public long ContainerMemoryBytes => ((long)Math.Ceiling(HeapMaxMb * 1.25) + 256) * 1024 * 1024;
}

/// <summary>
/// Game settings passed to the image as environment variables. The optional ones are null when
/// Bonfire does not manage them: then no variable is set and the image leaves (or computes, as for
/// MOTD) the property itself. <see cref="Whitelist"/> only seeds whitelist.json when it does not
/// exist yet; afterwards the server's own whitelist (console "whitelist add/remove") is authoritative.
/// </summary>
public sealed record GameSettings(
    bool OnlineMode,
    Difficulty Difficulty,
    int MaxPlayers,
    bool WhitelistEnabled,
    IReadOnlyList<string> Whitelist,
    string? Seed,
    string? Motd = null,
    GameMode? GameMode = null,
    bool? Pvp = null,
    int? ViewDistance = null,
    int? SpawnProtection = null,
    bool? AllowFlight = null,
    bool? EnableCommandBlock = null,
    bool? EnforceWhitelist = null)
{
    public bool Equals(GameSettings? other) =>
        other is not null
        && OnlineMode == other.OnlineMode
        && Difficulty == other.Difficulty
        && MaxPlayers == other.MaxPlayers
        && WhitelistEnabled == other.WhitelistEnabled
        && Whitelist.SequenceEqual(other.Whitelist)
        && Seed == other.Seed
        && Motd == other.Motd
        && GameMode == other.GameMode
        && Pvp == other.Pvp
        && ViewDistance == other.ViewDistance
        && SpawnProtection == other.SpawnProtection
        && AllowFlight == other.AllowFlight
        && EnableCommandBlock == other.EnableCommandBlock
        && EnforceWhitelist == other.EnforceWhitelist;

    public override int GetHashCode() => HashCode.Combine(OnlineMode, Difficulty, MaxPlayers, WhitelistEnabled, Seed, Motd, GameMode);
}

/// <summary>
/// Fully resolved, runnable description of a server. Every version is concrete — "latest"
/// is resolved before a definition is created, so recreating the container is reproducible.
/// </summary>
public sealed record ServerDefinition(
    Guid Id,
    string DisplayName,
    ServerType Type,
    string MinecraftVersion,
    string? FabricLoaderVersion,
    string? FabricLauncherVersion,
    int? PaperBuild,
    bool PaperExperimentalBuild,
    int JavaMajor,
    ImageReference Image,
    ResourceLimits Resources,
    int Port,
    PortExposure Exposure,
    RestartPolicyKind RestartPolicy,
    GameSettings Game,
    DateTimeOffset EulaAcceptedAt,
    DateTimeOffset CreatedAt)
{
    /// <summary>Docker resource name derived from the immutable id, never from the display name.</summary>
    public string ResourceName => $"bonfire-{Id:N}";
}
