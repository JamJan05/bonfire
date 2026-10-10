using System.Net.Sockets;
using Bonfire.Core.Docker;
using Bonfire.Infrastructure.Docker;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bonfire.Tests;

/// <summary>Adapter tests against a fake daemon on a real Unix socket — no Docker required.</summary>
public sealed class DockerEngineTests
{
    private static DockerEngine Engine(string socketPath, TimeSpan? deadline = null) =>
        new(new DockerEndpoint(socketPath), TimeProvider.System, NullLogger<DockerEngine>.Instance, deadline);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task HealthyDaemonIsConnectedWithNegotiatedVersionAndCapabilities()
    {
        using var temp = new TempDirectory();
        await using var daemon = new FakeDockerDaemon(Path.Combine(temp.Path, "docker.sock"), FakeDockerDaemon.Healthy);

        var result = await Engine(daemon.SocketPath).ProbeAsync(Ct);

        Assert.Equal(DockerConnectionStatus.Connected, result.Status);
        Assert.Equal("29.8.0", result.Daemon!.ServerVersion);
        Assert.Equal(new Version(1, 56), result.Daemon.NegotiatedApiVersion);
        Assert.Equal(true, result.Capabilities!.Seccomp);
        Assert.Equal("builtin", result.Capabilities.SeccompProfile);
        Assert.Equal(false, result.Capabilities.Rootless);
        Assert.Equal("2", result.Capabilities.CgroupVersion);
        Assert.False(result.RootlessUnsupported);

        // /version unversioned (negotiation), /info on the negotiated version.
        Assert.Contains("/version", daemon.RequestedPaths);
        Assert.Contains("/v1.56/info", daemon.RequestedPaths);
    }

    [Fact]
    public async Task NewerDaemonIsCappedAtBonfireMaximum()
    {
        using var temp = new TempDirectory();
        await using var daemon = new FakeDockerDaemon(Path.Combine(temp.Path, "docker.sock"), path =>
            path.EndsWith("/version", StringComparison.Ordinal)
                ? (200, """{"Version":"40.0.0","ApiVersion":"1.70","MinAPIVersion":"1.44"}""")
                : FakeDockerDaemon.Healthy(path));

        var result = await Engine(daemon.SocketPath).ProbeAsync(Ct);

        Assert.Equal(DockerApiVersion.ClientMaximum, result.Daemon!.NegotiatedApiVersion);
    }

    [Theory]
    [InlineData("1.40", "1.43")] // too old for Bonfire
    [InlineData("1.60", "1.70")] // dropped everything Bonfire knows
    public async Task NonOverlappingApiRangesAreIncompatible(string min, string max)
    {
        using var temp = new TempDirectory();
        await using var daemon = new FakeDockerDaemon(Path.Combine(temp.Path, "docker.sock"), path =>
            (200, $$"""{"Version":"x","ApiVersion":"{{max}}","MinAPIVersion":"{{min}}"}"""));

        var result = await Engine(daemon.SocketPath).ProbeAsync(Ct);

        Assert.Equal(DockerConnectionStatus.ApiIncompatible, result.Status);
        Assert.Null(result.Daemon!.NegotiatedApiVersion);
    }

    [Fact]
    public async Task FailingInfoKeepsConnectionButLeavesCapabilitiesUnknown()
    {
        using var temp = new TempDirectory();
        await using var daemon = new FakeDockerDaemon(Path.Combine(temp.Path, "docker.sock"), path =>
            path.EndsWith("/info", StringComparison.Ordinal) ? (500, """{"message":"boom"}""") : FakeDockerDaemon.Healthy(path));

        var result = await Engine(daemon.SocketPath).ProbeAsync(Ct);

        Assert.Equal(DockerConnectionStatus.Connected, result.Status);
        Assert.Null(result.Capabilities);
        Assert.True(result.CapabilitiesUnknown);
        Assert.False(result.SeccompMissing, "Unknown must not be reported as missing.");
        Assert.NotNull(result.TechnicalDetail);
    }

    [Fact]
    public async Task RootlessDaemonIsConnectedButFlagged()
    {
        using var temp = new TempDirectory();
        await using var daemon = new FakeDockerDaemon(Path.Combine(temp.Path, "docker.sock"), path =>
            path.EndsWith("/info", StringComparison.Ordinal)
                ? (200, """{"SecurityOptions":["name=seccomp,profile=builtin","name=rootless"]}""")
                : FakeDockerDaemon.Healthy(path));

        var result = await Engine(daemon.SocketPath).ProbeAsync(Ct);

        Assert.Equal(DockerConnectionStatus.Connected, result.Status);
        Assert.True(result.RootlessUnsupported);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public async Task AuthorizationRejectionIsApiAccessDenied(int status)
    {
        using var temp = new TempDirectory();
        await using var daemon = new FakeDockerDaemon(Path.Combine(temp.Path, "docker.sock"), _ => (status, """{"message":"denied"}"""));

        var result = await Engine(daemon.SocketPath).ProbeAsync(Ct);

        Assert.Equal(DockerConnectionStatus.ApiAccessDenied, result.Status);
    }

    [Fact]
    public async Task MissingSocketIsReported()
    {
        using var temp = new TempDirectory();

        var result = await Engine(Path.Combine(temp.Path, "nope.sock")).ProbeAsync(Ct);

        Assert.Equal(DockerConnectionStatus.SocketMissing, result.Status);
    }

    [Fact]
    public async Task SocketWithoutListenerIsDaemonNotResponding()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "docker.sock");
        // Bound but not listening: connect() fails with ECONNREFUSED, like a crashed daemon's stale socket.
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        socket.Bind(new UnixDomainSocketEndPoint(path));

        var result = await Engine(path).ProbeAsync(Ct);

        Assert.Equal(DockerConnectionStatus.DaemonNotResponding, result.Status);
    }

    [Fact]
    public async Task InaccessibleSocketIsPermissionDenied()
    {
        using var temp = new TempDirectory();
        await using var daemon = new FakeDockerDaemon(Path.Combine(temp.Path, "docker.sock"), FakeDockerDaemon.Healthy);
        File.SetUnixFileMode(daemon.SocketPath, UnixFileMode.None);

        var result = await Engine(daemon.SocketPath).ProbeAsync(Ct);

        Assert.Equal(DockerConnectionStatus.PermissionDenied, result.Status);
    }

    [Fact]
    public async Task SilentDaemonTimesOut()
    {
        using var temp = new TempDirectory();
        await using var daemon = new FakeDockerDaemon(Path.Combine(temp.Path, "docker.sock"), _ => null);

        var result = await Engine(daemon.SocketPath, TimeSpan.FromMilliseconds(300)).ProbeAsync(Ct);

        Assert.Equal(DockerConnectionStatus.Timeout, result.Status);
    }

    [Fact]
    public async Task CallerCancellationIsNotATimeout()
    {
        using var temp = new TempDirectory();
        await using var daemon = new FakeDockerDaemon(Path.Combine(temp.Path, "docker.sock"), _ => null);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(200);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Engine(daemon.SocketPath, TimeSpan.FromSeconds(30)).ProbeAsync(cts.Token));
    }

    [Fact]
    public async Task UnsupportedEndpointIsReportedWithoutConnecting()
    {
        var engine = new DockerEngine(null, TimeProvider.System, NullLogger<DockerEngine>.Instance);

        var result = await engine.ProbeAsync(Ct);

        Assert.Equal(DockerConnectionStatus.UnsupportedEndpoint, result.Status);
        Assert.Null(result.Endpoint);
    }
}
