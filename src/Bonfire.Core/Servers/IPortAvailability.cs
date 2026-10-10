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

/// <summary>Advisory port checks; a start can still fail with PortInUse and must be handled.</summary>
public interface IPortAvailability
{
    Task<PortCheck> CheckAsync(PortExposure exposure, int port, Guid? ignoreServerId, CancellationToken cancellationToken);

    /// <summary>First port from <see cref="PortRules.SearchStart"/> that looks free (bounded search), or null.</summary>
    Task<int?> FindFreeAsync(PortExposure exposure, CancellationToken cancellationToken);
}
