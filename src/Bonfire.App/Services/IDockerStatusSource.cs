using Bonfire.Core.Docker;

namespace Bonfire.App.Services;

/// <summary>Latest Docker probe result, used to decide which server operations are allowed.</summary>
public interface IDockerStatusSource
{
    /// <summary>Last probe result (connection state).</summary>
    DockerProbeResult? Current { get; }

    /// <summary>
    /// False after a Docker call failed following the last probe: capabilities may have changed,
    /// so creating or starting is blocked until the next probe. Containment is not affected.
    /// </summary>
    bool CapabilitiesTrusted { get; }
}
