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
/// Full lifecycle against the real Docker daemon. Opt-in: BONFIRE_DOCKER_LIFECYCLE_TESTS=1.
/// Downloads the itzg image and a Minecraft server, accepts the Minecraft EULA (the user agreed
/// to this for development), uses ~1.5 GB RAM for a few minutes. Every resource is labelled with
/// a fresh installation id; cleanup only touches that id.
/// </summary>
public sealed class ServerLifecycleTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    [Theory]
    [InlineData(ServerType.Vanilla)]
    [InlineData(ServerType.Fabric)]
    [InlineData(ServerType.Paper)]
    public async Task ServerFullLifecycle(ServerType type)
    {
        if (Environment.GetEnvironmentVariable("BONFIRE_DOCKER_LIFECYCLE_TESTS") != "1")
        {
            Assert.Skip("Set BONFIRE_DOCKER_LIFECYCLE_TESTS=1 to run real server lifecycle tests (downloads, EULA).");
        }

        using var temp = new TempDirectory();
        var app = AppPaths.Resolve(k => k == "HOME" ? temp.Path : null);
        Directory.CreateDirectory(app.DataDir);
        var registry = new ServerRegistry(app);
        var installation = Guid.NewGuid();
        var endpoint = DockerEndpoint.Resolve(Environment.GetEnvironmentVariable)!;
        using var connection = new DockerConnection(endpoint);
        var runtime = new DockerServerRuntime(
            connection, d => registry.PathsFor(d.Id), installation, HostUser.Uid, HostUser.Gid, new HostAddresses(),
            NullLogger<DockerServerRuntime>.Instance);

        try
        {
            var image = await runtime.AcquireImageAsync("java25", progress: null, Ct);
            Assert.Matches("^sha256:[0-9a-f]{64}$", image.Reference.Digest);

            var definition = type switch
            {
                ServerType.Fabric => ServerTestData.Vanilla() with
                {
                    Type = ServerType.Fabric, FabricLoaderVersion = "0.19.5", FabricLauncherVersion = "1.1.2",
                },
                ServerType.Paper => ServerTestData.Vanilla() with
                {
                    Type = ServerType.Paper, PaperBuild = 166, PaperExperimentalBuild = true,
                },
                _ => ServerTestData.Vanilla(),
            } with { Image = image.Reference, Port = FreePort() };
            var record = ServerRecord.New(definition);
            await registry.CreateAsync(record, Ct);
            Task Persist(ServerRecord r)
            {
                record = r;
                return registry.SaveAsync(r, Ct);
            }

            record = await runtime.ProvisionAsync(record, Persist, Ct);
            Assert.Equal(ProvisioningPhase.Ready, record.Phase);
            var containerId = record.ContainerId!;

            // Interrupted creation ("created, response lost"): ids forgotten, phase back to Provisioning.
            var lost = record with { Phase = ProvisioningPhase.Provisioning, ContainerId = null, NetworkId = null };
            record = await runtime.ProvisionAsync(lost, Persist, Ct);
            Assert.Equal(containerId, record.ContainerId); // adopted, not duplicated

            await runtime.StartAsync(record, Ct);
            await WaitForHealthAsync(runtime, definition.Id, ContainerHealth.Healthy, TimeSpan.FromMinutes(5));

            // Live log: the startup lines are there, the RCON secret never is.
            var secret = registry.ReadRconSecret(definition.Id)!;
            var logLines = new List<Bonfire.Core.Logs.LogLine>();
            using (var logCts = CancellationTokenSource.CreateLinkedTokenSource(Ct))
            {
                logCts.CancelAfter(TimeSpan.FromSeconds(5));
                try
                {
                    await runtime.StreamLogsAsync(record, null, 500, [secret], l => { lock (logLines) { logLines.Add(l); } }, logCts.Token);
                }
                catch (OperationCanceledException)
                {
                }
            }

            Assert.Contains(logLines, l => l.Text.Contains("Done (", StringComparison.Ordinal));
            Assert.All(logLines, l => Assert.DoesNotContain(secret, l.Text));
            Assert.All(logLines, l => Assert.NotNull(l.RawTimestamp));

            // RCON through exec: parsed player list, and option-like text stays a server command.
            var list = await runtime.ExecRconAsync(record, "list", [secret], TimeSpan.FromSeconds(10), Ct);
            Assert.Equal(Bonfire.Core.Rcon.RconOutcome.Completed, list.Outcome);
            Assert.Equal(0, Bonfire.Core.Rcon.RconCommands.ParseList(list.Output).Online);
            var injected = await runtime.ExecRconAsync(record, "--host evil list", [secret], TimeSpan.FromSeconds(10), Ct);
            Assert.Equal(0, injected.ExitCode);
            Assert.DoesNotContain("players online", injected.Output); // not executed as "list" against another host

            // A second mutation while one is running fails fast instead of interleaving.
            var stopping = runtime.StopAsync(record, Ct);
            var busy = await Assert.ThrowsAsync<ServerOperationException>(() => runtime.StartAsync(record, Ct));
            Assert.Equal(ServerOperationError.Busy, busy.Error);
            await stopping;

            var stopped = Assert.Single(await runtime.ListAsync(Ct));
            Assert.False(stopped.IsRunning);
            Assert.Equal(0, stopped.ExitCode); // graceful: the world was saved

            // Data belongs to the host user and survives restart.
            var paths = registry.PathsFor(definition.Id);
            Assert.True(File.Exists(Path.Combine(paths.DataDir, "server.properties")));
            Assert.True(Directory.Exists(Path.Combine(paths.DataDir, "world")));

            await runtime.StartAsync(record, Ct);
            await WaitForHealthAsync(runtime, definition.Id, ContainerHealth.Healthy, TimeSpan.FromMinutes(3));

            // Force stop escalates a pending stop instead of being rejected as Busy.
            var slowStop = runtime.StopAsync(record, Ct);
            await runtime.KillAsync(record, Ct);
            await slowStop;
            var killed = Assert.Single(await runtime.ListAsync(Ct));
            Assert.False(killed.IsRunning);

            record = await runtime.RemoveContainerAsync(record, Persist, Ct);
            Assert.Null(record.ContainerId);
            Assert.Empty(await runtime.ListAsync(Ct));
            Assert.True(Directory.Exists(Path.Combine(paths.DataDir, "world")), "Removing the container must keep the data.");
        }
        finally
        {
            await CleanupAsync(endpoint, installation);
        }
    }

    /// <summary>The whole wizard path: real metadata → creation service → healthy server. Also needs the network.</summary>
    [Theory]
    [InlineData(ServerType.Vanilla)]
    [InlineData(ServerType.Fabric)]
    [InlineData(ServerType.Paper)]
    public async Task CreatesAndStartsServerFromRealMetadata(ServerType type)
    {
        if (Environment.GetEnvironmentVariable("BONFIRE_DOCKER_LIFECYCLE_TESTS") != "1")
        {
            Assert.Skip("Set BONFIRE_DOCKER_LIFECYCLE_TESTS=1 to run real server lifecycle tests (downloads, EULA).");
        }

        using var temp = new TempDirectory();
        var app = AppPaths.Resolve(k => k == "HOME" ? temp.Path : null);
        Directory.CreateDirectory(app.DataDir);
        var registry = new ServerRegistry(app);
        var installation = Guid.NewGuid();
        var endpoint = DockerEndpoint.Resolve(Environment.GetEnvironmentVariable)!;
        using var connection = new DockerConnection(endpoint);
        var runtime = new DockerServerRuntime(
            connection, d => registry.PathsFor(d.Id), installation, HostUser.Uid, HostUser.Gid, new HostAddresses(),
            NullLogger<DockerServerRuntime>.Instance);
        using var http = new Bonfire.Infrastructure.Metadata.MetadataHttpClient();
        var metadata = new Bonfire.Infrastructure.Metadata.ServerMetadataService(http);
        var docker = new ServerCreationTests.SafeDocker();
        var operations = new Bonfire.App.Services.ServerOperationsService(registry, runtime, docker, NullLogger<Bonfire.App.Services.ServerOperationsService>.Instance);
        var creation = new Bonfire.App.Services.ServerCreationService(
            registry, runtime, operations, new PortAvailability(registry, runtime), TimeProvider.System,
            NullLogger<Bonfire.App.Services.ServerCreationService>.Instance);

        try
        {
            Bonfire.Core.Metadata.ResolvedServerVersion? resolved = null;
            foreach (var release in (await metadata.GetReleasesAsync(Ct)).Take(5))
            {
                try
                {
                    resolved = await metadata.ResolveAsync(type, release.Id, Ct);
                    break;
                }
                catch (Bonfire.Core.Metadata.MetadataException ex) when (ex.Error == Bonfire.Core.Metadata.MetadataError.Unsupported)
                {
                }
            }

            Assert.NotNull(resolved);
            var request = ServerCreationTests.Request() with { Version = resolved!, Port = FreePort() };
            var result = await creation.CreateAsync(request, null, Ct);

            Assert.True(result.Started, result.StartError?.Message);
            await WaitForHealthAsync(runtime, result.ServerId, ContainerHealth.Healthy, TimeSpan.FromMinutes(5));
            await operations.StopAsync(result.ServerId, Ct);
            Assert.Equal(0, (await runtime.ListAsync(Ct)).Single().ExitCode);
        }
        finally
        {
            await CleanupAsync(endpoint, installation);
        }
    }

    /// <summary>
    /// Settings change on a running Vanilla server: values reach server.properties after the
    /// container is recreated, and a player removed from the whitelist in game stays removed.
    /// </summary>
    [Fact]
    public async Task SettingsChangeAndWhitelistSeedSemantics()
    {
        if (Environment.GetEnvironmentVariable("BONFIRE_DOCKER_LIFECYCLE_TESTS") != "1")
        {
            Assert.Skip("Set BONFIRE_DOCKER_LIFECYCLE_TESTS=1 to run real server lifecycle tests (downloads, EULA).");
        }

        using var temp = new TempDirectory();
        var app = AppPaths.Resolve(k => k == "HOME" ? temp.Path : null);
        Directory.CreateDirectory(app.DataDir);
        var registry = new ServerRegistry(app);
        var installation = Guid.NewGuid();
        var endpoint = DockerEndpoint.Resolve(Environment.GetEnvironmentVariable)!;
        using var connection = new DockerConnection(endpoint);
        var runtime = new DockerServerRuntime(
            connection, d => registry.PathsFor(d.Id), installation, HostUser.Uid, HostUser.Gid, new HostAddresses(),
            NullLogger<DockerServerRuntime>.Instance);
        var operations = new Bonfire.App.Services.ServerOperationsService(
            registry, runtime, new ServerCreationTests.SafeDocker(), NullLogger<Bonfire.App.Services.ServerOperationsService>.Instance);

        try
        {
            var image = await runtime.AcquireImageAsync("java25", null, Ct);
            var definition = ServerTestData.Vanilla() with
            {
                Image = image.Reference,
                Port = FreePort(),
                Game = new GameSettings(true, Difficulty.Normal, 10, true, ["Notch", "jeb_"], null),
            };
            var record = ServerRecord.New(definition);
            var paths = await registry.CreateAsync(record, Ct);
            await runtime.ProvisionAsync(record, r => registry.SaveAsync(r, Ct), Ct);
            await operations.StartAsync(definition.Id, Ct);
            await WaitForHealthAsync(runtime, definition.Id, ContainerHealth.Healthy, TimeSpan.FromMinutes(5));

            var whitelistFile = Path.Combine(paths.DataDir, "whitelist.json");
            Assert.Contains("jeb_", await File.ReadAllTextAsync(whitelistFile, Ct));
            await operations.SendCommandAsync(definition.Id, "whitelist remove jeb_", Ct);
            await Task.Delay(TimeSpan.FromSeconds(2), Ct);
            Assert.DoesNotContain("jeb_", await File.ReadAllTextAsync(whitelistFile, Ct));

            var game = registry.Load(definition.Id)!.Record!.Definition.Game with { Motd = "Bonfire test", MaxPlayers = 7, Pvp = false };
            var result = await operations.ApplySettingsAsync(definition.Id, game, Ct);
            Assert.Equal(Bonfire.App.Services.SettingsApplyOutcome.AppliedAndRestarted, result.Outcome);
            await WaitForHealthAsync(runtime, definition.Id, ContainerHealth.Healthy, TimeSpan.FromMinutes(5));

            var props = ServerPropertiesFile.Read(paths)!;
            Assert.Equal("Bonfire test", props["motd"]);
            Assert.Equal("7", props["max-players"]);
            Assert.Equal("false", props["pvp"]);
            Assert.DoesNotContain("jeb_", await File.ReadAllTextAsync(whitelistFile, Ct)); // removal survived recreation
            Assert.Contains("Notch", await File.ReadAllTextAsync(whitelistFile, Ct));
            Assert.Null(registry.Load(definition.Id)!.Record!.PendingChange);

            await operations.StopAsync(definition.Id, Ct);
        }
        finally
        {
            await CleanupAsync(endpoint, installation);
        }
    }

    /// <summary>
    /// Backup and restore on a real server: after swapping the data directory of the stopped
    /// container, the next start must use the restored directory (bind mount resolved at start).
    /// </summary>
    [Fact]
    public async Task BackupAndRestoreOnARealServer()
    {
        if (Environment.GetEnvironmentVariable("BONFIRE_DOCKER_LIFECYCLE_TESTS") != "1")
        {
            Assert.Skip("Set BONFIRE_DOCKER_LIFECYCLE_TESTS=1 to run real server lifecycle tests (downloads, EULA).");
        }

        using var temp = new TempDirectory();
        var app = AppPaths.Resolve(k => k == "HOME" ? temp.Path : null);
        Bonfire.Infrastructure.AppDirectories.Ensure(app);
        var registry = new ServerRegistry(app);
        var installation = Guid.NewGuid();
        var endpoint = DockerEndpoint.Resolve(Environment.GetEnvironmentVariable)!;
        using var connection = new DockerConnection(endpoint);
        var runtime = new DockerServerRuntime(
            connection, d => registry.PathsFor(d.Id), installation, HostUser.Uid, HostUser.Gid, new HostAddresses(),
            NullLogger<DockerServerRuntime>.Instance);
        var restore = new Bonfire.App.Services.RestoreEngine(registry, runtime, TimeProvider.System, NullLogger<Bonfire.App.Services.RestoreEngine>.Instance);
        var operations = new Bonfire.App.Services.ServerOperationsService(
            registry, runtime, new ServerCreationTests.SafeDocker(), NullLogger<Bonfire.App.Services.ServerOperationsService>.Instance, restore);
        var store = new Bonfire.Infrastructure.Backups.BackupStore(app);
        var settingsStore = new Bonfire.Infrastructure.SettingsStore(app.SettingsFile);
        var backups = new Bonfire.App.Services.BackupService(registry, runtime, operations, store, restore,
            new Bonfire.App.Services.SettingsService(settingsStore, settingsStore.Load(), NullLogger<Bonfire.App.Services.SettingsService>.Instance),
            TimeProvider.System);

        try
        {
            var image = await runtime.AcquireImageAsync("java25", null, Ct);
            var definition = ServerTestData.Vanilla() with { Image = image.Reference, Port = FreePort(), RestartPolicy = Bonfire.Core.Servers.RestartPolicyKind.UnlessStopped };
            var record = ServerRecord.New(definition);
            var paths = await registry.CreateAsync(record, Ct);
            await runtime.ProvisionAsync(record, r => registry.SaveAsync(r, Ct), Ct);
            await operations.StartAsync(definition.Id, Ct);
            await WaitForHealthAsync(runtime, definition.Id, ContainerHealth.Healthy, TimeSpan.FromMinutes(5));

            // Running servers are not backed up (MVP).
            var running = await Assert.ThrowsAsync<Bonfire.Core.Backups.BackupException>(
                () => backups.CreateAsync(definition.Id, Bonfire.Core.Backups.BackupKind.Manual, null, Ct));
            Assert.Equal(Bonfire.Core.Backups.BackupError.ServerRunning, running.Error);

            await operations.StopAsync(definition.Id, Ct);
            var marker = Path.Combine(paths.DataDir, "bonfire-marker.txt");
            await File.WriteAllTextAsync(marker, "from-backup", Ct);
            var backup = await backups.CreateAsync(definition.Id, Bonfire.Core.Backups.BackupKind.Manual, null, Ct);
            Assert.True(backup.IsUsable);

            await File.WriteAllTextAsync(marker, "changed-later", Ct);
            File.Delete(Path.Combine(paths.DataDir, "world", "level.dat"));

            await backups.RestoreAsync(definition.Id, backup.BackupId, null, Ct);
            Assert.Equal("from-backup", await File.ReadAllTextAsync(marker, Ct));
            Assert.True(File.Exists(Path.Combine(paths.DataDir, "world", "level.dat")));
            var rollback = Assert.Single(Directory.EnumerateDirectories(paths.ServerDir, Bonfire.App.Services.RestoreEngine.RollbackPrefix + "*"),
                d => !d.EndsWith(".content", StringComparison.Ordinal));
            var rollbackLog = new FileInfo(Path.Combine(rollback, "logs", "latest.log")).LastWriteTimeUtc;

            var startedAt = DateTime.UtcNow;
            await operations.StartAsync(definition.Id, Ct); // verified container, restored policy
            await WaitForHealthAsync(runtime, definition.Id, ContainerHealth.Healthy, TimeSpan.FromMinutes(5));

            // The container writes into the restored directory, not into the rollback copy.
            Assert.True(new FileInfo(Path.Combine(paths.DataDir, "logs", "latest.log")).LastWriteTimeUtc >= startedAt.AddSeconds(-2));
            Assert.Equal(rollbackLog, new FileInfo(Path.Combine(rollback, "logs", "latest.log")).LastWriteTimeUtc);
            var list = await operations.QueryPlayersAsync(definition.Id, Ct); // RCON works with the kept secret
            Assert.Equal(0, list.Online);

            await operations.StopAsync(definition.Id, Ct);
        }
        finally
        {
            await CleanupAsync(endpoint, installation);
        }
    }

    [Fact]
    public async Task ModsFromModrinthOnARealFabricServer()
    {
        if (Environment.GetEnvironmentVariable("BONFIRE_DOCKER_LIFECYCLE_TESTS") != "1")
        {
            Assert.Skip("Set BONFIRE_DOCKER_LIFECYCLE_TESTS=1 to run real server lifecycle tests (downloads, EULA).");
        }

        using var temp = new TempDirectory();
        var app = AppPaths.Resolve(k => k == "HOME" ? temp.Path : null);
        Bonfire.Infrastructure.AppDirectories.Ensure(app);
        var registry = new ServerRegistry(app);
        var installation = Guid.NewGuid();
        var endpoint = DockerEndpoint.Resolve(Environment.GetEnvironmentVariable)!;
        using var connection = new DockerConnection(endpoint);
        var runtime = new DockerServerRuntime(
            connection, d => registry.PathsFor(d.Id), installation, HostUser.Uid, HostUser.Gid, new HostAddresses(),
            NullLogger<DockerServerRuntime>.Instance);
        var engine = new Bonfire.App.Services.ContentEngine(registry, runtime, NullLogger<Bonfire.App.Services.ContentEngine>.Instance);
        var operations = new Bonfire.App.Services.ServerOperationsService(
            registry, runtime, new ServerCreationTests.SafeDocker(), NullLogger<Bonfire.App.Services.ServerOperationsService>.Instance, null, engine);
        var content = new Bonfire.App.Services.ContentService(registry, operations, runtime, engine,
            new Bonfire.Infrastructure.Content.ModrinthClient(new Bonfire.Infrastructure.Metadata.MetadataHttpClient()),
            () => new Bonfire.Infrastructure.Content.ContentDownloader(), TimeProvider.System, NullLogger<Bonfire.App.Services.ContentService>.Instance);

        try
        {
            var image = await runtime.AcquireImageAsync("java25", null, Ct);
            var definition = ServerTestData.Vanilla() with
            {
                Type = ServerType.Fabric, FabricLoaderVersion = "0.19.5", FabricLauncherVersion = "1.1.2",
                Image = image.Reference, Port = FreePort(), Resources = new ResourceLimits(1024, 1536, 2),
            };
            var record = ServerRecord.New(definition);
            var paths = await registry.CreateAsync(record, Ct);
            await runtime.ProvisionAsync(record, r => registry.SaveAsync(r, Ct), Ct);

            // Chunky requires Fabric API: the plan must add it.
            var chunky = await content.PlanInstallAsync(definition.Id, "chunky", null, Ct);
            Assert.True(chunky.CanInstall, string.Join(", ", chunky.Problems));
            Assert.Contains(chunky.Items, i => i.IsDependency && i.Version.ProjectId == "P7dR8mSH");
            await content.InstallAsync(definition.Id, chunky, null, Ct);
            var lithium = await content.PlanInstallAsync(definition.Id, "gvQqBUqZ", null, Ct);
            await content.InstallAsync(definition.Id, lithium, null, Ct);
            Assert.Equal(3, Directory.GetFiles(Path.Combine(paths.DataDir, "mods")).Length);

            await operations.StartAsync(definition.Id, Ct);
            await WaitForHealthAsync(runtime, definition.Id, ContainerHealth.Healthy, TimeSpan.FromMinutes(6));
            var log = await File.ReadAllTextAsync(Path.Combine(paths.DataDir, "logs", "latest.log"), Ct);
            Assert.Contains("chunky", log, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("lithium", log, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("fabric-api", log, StringComparison.OrdinalIgnoreCase);

            // Changes need a stopped server.
            var running = await Assert.ThrowsAsync<ServerOperationException>(() => content.RemoveAsync(definition.Id, lithium.Items[0].InstallName, false, Ct));
            Assert.Equal(ServerOperationError.StillRunning, running.Error);
            await operations.StopAsync(definition.Id, Ct);

            // Fabric API is protected while Chunky is active; disabling Chunky first frees it.
            var api = chunky.Items.Single(i => i.IsDependency).InstallName;
            var blocked = await Assert.ThrowsAsync<Bonfire.App.Services.ContentException>(() => content.SetEnabledAsync(definition.Id, api, false, Ct));
            Assert.Equal(Bonfire.App.Services.ContentError.HasDependents, blocked.Error);
            await content.SetEnabledAsync(definition.Id, chunky.Items[0].InstallName, false, Ct);
            await content.RemoveAsync(definition.Id, lithium.Items[0].InstallName, false, Ct);
            Assert.Single(Directory.GetFiles(Path.Combine(paths.DataDir, "mods")));

            // The server starts again with the remaining add-on; the disabled one is out of its reach.
            await operations.StartAsync(definition.Id, Ct);
            await WaitForHealthAsync(runtime, definition.Id, ContainerHealth.Healthy, TimeSpan.FromMinutes(5));
            await operations.StopAsync(definition.Id, Ct);
            var inventory = await content.InventoryAsync(definition.Id, Ct);
            Assert.All(inventory.Items, i => Assert.Equal(Bonfire.Core.Content.ItemState.Tracked, i.State));
            Assert.Single(inventory.Items, i => i.Disabled);
        }
        finally
        {
            await CleanupAsync(endpoint, installation);
        }
    }

    private static async Task WaitForHealthAsync(IServerRuntime runtime, Guid serverId, ContainerHealth wanted, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var state = (await runtime.ListAsync(Ct)).Single(s => s.ServerId == serverId);
            if (state.Health == wanted)
            {
                return;
            }

            Assert.True(state.IsRunning, $"Container stopped unexpectedly (exit {state.ExitCode})");
            await Task.Delay(TimeSpan.FromSeconds(3), Ct);
        }

        Assert.Fail($"Server did not become {wanted} within {timeout}");
    }

    private static async Task CleanupAsync(DockerEndpoint endpoint, Guid installation)
    {
        using var client = new DockerClientBuilder().WithEndpoint(endpoint.Uri).Build();
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
