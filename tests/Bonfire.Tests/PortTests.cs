using System.Net;
using System.Net.Sockets;
using Bonfire.Core;
using Bonfire.Core.Servers;
using Bonfire.Infrastructure.Servers;

namespace Bonfire.Tests;

public sealed class PortTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly ServerRegistry _registry;
    private readonly FakeServerRuntime _runtime = new();
    private readonly PortAvailability _ports;

    public PortTests()
    {
        var app = AppPaths.Resolve(k => k == "HOME" ? _temp.Path : null);
        Directory.CreateDirectory(app.DataDir);
        _registry = new ServerRegistry(app);
        _ports = new PortAvailability(_registry, _runtime);
    }

    public void Dispose() => _temp.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static int FreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    [Theory]
    [InlineData("127.0.0.1", 25565, "127.0.0.1", 25565, true)]
    [InlineData("127.0.0.1", 25565, "0.0.0.0", 25565, true)]
    [InlineData("0.0.0.0", 25565, "192.168.1.5", 25565, true)]
    [InlineData("127.0.0.1", 25565, "", 25565, true)]
    [InlineData("127.0.0.1", 25565, "::", 25565, true)]
    [InlineData("127.0.0.1", 25565, "192.168.1.5", 25565, false)]
    [InlineData("0.0.0.0", 25565, "0.0.0.0", 25566, false)]
    public void AddressOverlapRules(string a, int pa, string b, int pb, bool overlaps) =>
        Assert.Equal(overlaps, PortRules.Overlaps(a, pa, b, pb));

    [Fact]
    public async Task AnotherBonfireServerOnTheSamePortIsDetected()
    {
        var port = FreePort();
        await _registry.CreateAsync(ServerRecord.New(ServerTestData.Vanilla() with { Port = port, Exposure = PortExposure.AllInterfaces }), Ct);

        Assert.Equal(PortCheck.UsedByBonfireServer, await _ports.CheckAsync(PortExposure.Localhost, port, null, Ct));
    }

    [Fact]
    public async Task ContainerPublishingAnOverlappingPortIsDetected()
    {
        var port = FreePort();
        _runtime.PublishedPorts.Add(new PortBinding(80, "tcp", "0.0.0.0", port));

        Assert.Equal(PortCheck.UsedByContainer, await _ports.CheckAsync(PortExposure.Localhost, port, null, Ct));
    }

    [Fact]
    public async Task HostListenerIsDetected()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        Assert.Equal(PortCheck.UsedByHost, await _ports.CheckAsync(PortExposure.Localhost, port, null, Ct));
    }

    [Fact]
    public async Task UnreachableDockerMakesTheResultUnknownNotFree()
    {
        _runtime.FailPortListing = true;

        Assert.Equal(PortCheck.Unknown, await _ports.CheckAsync(PortExposure.Localhost, FreePort(), null, Ct));
    }

    [Fact]
    public async Task FreePortIsFree() =>
        Assert.Equal(PortCheck.Free, await _ports.CheckAsync(PortExposure.Localhost, FreePort(), null, Ct));
}
