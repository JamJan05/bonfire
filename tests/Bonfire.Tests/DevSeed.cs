using System.Net;
using System.Net.Sockets;
using Bonfire.Core;
using Bonfire.Core.Docker;
using Bonfire.Core.Servers;
using Bonfire.Infrastructure;
using Bonfire.Infrastructure.Docker;
using Bonfire.Infrastructure.Linux;
using Bonfire.Infrastructure.Servers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bonfire.Tests;

/// <summary>
/// Developer tool, not a test: creates a real (not started) Vanilla server for the Bonfire
/// installation under HOME=$BONFIRE_DEV_SEED_HOME, so the GUI can be tried before the wizard
/// exists. Leaves the container in place. Run explicitly:
/// <c>BONFIRE_DEV_SEED_HOME=/some/dir dotnet test --filter-method "*DevSeed*"</c>
/// </summary>
public sealed class DevSeed
{
    [Fact]
    public async Task CreateServerForManualTesting()
    {
        var home = Environment.GetEnvironmentVariable("BONFIRE_DEV_SEED_HOME");
        if (string.IsNullOrEmpty(home))
        {
            Assert.Skip("Developer tool: set BONFIRE_DEV_SEED_HOME to create a server for manual GUI testing.");
        }

        var ct = TestContext.Current.CancellationToken;
        var app = AppPaths.Resolve(k => k == "HOME" ? home : null);
        AppDirectories.Ensure(app);
        var installation = await InstallationIdentity.LoadOrCreateAsync(app, ct);
        var registry = new ServerRegistry(app);
        using var connection = new DockerConnection(DockerEndpoint.Resolve(Environment.GetEnvironmentVariable)!);
        var runtime = new DockerServerRuntime(
            connection, d => registry.PathsFor(d.Id), installation, HostUser.Uid, HostUser.Gid,
            NullLogger<DockerServerRuntime>.Instance);

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var image = await runtime.AcquireImageAsync("java25", null, ct);
        var record = ServerRecord.New(ServerTestData.Vanilla() with
        {
            DisplayName = "Dev world", Image = image.Reference, Port = port,
        });
        await registry.CreateAsync(record, ct);
        await runtime.ProvisionAsync(record, r => registry.SaveAsync(r, ct), ct);
    }
}
