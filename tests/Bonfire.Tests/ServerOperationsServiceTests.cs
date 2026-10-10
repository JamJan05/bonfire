using Bonfire.App.Services;
using Bonfire.Core;
using Bonfire.Core.Docker;
using Bonfire.Core.Servers;
using Bonfire.Infrastructure.Servers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bonfire.Tests;

public sealed class ServerOperationsServiceTests : IDisposable
{
    private sealed class Status : IDockerStatusSource
    {
        public DockerProbeResult? Current { get; set; }

        public bool CapabilitiesTrusted { get; set; } = true;
    }

    private readonly TempDirectory _temp = new();
    private readonly ServerRegistry _registry;
    private readonly FakeServerRuntime _runtime = new();
    private readonly Status _status = new();
    private readonly ServerOperationsService _operations;

    public ServerOperationsServiceTests()
    {
        var app = AppPaths.Resolve(k => k == "HOME" ? _temp.Path : null);
        Directory.CreateDirectory(app.DataDir);
        _registry = new ServerRegistry(app);
        _operations = new ServerOperationsService(_registry, _runtime, _status, NullLogger<ServerOperationsService>.Instance);
        _status.Current = Daemon(rootless: false, seccomp: true);
    }

    public void Dispose() => _temp.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DockerProbeResult Daemon(bool? rootless, bool? seccomp, bool capsKnown = true) => new(
        DockerConnectionStatus.Connected,
        new DockerEndpoint("/var/run/docker.sock"),
        DateTimeOffset.UtcNow,
        new DockerDaemonInfo("29.8.0", new Version(1, 40), new Version(1, 56), new Version(1, 56), null, null, null),
        capsKnown ? new DockerCapabilities(rootless, seccomp, "builtin", "2", "cgroupfs") : null);

    private async Task<Guid> CreateServerAsync(ProvisioningPhase phase = ProvisioningPhase.Ready)
    {
        var record = ServerRecord.New(ServerTestData.Vanilla()) with { Phase = phase, ContainerId = "c1", NetworkId = "n1" };
        await _registry.CreateAsync(record, Ct);
        return record.Definition.Id;
    }

    public static TheoryData<string> UnsafeDaemons => new() { "rootless", "no seccomp", "unknown caps", "untrusted", "disconnected" };

    private void MakeUnsafe(string kind)
    {
        if (kind == "untrusted")
        {
            _status.CapabilitiesTrusted = false; // a Docker call failed after the last probe
            return;
        }

        _status.Current = kind switch
        {
            "rootless" => Daemon(rootless: true, seccomp: true),
            "no seccomp" => Daemon(rootless: false, seccomp: false),
            "unknown caps" => Daemon(null, null, capsKnown: false),
            _ => new DockerProbeResult(DockerConnectionStatus.PermissionDenied, null, DateTimeOffset.UtcNow),
        };
    }

    [Theory]
    [MemberData(nameof(UnsafeDaemons))]
    public async Task StartAndRepairAreBlockedOnAnUnsafeDaemon(string kind)
    {
        var id = await CreateServerAsync();
        MakeUnsafe(kind);

        var start = await Assert.ThrowsAsync<ServerOperationException>(() => _operations.StartAsync(id, Ct));
        var repair = await Assert.ThrowsAsync<ServerOperationException>(() => _operations.RepairAsync(id, Ct));

        Assert.Equal(ServerOperationError.DockerUnsupported, start.Error);
        Assert.Equal(ServerOperationError.DockerUnsupported, repair.Error);
        Assert.Empty(_runtime.Calls);
    }

    [Theory]
    [InlineData("rootless")]
    [InlineData("no seccomp")]
    [InlineData("unknown caps")]
    [InlineData("untrusted")]
    public async Task ContainmentStaysPossibleWhenOnlyCapabilitiesRegressed(string kind)
    {
        var id = await CreateServerAsync();
        MakeUnsafe(kind);

        await _operations.StopAsync(id, Ct);
        await _operations.ForceStopAsync(id, Ct);
        await _operations.RemoveContainerAsync(id, Ct);

        Assert.Equal(["stop", "kill", "remove"], _runtime.Calls);
    }

    [Fact]
    public async Task RestartStartsOnlyAfterTheServerStopped()
    {
        var id = await CreateServerAsync();
        _runtime.Containers.Add(FakeServerRuntime.State(id, "exited"));

        await _operations.RestartAsync(id, Ct);

        Assert.Equal(["stop", "start"], _runtime.Calls);
    }

    [Fact]
    public async Task ForceStopDuringRestartAbortsTheRestart()
    {
        var id = await CreateServerAsync();
        var stopping = new TaskCompletionSource();
        _runtime.Stop = (_, _) => stopping.Task;
        _runtime.Containers.Add(FakeServerRuntime.State(id, "exited"));

        var restart = _operations.RestartAsync(id, Ct);
        await _operations.ForceStopAsync(id, Ct);
        stopping.SetResult();

        var error = await Assert.ThrowsAsync<ServerOperationException>(() => restart);
        Assert.Equal(ServerOperationError.RestartAborted, error.Error);
        Assert.DoesNotContain("start", _runtime.Calls);
    }

    [Fact]
    public async Task ForceStopWhileRestartVerifiesTheStopStillAbortsTheRestart()
    {
        var id = await CreateServerAsync();
        var listing = new TaskCompletionSource<IReadOnlyList<ServerContainerState>>();
        var listingStarted = new TaskCompletionSource();
        _runtime.List = _ =>
        {
            listingStarted.TrySetResult();
            return listing.Task;
        };

        var restart = _operations.RestartAsync(id, Ct);
        await listingStarted.Task; // the stop finished; restart is checking the state
        await _operations.ForceStopAsync(id, Ct);
        listing.SetResult([FakeServerRuntime.State(id, "exited")]);

        var error = await Assert.ThrowsAsync<ServerOperationException>(() => restart);
        Assert.Equal(ServerOperationError.RestartAborted, error.Error);
        Assert.DoesNotContain("start", _runtime.Calls);
    }

    [Fact]
    public async Task SecondOperationOnTheSameServerIsBusy()
    {
        var id = await CreateServerAsync();
        var stopping = new TaskCompletionSource();
        _runtime.Stop = (_, _) => stopping.Task;

        var stop = _operations.StopAsync(id, Ct);
        var busy = await Assert.ThrowsAsync<ServerOperationException>(() => _operations.StartAsync(id, Ct));
        stopping.SetResult();
        await stop;

        Assert.Equal(ServerOperationError.Busy, busy.Error);
    }

    [Theory]
    [InlineData(ProvisioningPhase.Provisioning, "provision")]
    [InlineData(ProvisioningPhase.Removing, "remove")]
    [InlineData(ProvisioningPhase.Ready, "provision")]
    public async Task RepairFollowsThePersistedIntent(ProvisioningPhase phase, string expectedCall)
    {
        var id = await CreateServerAsync(phase);

        await _operations.RepairAsync(id, Ct);

        Assert.Equal([expectedCall], _runtime.Calls);
        Assert.Equal(ProvisioningPhase.Ready, _registry.Load(id)!.Record!.Phase);
    }

    [Fact]
    public async Task OperationsUseTheRecordOnDiskNotACachedCopy()
    {
        var id = await CreateServerAsync();
        File.WriteAllText(_registry.PathsFor(id).RecordFile, "{ damaged");

        var error = await Assert.ThrowsAsync<ServerOperationException>(() => _operations.StopAsync(id, Ct));

        Assert.Equal(ServerOperationError.RecordUnavailable, error.Error);
        Assert.Empty(_runtime.Calls);
    }
}
