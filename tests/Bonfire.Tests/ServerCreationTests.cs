using Bonfire.App.Services;
using Bonfire.Core;
using Bonfire.Core.Docker;
using Bonfire.Core.Metadata;
using Bonfire.Core.Servers;
using Bonfire.Infrastructure.Servers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bonfire.Tests;

public sealed class ServerCreationTests : IDisposable
{
    internal sealed class SafeDocker : IDockerStatusSource
    {
        public DockerProbeResult? Current { get; set; } = new(
            DockerConnectionStatus.Connected,
            new DockerEndpoint("/var/run/docker.sock"),
            DateTimeOffset.UtcNow,
            new DockerDaemonInfo("29.8.0", new Version(1, 40), new Version(1, 56), new Version(1, 56), null, null, null),
            new DockerCapabilities(false, true, "builtin", "2", "cgroupfs"));

        public bool CapabilitiesTrusted { get; set; } = true;
    }

    internal sealed class FixedPorts : IPortAvailability
    {
        public PortCheck Result { get; set; } = PortCheck.Free;

        public Task<PortCheck> CheckAsync(PortExposure exposure, int port, Guid? ignoreServerId, CancellationToken cancellationToken) =>
            Task.FromResult(Result);

        public Task<int?> FindFreeAsync(PortExposure exposure, CancellationToken cancellationToken) => Task.FromResult<int?>(25565);
    }

    private readonly TempDirectory _temp = new();
    private readonly ServerRegistry _registry;
    private readonly FakeServerRuntime _runtime = new();
    private readonly SafeDocker _docker = new();
    private readonly FixedPorts _ports = new();
    private readonly ServerCreationService _creation;

    public ServerCreationTests()
    {
        var app = AppPaths.Resolve(k => k == "HOME" ? _temp.Path : null);
        Directory.CreateDirectory(app.DataDir);
        _registry = new ServerRegistry(app);
        var operations = new ServerOperationsService(_registry, _runtime, _docker, NullLogger<ServerOperationsService>.Instance);
        _creation = new ServerCreationService(_registry, _runtime, operations, _ports, TimeProvider.System, NullLogger<ServerCreationService>.Instance);
    }

    public void Dispose() => _temp.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    internal static CreationRequest Request(string name = "My world", bool start = true) => new(
        name,
        new ResolvedServerVersion(ServerType.Vanilla, "26.3", 25, null, null, null, true),
        new ResourceLimits(1024, 2048, 1),
        25565,
        PortExposure.Localhost,
        RestartPolicyKind.No,
        new GameSettings(true, Difficulty.Normal, 10, false, [], null),
        DateTimeOffset.UtcNow,
        start);

    [Fact]
    public async Task CreatesProvisionsAndStarts()
    {
        var result = await _creation.CreateAsync(Request(), null, Ct);

        Assert.True(result.Started);
        Assert.Equal(["pull:java25", "provision", "start"], _runtime.Calls);
        var record = _registry.Load(result.ServerId)!.Record!;
        Assert.Equal(ServerTestData.Digest, record.Definition.Image.Digest); // pinned from the pull
        Assert.Equal(ProvisioningPhase.Ready, record.Phase);
    }

    [Fact]
    public async Task InvalidInputFailsBeforeAnythingIsDownloaded()
    {
        var error = await Assert.ThrowsAsync<CreationValidationException>(() => _creation.CreateAsync(Request(name: ""), null, Ct));

        Assert.Contains("Validation_NameEmpty", error.Errors);
        Assert.Empty(_runtime.Calls);
        Assert.Empty(_registry.LoadAll());
    }

    [Fact]
    public async Task MissingEulaFailsBeforeDownloading()
    {
        var error = await Assert.ThrowsAsync<CreationValidationException>(() =>
            _creation.CreateAsync(Request() with { EulaAcceptedAt = default }, null, Ct));

        Assert.Contains("Validation_EulaRequired", error.Errors);
        Assert.Empty(_runtime.Calls);
    }

    [Fact]
    public async Task UnsafeDaemonFailsBeforeDownloading()
    {
        _docker.CapabilitiesTrusted = false;

        var error = await Assert.ThrowsAsync<ServerOperationException>(() => _creation.CreateAsync(Request(), null, Ct));

        Assert.Equal(ServerOperationError.DockerUnsupported, error.Error);
        Assert.Empty(_runtime.Calls);
    }

    [Fact]
    public async Task TakenPortFailsBeforeDownloading()
    {
        _ports.Result = PortCheck.UsedByHost;

        var error = await Assert.ThrowsAsync<ServerOperationException>(() => _creation.CreateAsync(Request(), null, Ct));

        Assert.Equal(ServerOperationError.PortInUse, error.Error);
        Assert.Empty(_runtime.Calls);
    }

    [Fact]
    public async Task CancelDuringDownloadLeavesNoServer()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        _runtime.Acquire = async (_, ct) =>
        {
            await cts.CancelAsync();
            ct.ThrowIfCancellationRequested();
            return _runtime.Image;
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _creation.CreateAsync(Request(), null, cts.Token));

        Assert.Empty(_registry.LoadAll());
    }

    [Fact]
    public async Task StartFailureStillLeavesACreatedServer()
    {
        _runtime.StartBehavior = _ => throw new ServerOperationException(ServerOperationError.PortInUse, "x");

        var result = await _creation.CreateAsync(Request(), null, Ct);

        Assert.False(result.Started);
        Assert.Equal(ServerOperationError.PortInUse, result.StartError!.Error);
        Assert.Equal(ProvisioningPhase.Ready, _registry.Load(result.ServerId)!.Record!.Phase);
    }

    [Fact]
    public async Task CancellingTheStartStillReportsTheCreatedServer()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        _runtime.StartBehavior = async _ =>
        {
            await cts.CancelAsync();
            cts.Token.ThrowIfCancellationRequested();
        };

        var result = await _creation.CreateAsync(Request(), null, cts.Token);

        Assert.True(result.StartCancelled);
        Assert.False(result.Started);
        Assert.Equal(ProvisioningPhase.Ready, _registry.Load(result.ServerId)!.Record!.Phase);
    }

    [Fact]
    public async Task NotStartingWhenNotRequested()
    {
        var result = await _creation.CreateAsync(Request(start: false), null, Ct);

        Assert.False(result.Started);
        Assert.Null(result.StartError);
        Assert.DoesNotContain("start", _runtime.Calls);
    }
}
