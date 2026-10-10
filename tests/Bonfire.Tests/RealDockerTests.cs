using Bonfire.Core.Docker;
using Bonfire.Infrastructure.Docker;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bonfire.Tests;

/// <summary>
/// Read-only checks against the real local daemon. Opt-in: BONFIRE_DOCKER_TESTS=1.
/// They never pull images or create containers, networks or volumes.
/// </summary>
public sealed class RealDockerTests
{
    [Fact]
    public async Task ProbesLocalDaemon()
    {
        if (Environment.GetEnvironmentVariable("BONFIRE_DOCKER_TESTS") != "1")
        {
            Assert.Skip("Set BONFIRE_DOCKER_TESTS=1 to run tests against the local Docker daemon.");
        }

        var engine = new DockerEngine(
            DockerEndpoint.Resolve(Environment.GetEnvironmentVariable),
            TimeProvider.System,
            NullLogger<DockerEngine>.Instance);

        var result = await engine.ProbeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DockerConnectionStatus.Connected, result.Status);
        Assert.NotNull(result.Daemon!.NegotiatedApiVersion);
        Assert.NotNull(result.Capabilities);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"Docker {result.Daemon.ServerVersion}, API {result.Daemon.NegotiatedApiVersion}, " +
            $"seccomp={result.Capabilities!.Seccomp} ({result.Capabilities.SeccompProfile}), " +
            $"rootless={result.Capabilities.Rootless}, cgroup v{result.Capabilities.CgroupVersion}");
    }
}
