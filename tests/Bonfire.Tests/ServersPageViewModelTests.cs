using Bonfire.App.Localization;
using Bonfire.App.Services;
using Bonfire.App.ViewModels;
using Bonfire.Core;
using Bonfire.Core.Docker;
using Bonfire.Core.Servers;
using Bonfire.Infrastructure.Servers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Bonfire.Tests;

[Collection(LocalizerCollection.Name)]
public sealed class ServersPageViewModelTests : IDisposable
{
    private sealed class ConnectedEngine : IDockerEngine
    {
        public int Probes;

        public Task<DockerProbeResult> ProbeAsync(CancellationToken cancellationToken) => Task.FromResult(Probe());

        private DockerProbeResult Probe()
        {
            Interlocked.Increment(ref Probes);
            return new DockerProbeResult(
            DockerConnectionStatus.Connected,
            new DockerEndpoint("/var/run/docker.sock"),
            DateTimeOffset.UtcNow,
            new DockerDaemonInfo("29.8.0", new Version(1, 40), new Version(1, 56), new Version(1, 56), null, null, null),
            new DockerCapabilities(false, true, "builtin", "2", "cgroupfs"));
        }
    }

    private sealed class NoDialogs : IDialogService
    {
        public bool Answer { get; set; } = true;

        public Task<bool> ConfirmAsync(string title, string message, string confirmText, bool dangerous) => Task.FromResult(Answer);
    }

    private readonly TempDirectory _temp = new();
    private readonly ServerRegistry _registry;
    private readonly FakeServerRuntime _runtime = new();
    private readonly FakeTimeProvider _time = new();
    private readonly ConnectedEngine _engine = new();
    private readonly DockerStatusViewModel _docker;
    private readonly ServersPageViewModel _page;
    private readonly ServerOperationsService _operations;

    public ServersPageViewModelTests()
    {
        Localizer.Instance.SetLanguage(AppLanguage.English);
        var app = AppPaths.Resolve(k => k == "HOME" ? _temp.Path : null);
        Directory.CreateDirectory(app.DataDir);
        _registry = new ServerRegistry(app);
        _docker = new DockerStatusViewModel(_engine, Localizer.Instance, NullLogger<DockerStatusViewModel>.Instance);
        var operations = new ServerOperationsService(_registry, _runtime, _docker, NullLogger<ServerOperationsService>.Instance);
        _operations = operations;
        _page = new ServersPageViewModel(
            Localizer.Instance, _docker, _registry, _runtime, operations, new NoDialogs(), new Navigator(), _time,
            NullLogger<ServersPageViewModel>.Instance);
    }

    public void Dispose()
    {
        _page.Dispose();
        _docker.Dispose();
        _temp.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<Guid> CreateServerAsync()
    {
        var record = ServerRecord.New(ServerTestData.Vanilla()) with { Phase = ProvisioningPhase.Ready, ContainerId = "c1", NetworkId = "n1" };
        await _registry.CreateAsync(record, Ct);
        return record.Definition.Id;
    }

    [Fact]
    public async Task PollShowsServersAndKeepsItemInstancesStable()
    {
        var id = await CreateServerAsync();
        _runtime.Containers.Add(FakeServerRuntime.State(id, "running", ContainerHealth.Healthy));
        await _docker.RefreshCommand.ExecuteAsync(null);

        await _page.RefreshAsync();
        var item = Assert.Single(_page.Servers);
        Assert.Equal(ServerStatus.Running, item.Snapshot.Status);

        _runtime.Containers[0] = FakeServerRuntime.State(id, "exited");
        await _page.RefreshAsync();

        Assert.Same(item, Assert.Single(_page.Servers));
        Assert.Equal(ServerStatus.Stopped, item.Snapshot.Status);
        Assert.True(item.ShowStart);
    }

    [Fact]
    public async Task FailedPollKeepsLastStateMarkedStale()
    {
        var id = await CreateServerAsync();
        _runtime.Containers.Add(FakeServerRuntime.State(id, "running", ContainerHealth.Healthy));
        await _docker.RefreshCommand.ExecuteAsync(null);
        await _page.RefreshAsync();

        _runtime.List = _ => throw new HttpRequestException("daemon went away");
        await _page.RefreshAsync();

        var item = Assert.Single(_page.Servers);
        Assert.Equal(ServerStatus.Running, item.Snapshot.Status); // not invented "stopped"
        Assert.True(item.IsStale);
        Assert.True(_page.IsStale);
    }

    [Fact]
    public async Task PollThatStartedBeforeAnActionFinishedIsDiscarded()
    {
        var id = await CreateServerAsync();
        _runtime.Containers.Add(FakeServerRuntime.State(id, "exited"));
        await _docker.RefreshCommand.ExecuteAsync(null);
        await _page.RefreshAsync();
        var item = Assert.Single(_page.Servers);
        var published = new List<ServerStatus>();
        item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(item.StatusText))
            {
                published.Add(item.Snapshot.Status);
            }
        };

        // A slow poll is inside the Docker listing, holding the old "exited" state…
        var slowList = new TaskCompletionSource<IReadOnlyList<ServerContainerState>>();
        var slowListStarted = new TaskCompletionSource();
        var first = true;
        _runtime.List = _ =>
        {
            if (!first)
            {
                return Task.FromResult<IReadOnlyList<ServerContainerState>>(_runtime.Containers.ToList());
            }

            first = false;
            slowListStarted.SetResult();
            return slowList.Task;
        };
        var poll = _page.RefreshAsync();
        await slowListStarted.Task;

        // …while the user starts the server and that finishes first.
        _runtime.Containers[0] = FakeServerRuntime.State(id, "running", ContainerHealth.Healthy);
        await item.StartCommand.ExecuteAsync(null);
        published.Clear();

        slowList.SetResult([FakeServerRuntime.State(id, "exited")]);
        await poll;

        Assert.DoesNotContain(ServerStatus.Stopped, published); // the stale result was never shown
        Assert.Equal(ServerStatus.Running, item.Snapshot.Status);
    }

    [Fact]
    public async Task ListingFailureBlocksStartingUntilDockerIsProbedAgain()
    {
        var id = await CreateServerAsync();
        _runtime.Containers.Add(FakeServerRuntime.State(id, "exited"));
        await _docker.RefreshCommand.ExecuteAsync(null);
        await _page.RefreshAsync();
        var item = Assert.Single(_page.Servers);
        var probesBefore = _engine.Probes;

        _runtime.List = _ => throw new HttpRequestException("daemon restarting");
        await _page.RefreshAsync();

        Assert.True(_engine.Probes > probesBefore, "a failing daemon must be probed again");
        await item.StartCommand.ExecuteAsync(null);
        Assert.Equal("Error_DockerUnsupported", item.ErrorKeyForTests);
        Assert.DoesNotContain("start", _runtime.Calls);
    }

    [Fact]
    public async Task ForcingAPendingStopWorksWhileCapabilitiesAreUntrusted()
    {
        var id = await CreateServerAsync();
        _runtime.Containers.Add(FakeServerRuntime.State(id, "running", ContainerHealth.Healthy));
        await _docker.RefreshCommand.ExecuteAsync(null);
        await _page.RefreshAsync();
        var item = Assert.Single(_page.Servers);
        var stopping = new TaskCompletionSource();
        _runtime.Stop = (_, _) => stopping.Task;

        var stop = item.StopCommand.ExecuteAsync(null);
        _docker.Invalidate(); // e.g. a listing failed meanwhile
        _time.Advance(ServerItemViewModel.ForceStopOfferDelay + TimeSpan.FromSeconds(1));
        await item.ForceStopCommand.ExecuteAsync(null);
        stopping.SetResult();
        await stop;

        Assert.Contains("kill", _runtime.Calls);
        Assert.Null(item.ErrorKeyForTests);
    }

    [Fact]
    public async Task DockerOnlyCardsSurviveAnUnavailableDocker()
    {
        var orphanId = Guid.NewGuid();
        _runtime.Containers.Add(FakeServerRuntime.State(orphanId, "running"));
        await _docker.RefreshCommand.ExecuteAsync(null);
        await _page.RefreshAsync();
        Assert.Equal(ServerStatus.Orphan, Assert.Single(_page.Servers).Snapshot.Status);

        _runtime.List = _ => throw new HttpRequestException("gone");
        await _page.RefreshAsync();

        var card = Assert.Single(_page.Servers);
        Assert.True(card.IsStale);

        _runtime.List = null;
        _runtime.Containers.Clear();
        await _docker.RefreshCommand.ExecuteAsync(null);
        await _page.RefreshAsync();
        Assert.Empty(_page.Servers); // removed once Docker confirms it is gone
    }

    [Fact]
    public async Task ForceStopIsOfferedOnlyAfterTheDelay()
    {
        var id = await CreateServerAsync();
        _runtime.Containers.Add(FakeServerRuntime.State(id, "running", ContainerHealth.Healthy));
        await _docker.RefreshCommand.ExecuteAsync(null);
        await _page.RefreshAsync();
        var item = Assert.Single(_page.Servers);
        var stopping = new TaskCompletionSource();
        _runtime.Stop = (_, _) => stopping.Task;

        var stop = item.StopCommand.ExecuteAsync(null);
        Assert.True(item.IsStopping);
        Assert.False(item.ShowForceStop);

        _time.Advance(ServerItemViewModel.ForceStopOfferDelay - TimeSpan.FromSeconds(1));
        Assert.False(item.ShowForceStop);
        _time.Advance(TimeSpan.FromSeconds(2));
        Assert.True(item.ShowForceStop);
        Assert.True(item.ForceStopCommand.CanExecute(null));

        await item.ForceStopCommand.ExecuteAsync(null);
        Assert.Contains("kill", _runtime.Calls);

        stopping.SetResult();
        await stop;
        Assert.False(item.IsStopping);
        Assert.False(item.ShowForceStop);
    }

    [Fact]
    public async Task ConsoleStopDrivesTheStoppingIndicatorAndForceStopOffer()
    {
        var id = await CreateServerAsync();
        _runtime.Containers.Add(FakeServerRuntime.State(id, "running", ContainerHealth.Healthy));
        await _docker.RefreshCommand.ExecuteAsync(null);
        await _page.RefreshAsync();
        var item = Assert.Single(_page.Servers);
        var stopping = new TaskCompletionSource();
        _runtime.Stop = (_, _) => stopping.Task;
        var operations = new ServerOperationsService(_registry, _runtime, _docker, NullLogger<ServerOperationsService>.Instance);
        using var details = new ServerDetailsViewModel(
            Localizer.Instance,
            item,
            new ServerLogViewModel(id, _registry, _runtime, Localizer.Instance, _time, NullLogger.Instance),
            new ServerConsoleViewModel(id, operations, Localizer.Instance, NullLogger.Instance),
            new ServerPlayersViewModel(id, operations, Localizer.Instance, _time),
            new ServerSettingsViewModel(id, _registry, operations, new NoDialogs(), Localizer.Instance, NullLogger.Instance),
            TestDetails.Backups(id, _registry, _runtime, operations, new NoDialogs(), new NoLauncher()),
            TestDetails.Content(id, _registry, _runtime, operations),
            _registry,
            new NoLauncher(),
            new Navigator());

        details.Console.Input = "stop";
        await details.Console.SendCommand.ExecuteAsync(null);

        Assert.True(item.IsStopping);
        _time.Advance(ServerItemViewModel.ForceStopOfferDelay + TimeSpan.FromSeconds(1));
        Assert.True(item.ShowForceStop);
        Assert.Empty(_runtime.RconCommands);
        stopping.SetResult();
    }

    [Fact]
    public async Task SettingsChangeOnARunningServerOffersForceStopWhichAbortsIt()
    {
        var id = await CreateServerAsync();
        _runtime.Containers.Add(FakeServerRuntime.State(id, "running", ContainerHealth.Healthy));
        await _docker.RefreshCommand.ExecuteAsync(null);
        await _page.RefreshAsync();
        var item = Assert.Single(_page.Servers);
        var stopping = new TaskCompletionSource();
        _runtime.Stop = (_, _) => stopping.Task;
        var operations = _operations; // the same coordinator as the card, as in the app
        var settings = new ServerSettingsViewModel(id, _registry, operations, new NoDialogs(), Localizer.Instance, NullLogger.Instance);
        using var details = new ServerDetailsViewModel(
            Localizer.Instance, item,
            new ServerLogViewModel(id, _registry, _runtime, Localizer.Instance, _time, NullLogger.Instance),
            new ServerConsoleViewModel(id, operations, Localizer.Instance, NullLogger.Instance),
            new ServerPlayersViewModel(id, operations, Localizer.Instance, _time),
            settings, TestDetails.Backups(id, _registry, _runtime, operations, new NoDialogs(), new NoLauncher()),
            TestDetails.Content(id, _registry, _runtime, operations), _registry, new NoLauncher(), new Navigator());
        await settings.LoadAsync();

        settings.Motd = "Changed";
        var save = settings.SaveCommand.ExecuteAsync(null);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.True(item.IsStopping);
        _time.Advance(ServerItemViewModel.ForceStopOfferDelay + TimeSpan.FromSeconds(1));
        Assert.True(item.ForceStopCommand.CanExecute(null));

        await item.ForceStopCommand.ExecuteAsync(null);
        stopping.SetResult();
        await save;

        Assert.Contains("force stop", settings.ResultMessage);
        Assert.Null(_registry.Load(id)!.Record!.Definition.Game.Motd);
    }

    private sealed class NoLauncher : IFolderLauncher
    {
        public Task<bool> OpenAsync(string path) => Task.FromResult(true);

        public Task<bool> OpenUriAsync(Uri uri) => Task.FromResult(true);
    }

    [Fact]
    public async Task ErrorsAreLocalized()
    {
        var id = await CreateServerAsync();
        _runtime.Containers.Add(FakeServerRuntime.State(id, "running", ContainerHealth.Healthy));
        await _docker.RefreshCommand.ExecuteAsync(null);
        await _page.RefreshAsync();
        var item = Assert.Single(_page.Servers);
        _runtime.Stop = (_, _) => throw new ServerOperationException(ServerOperationError.PortInUse, "x");

        await item.StopCommand.ExecuteAsync(null);

        Assert.Equal("The port is already used by another program or server.", item.ErrorMessage);
        try
        {
            Localizer.Instance.SetLanguage(AppLanguage.Polish);
            Assert.Equal("Port jest już zajęty przez inny program lub serwer.", item.ErrorMessage);
        }
        finally
        {
            Localizer.Instance.SetLanguage(AppLanguage.English);
        }
    }
}
