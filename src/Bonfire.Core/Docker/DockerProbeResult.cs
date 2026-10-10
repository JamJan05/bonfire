namespace Bonfire.Core.Docker;

/// <summary>Outcome of trying to talk to the Docker Engine.</summary>
public enum DockerConnectionStatus
{
    Connected,

    /// <summary>DOCKER_HOST points to something other than a local Unix socket.</summary>
    UnsupportedEndpoint,

    /// <summary>The socket does not exist — Docker is not installed or not started.</summary>
    SocketMissing,

    /// <summary>The operating system refused access to the socket.</summary>
    PermissionDenied,

    /// <summary>The daemon answered but refused the request (HTTP 401/403, e.g. an authorization plugin).</summary>
    ApiAccessDenied,

    /// <summary>The socket exists but nothing is listening, or the connection broke.</summary>
    DaemonNotResponding,

    /// <summary>No answer within the probe deadline.</summary>
    Timeout,

    /// <summary>The daemon's API version range does not overlap Bonfire's.</summary>
    ApiIncompatible,

    Failed,
}

/// <summary>Facts reported by the daemon's <c>/version</c> endpoint.</summary>
public sealed record DockerDaemonInfo(
    string ServerVersion,
    Version MinimumApiVersion,
    Version MaximumApiVersion,
    Version? NegotiatedApiVersion,
    string? OperatingSystem,
    string? KernelVersion,
    string? Architecture);

/// <summary>
/// Features reported by <c>/info</c>. Every value is nullable: unknown is not the same as absent.
/// </summary>
public sealed record DockerCapabilities(
    bool? Rootless,
    bool? Seccomp,
    string? SeccompProfile,
    string? CgroupVersion,
    string? CgroupDriver);

public sealed record DockerProbeResult(
    DockerConnectionStatus Status,
    DockerEndpoint? Endpoint,
    DateTimeOffset CheckedAt,
    DockerDaemonInfo? Daemon = null,
    DockerCapabilities? Capabilities = null,
    string? TechnicalDetail = null)
{
    public bool IsConnected => Status == DockerConnectionStatus.Connected;

    /// <summary>Connected, but the daemon runs rootless, which Bonfire does not support yet.</summary>
    public bool RootlessUnsupported => IsConnected && Capabilities?.Rootless == true;

    /// <summary>Connected, and the daemon positively reports no seccomp support.</summary>
    public bool SeccompMissing => IsConnected && Capabilities?.Seccomp == false;

    /// <summary>Connected, but <c>/info</c> could not be read, so capabilities are unknown.</summary>
    public bool CapabilitiesUnknown => IsConnected && Capabilities is null;
}
