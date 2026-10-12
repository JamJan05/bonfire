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
    [InlineData("10.8.0.1", 25565, "192.168.1.5", 25565, false)]
    [InlineData("fd00::1", 25565, "fd00:0:0:0:0:0:0:1", 25565, true)] // two spellings of one address
    [InlineData("fd00::1", 25565, "fd00::2", 25565, false)]
    public void AddressOverlapRules(string a, int pa, string b, int pb, bool overlaps) =>
        Assert.Equal(overlaps, PortRules.Overlaps(a, pa, b, pb));

    [Theory]
    [InlineData(PortExposure.Localhost, null, "127.0.0.1")]
    [InlineData(PortExposure.AllInterfaces, null, "0.0.0.0")]
    [InlineData(PortExposure.SpecificAddress, "10.8.0.1", "10.8.0.1")]
    public void HostIpFollowsTheExposure(PortExposure exposure, string? address, string expected) =>
        Assert.Equal(expected, PortRules.HostIp(exposure, address));

    [Fact]
    public void UnknownExposureNeverBecomesAWildcard()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PortRules.HostIp((PortExposure)99, null));
        Assert.Throws<ArgumentException>(() => PortRules.HostIp(PortExposure.SpecificAddress, null));
    }

    [Fact]
    public void EndpointsBracketIpv6()
    {
        var v4 = ServerTestData.Vanilla() with { Exposure = PortExposure.SpecificAddress, HostAddress = "10.8.0.1", Port = 25565 };
        var v6 = v4 with { HostAddress = "fd00::1" };
        Assert.Equal("10.8.0.1:25565", PortRules.Endpoint(v4));
        Assert.Equal("[fd00::1]:25565", PortRules.Endpoint(v6));
    }

    [Fact]
    public async Task AnotherBonfireServerOnTheSamePortIsDetected()
    {
        var port = FreePort();
        await _registry.CreateAsync(ServerRecord.New(ServerTestData.Vanilla() with { Port = port, Exposure = PortExposure.AllInterfaces }), Ct);

        Assert.Equal(PortCheck.UsedByBonfireServer, await _ports.CheckAsync("127.0.0.1", port, PortCheckScope.All, Ct));
    }

    [Fact]
    public async Task TheEditedServersOwnRecordIsIgnored()
    {
        var port = FreePort();
        var record = ServerRecord.New(ServerTestData.Vanilla() with { Port = port, Exposure = PortExposure.AllInterfaces });
        await _registry.CreateAsync(record, Ct);

        Assert.Equal(PortCheck.Free, await _ports.CheckAsync("127.0.0.1", port, new PortCheckScope(record.Definition.Id), Ct));
    }

    [Fact]
    public async Task ContainerPublishingAnOverlappingPortIsDetected()
    {
        var port = FreePort();
        _runtime.PublishedPorts.Add(new PublishedPort("other", new PortBinding(80, "tcp", "0.0.0.0", port)));

        Assert.Equal(PortCheck.UsedByContainer, await _ports.CheckAsync("127.0.0.1", port, PortCheckScope.All, Ct));
    }

    [Fact]
    public async Task OnlyTheEditedServersOwnContainerIsIgnored()
    {
        var port = FreePort();
        _runtime.PublishedPorts.Add(new PublishedPort("mine", new PortBinding(25565, "tcp", "10.8.0.1", port)));
        _runtime.PublishedPorts.Add(new PublishedPort("other", new PortBinding(80, "tcp", "10.8.0.1", port)));

        Assert.Equal(PortCheck.UsedByContainer, await _ports.CheckAsync("10.8.0.1", port, new PortCheckScope(IgnoreContainerId: "mine"), Ct));
        _runtime.PublishedPorts.RemoveAll(p => p.ContainerId == "other");
        Assert.Equal(PortCheck.UsedByContainer, await _ports.CheckAsync("10.8.0.1", port, PortCheckScope.All, Ct));
        // Nothing else holds it; the host probe cannot bind 10.8.0.1 here, which is "unknown", not "used".
        Assert.NotEqual(PortCheck.UsedByContainer, await _ports.CheckAsync("10.8.0.1", port, new PortCheckScope(IgnoreContainerId: "mine"), Ct));
    }

    [Fact]
    public async Task HostListenerIsDetected()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        Assert.Equal(PortCheck.UsedByHost, await _ports.CheckAsync("127.0.0.1", port, PortCheckScope.All, Ct));
    }

    [Fact]
    public async Task HostProbeCanBeSkippedForAnOverlappingReplacement()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        Assert.Equal(PortCheck.Free, await _ports.CheckAsync("127.0.0.1", port, new PortCheckScope(ProbeHost: false), Ct));
    }

    [Fact]
    public async Task UnreachableDockerMakesTheResultUnknownNotFree()
    {
        _runtime.FailPortListing = true;

        Assert.Equal(PortCheck.Unknown, await _ports.CheckAsync("127.0.0.1", FreePort(), PortCheckScope.All, Ct));
    }

    [Fact]
    public async Task FreePortIsFree() =>
        Assert.Equal(PortCheck.Free, await _ports.CheckAsync("127.0.0.1", FreePort(), PortCheckScope.All, Ct));

    [Fact]
    public async Task AnAddressThisHostDoesNotHaveIsUnknownNotFree() =>
        Assert.Equal(PortCheck.Unknown, await _ports.CheckAsync("192.0.2.1", FreePort(), PortCheckScope.All, Ct));
}
