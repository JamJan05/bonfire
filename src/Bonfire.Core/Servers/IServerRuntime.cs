using Bonfire.Core.Logs;
using Bonfire.Core.Rcon;

namespace Bonfire.Core.Servers;

public enum ServerOperationError
{
    /// <summary>The server's container does not exist (removed outside Bonfire, or not created yet).</summary>
    ContainerMissing,

    /// <summary>The container's real configuration does not match the hardened spec; it will not be started.</summary>
    HardeningMismatch,

    /// <summary>More than one Docker resource claims to be this server.</summary>
    Conflict,

    /// <summary>The operation requires a stopped container.</summary>
    StillRunning,

    /// <summary>The image could not be pulled or resolved to a digest.</summary>
    ImageUnavailable,

    /// <summary>The host port is already taken.</summary>
    PortInUse,

    /// <summary>Another operation on this server is in progress.</summary>
    Busy,

    /// <summary>The Docker daemon is unreachable or lacks required security features (rootless, no seccomp, unknown).</summary>
    DockerUnsupported,

    /// <summary>The server record is missing or damaged.</summary>
    RecordUnavailable,

    /// <summary>A restart was interrupted by a force stop; the server was not started again.</summary>
    RestartAborted,

    /// <summary>The operation needs a running server.</summary>
    NotRunning,

    /// <summary>A previous console command is still running inside the container.</summary>
    CommandStillRunning,

    /// <summary>An interrupted settings change must be completed first (Repair).</summary>
    SettingsChangePending,

    /// <summary>A force stop interrupted the settings change; nothing was changed.</summary>
    SettingsAborted,

    /// <summary>The server's host address is not assigned to any interface (VPN down); it was not started.</summary>
    AddressNotPresent,

    /// <summary>The host's interfaces could not be enumerated, so a specific-address server is not started.</summary>
    AddressCheckFailed,

    /// <summary>
    /// Docker started the container without publishing the game port (seen on Docker 29.8 after a
    /// start that failed to bind the host address). The container was stopped again; it must be
    /// removed and recreated.
    /// </summary>
    PortNotPublished,

    /// <summary>
    /// As <see cref="PortNotPublished"/>, but stopping the container failed too: it may still be
    /// running with the wrong (or no) publication and needs a stop or force stop by the user.
    /// </summary>
    ContainmentFailed,

    /// <summary>Any other Docker error.</summary>
    Docker,
}

/// <summary>A host port binding and the container holding it.</summary>
public sealed record PublishedPort(string ContainerId, PortBinding Binding);

public sealed class ServerOperationException(ServerOperationError error, string detail, Exception? inner = null)
    : Exception($"{error}: {detail}", inner)
{
    public ServerOperationError Error { get; } = error;

    public string Detail { get; } = detail;
}

/// <summary>An image pulled and resolved to an immutable digest.</summary>
public sealed record ResolvedImage(ImageReference Reference, string ImageId);

public sealed record ImagePullProgress(string? Layer, string Status, long? Current, long? Total);

public enum ContainerHealth
{
    None,
    Starting,
    Healthy,
    Unhealthy,
}

/// <summary>Live state of a server's container as reported by Docker.</summary>
public sealed record ServerContainerState(
    Guid ServerId,
    string ContainerId,
    string State,
    ContainerHealth Health,
    DateTimeOffset? StartedAt,
    int? ExitCode,
    bool OomKilled)
{
    public bool IsRunning => State is "running" or "restarting";
}

/// <summary>Creates and controls server containers. All operations are idempotent where possible.</summary>
public interface IServerRuntime
{
    Task<ResolvedImage> AcquireImageAsync(string tag, IProgress<ImagePullProgress>? progress, CancellationToken cancellationToken);

    /// <summary>
    /// Creates (or adopts, after an interrupted attempt) the network and container, verifies the
    /// container's configuration and returns the record in phase Ready. <paramref name="persist"/>
    /// is called after every step that changes Docker state.
    /// </summary>
    Task<ServerRecord> ProvisionAsync(ServerRecord record, Func<ServerRecord, Task> persist, CancellationToken cancellationToken);

    /// <summary>
    /// Verifies the container configuration, refuses a specific host address that is not assigned
    /// right now, starts the container and checks that Docker actually published the game port
    /// (otherwise the container is stopped again and <see cref="ServerOperationError.PortNotPublished"/> is raised).
    /// </summary>
    Task StartAsync(ServerRecord record, CancellationToken cancellationToken);

    /// <summary>
    /// Asks the server to stop and waits without any automatic kill. Cancelling only stops
    /// waiting; the daemon keeps stopping the container.
    /// </summary>
    Task StopAsync(ServerRecord record, CancellationToken cancellationToken);

    /// <summary>
    /// Kills the container immediately; unsaved world data may be lost. While a stop of the same
    /// server is in progress this escalates that stop instead of failing as Busy.
    /// </summary>
    Task KillAsync(ServerRecord record, CancellationToken cancellationToken);

    /// <summary>Changes only the restart policy of the server's existing container.</summary>
    Task SetRestartPolicyAsync(ServerRecord record, RestartPolicyKind policy, CancellationToken cancellationToken);

    /// <summary>Removes the stopped container and its network. Server data is never touched.</summary>
    Task<ServerRecord> RemoveContainerAsync(ServerRecord record, Func<ServerRecord, Task> persist, CancellationToken cancellationToken);

    Task<IReadOnlyList<ServerContainerState>> ListAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Follows the container's log until the stream ends (container stopped, removed, daemon gone)
    /// or <paramref name="cancellationToken"/> fires. Lines are sanitized and <paramref name="secrets"/>
    /// are redacted. With <paramref name="since"/> (a raw Docker timestamp) only newer lines are sent,
    /// otherwise the last <paramref name="tail"/> lines first.
    /// </summary>
    Task StreamLogsAsync(
        ServerRecord record,
        string? since,
        int tail,
        IReadOnlyList<string> secrets,
        Action<LogLine> onLine,
        CancellationToken cancellationToken);

    /// <summary>
    /// Sends one console command through rcon-cli inside the running container. The command is a
    /// single argument after "--" (never interpreted as options or by a shell). On timeout the
    /// outcome is Unknown — the command may have run — and it is never retried.
    /// </summary>
    Task<RconResult> ExecRconAsync(ServerRecord record, string command, IReadOnlyList<string> secrets, TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>Host ports published (or reserved by a stopped container) by any container on this daemon, not only Bonfire's.</summary>
    Task<IReadOnlyList<PublishedPort>> ListPublishedPortsAsync(CancellationToken cancellationToken);
}
