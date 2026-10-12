namespace Bonfire.Core.Servers;

public enum PortCheck
{
    Free,

    /// <summary>Another Bonfire server is configured for an overlapping address and port.</summary>
    UsedByBonfireServer,

    /// <summary>A Docker container already publishes an overlapping address and port.</summary>
    UsedByContainer,

    /// <summary>Some program on the host is listening there.</summary>
    UsedByHost,

    /// <summary>The check itself failed (e.g. Docker unreachable); the port may or may not be free.</summary>
    Unknown,
}

/// <summary>
/// What a port check may ignore because it belongs to the server being edited: its own record
/// and its own container. <see cref="ProbeHost"/> is false when the new binding overlaps the
/// server's current one — its own container's listener would otherwise report a false conflict;
/// a host program that took the port is then still caught by Docker at the next start.
/// </summary>
public sealed record PortCheckScope(Guid? IgnoreServerId = null, string? IgnoreContainerId = null, bool ProbeHost = true)
{
    public static readonly PortCheckScope All = new();
}

/// <summary>Advisory port checks; a start can still fail with PortInUse and must be handled.</summary>
public interface IPortAvailability
{
    /// <param name="hostIp">The binding's host address (127.0.0.1, 0.0.0.0 or a specific address).</param>
    Task<PortCheck> CheckAsync(string hostIp, int port, PortCheckScope scope, CancellationToken cancellationToken);

    /// <summary>First port from <see cref="PortRules.SearchStart"/> that looks free (bounded search), or null.</summary>
    Task<int?> FindFreeAsync(string hostIp, CancellationToken cancellationToken);
}
