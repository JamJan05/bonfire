using System.Net;
using System.Net.Sockets;
using Bonfire.Core;
using Bonfire.Core.Docker;
using Bonfire.Core.Servers;
using Bonfire.Infrastructure.Docker;
using Bonfire.Infrastructure.Linux;
using Bonfire.Infrastructure.Servers;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bonfire.Tests;

/// <summary>
/// A server published on one host address against the real daemon. Opt-in:
/// BONFIRE_DOCKER_LIFECYCLE_TESTS=1 (downloads the image, accepts the EULA). Needs a non-loopback
/// IPv4 address on the host; the VPN-down path (address added after dockerd started, then removed)
/// needs root and is documented in docs/design/client-modpack.md instead.
/// </summary>
public sealed class RealNetworkExposureTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static int FreePort(IPAddress address)
    {
        using var listener = new TcpListener(address, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task<bool> ConnectsAsync(string address, int port)
    {
        using var client = new TcpClient();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await client.ConnectAsync(IPAddress.Parse(address), port, timeout.Token);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            return false;
        }
    }

    [Fact]
    public async Task ServerOnASpecificAddressIsReachableOnlyThere()
    {
        if (Environment.GetEnvironmentVariable("BONFIRE_DOCKER_LIFECYCLE_TESTS") != "1")
        {
            Assert.Skip("Set BONFIRE_DOCKER_LIFECYCLE_TESTS=1 to run real server lifecycle tests (downloads, EULA).");
        }

        var hosts = new HostAddresses();
        var lan = HostAddressRules.Suggestions(hosts.Read()).FirstOrDefault(s => s.Family == AddressFamily.InterNetwork);
        if (lan is null)
        {
            Assert.Skip("No non-loopback IPv4 address on this host.");
        }

        using var temp = new TempDirectory();
        var app = AppPaths.Resolve(k => k == "HOME" ? temp.Path : null);
        Directory.CreateDirectory(app.DataDir);
        var registry = new ServerRegistry(app);
        var installation = Guid.NewGuid();
        var endpoint = DockerEndpoint.Resolve(Environment.GetEnvironmentVariable)!;
        using var connection = new DockerConnection(endpoint);
        var runtime = new DockerServerRuntime(
            connection, d => registry.PathsFor(d.Id), installation, HostUser.Uid, HostUser.Gid, hosts,
            NullLogger<DockerServerRuntime>.Instance);

        try
        {
            var image = await runtime.AcquireImageAsync("java25", progress: null, Ct);
            var port = FreePort(IPAddress.Parse(lan.Address));
            var definition = ServerTestData.Vanilla() with
            {
                Image = image.Reference, Port = port, Exposure = PortExposure.SpecificAddress, HostAddress = lan.Address,
                RestartPolicy = Bonfire.Core.Servers.RestartPolicyKind.No,
            };
            var record = ServerRecord.New(definition);
            await registry.CreateAsync(record, Ct);
            Task Persist(ServerRecord r)
            {
                record = r;
                return registry.SaveAsync(r, Ct);
            }

            record = await runtime.ProvisionAsync(record, Persist, Ct);
            await runtime.StartAsync(record, Ct); // includes the effective-publication check

            // A connection from the host to its own address is DNATed straight into the container (nat OUTPUT
            // chain), so it is refused until the game server listens: wait for the health check first.
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(5);
            while ((await runtime.ListAsync(Ct)).Single(s => s.ServerId == definition.Id).Health != ContainerHealth.Healthy)
            {
                Assert.True(DateTimeOffset.UtcNow < deadline, "the server did not become healthy in time");
                await Task.Delay(TimeSpan.FromSeconds(5), Ct);
            }

            Assert.True(await ConnectsAsync(lan.Address, port), $"{lan.Address}:{port} should accept connections");
            Assert.False(await ConnectsAsync("127.0.0.1", port), $"127.0.0.1:{port} must not be published");

            var client = await connection.GetClientAsync(Ct);
            var inspect = await client.Containers.InspectContainerAsync(record.ContainerId!, Ct);
            var effective = Assert.Single(ContainerMapping.ToEffectivePorts(inspect));
            Assert.Equal(new Bonfire.Core.Servers.PortBinding(25565, "tcp", lan.Address, port), effective);

            await runtime.StopAsync(record, Ct);

            // An address nobody has: refused before Docker is asked, the container stays stopped.
            var absent = record with { Definition = definition with { HostAddress = "192.0.2.1" } };
            await registry.SaveAsync(absent, Ct);
            await runtime.ProvisionAsync(absent with { Phase = ProvisioningPhase.Provisioning }, Persist, Ct); // recreates with the new binding
            var ex = await Assert.ThrowsAsync<ServerOperationException>(() => runtime.StartAsync(record, Ct));
            Assert.Equal(ServerOperationError.AddressNotPresent, ex.Error);
            var state = (await runtime.ListAsync(Ct)).Single(s => s.ServerId == definition.Id);
            Assert.False(state.IsRunning);
        }
        finally
        {
            await CleanupAsync(connection, installation);
        }
    }

    private static async Task CleanupAsync(DockerConnection connection, Guid installation)
    {
        var client = await connection.GetClientAsync(CancellationToken.None);
        var filter = new Dictionary<string, IDictionary<string, bool>>
        {
            ["label"] = new Dictionary<string, bool> { [$"{ServerLabels.Installation}={installation:N}"] = true },
        };
        foreach (var c in await client.Containers.ListContainersAsync(new ContainersListParameters { All = true, Filters = filter }))
        {
            await client.Containers.RemoveContainerAsync(c.ID, new ContainerRemoveParameters { Force = true });
        }

        foreach (var n in await client.Networks.ListNetworksAsync(new NetworksListParameters { Filters = filter }))
        {
            await client.Networks.DeleteNetworkAsync(n.ID);
        }
    }
}
