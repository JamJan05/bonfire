using Bonfire.Core.Docker;

namespace Bonfire.Tests;

/// <summary>A probe that always reports a safe, connected daemon (for view-model and headless tests).</summary>
internal sealed class HeadlessConnectedEngine : IDockerEngine
{
    public Task<DockerProbeResult> ProbeAsync(CancellationToken cancellationToken) => Task.FromResult(new DockerProbeResult(
        DockerConnectionStatus.Connected, new DockerEndpoint("/var/run/docker.sock"), DateTimeOffset.UtcNow,
        new DockerDaemonInfo("29.8.0", new Version(1, 40), new Version(1, 56), new Version(1, 56), null, null, null),
        new DockerCapabilities(false, true, "builtin", "2", "cgroupfs")));
}
