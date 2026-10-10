namespace Bonfire.Core.Docker;

public interface IDockerEngine
{
    /// <summary>
    /// Checks the connection to the Docker Engine. Never throws for connection problems — they
    /// are reported in the result; throws <see cref="OperationCanceledException"/> only when
    /// <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    Task<DockerProbeResult> ProbeAsync(CancellationToken cancellationToken);
}
