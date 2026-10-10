using Bonfire.App.Localization;
using Bonfire.App.Services;
using Bonfire.App.ViewModels;
using Bonfire.Core;
using Bonfire.Core.Metadata;
using Bonfire.Core.Servers;
using Bonfire.Infrastructure.Linux;
using Bonfire.Infrastructure.Servers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bonfire.Tests;

[Collection(LocalizerCollection.Name)]
public sealed class NewServerViewModelTests : IDisposable
{
    private sealed class ControlledMetadata : IServerMetadata
    {
        public Dictionary<(ServerType, string), TaskCompletionSource<ResolvedServerVersion>> Pending { get; } = [];

        public bool AutoResolve { get; set; } = true;

        public Task<IReadOnlyList<GameVersion>> GetReleasesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<GameVersion>>(
                [new GameVersion("26.3", DateTimeOffset.UtcNow), new GameVersion("26.2", DateTimeOffset.UtcNow.AddMonths(-3))]);

        public Task<ResolvedServerVersion> ResolveAsync(ServerType type, string minecraftVersion, CancellationToken cancellationToken)
        {
            if (AutoResolve)
            {
                return Task.FromResult(new ResolvedServerVersion(type, minecraftVersion, 25,
                    type == ServerType.Fabric ? "0.19.5" : null, type == ServerType.Fabric ? "1.1.2" : null,
                    type == ServerType.Paper ? 90 : null, true));
            }

            var tcs = new TaskCompletionSource<ResolvedServerVersion>();
            Pending[(type, minecraftVersion)] = tcs;
            return tcs.Task;
        }

        public void Invalidate()
        {
        }
    }

    private sealed class NoLauncher : IFolderLauncher
    {
        public Task<bool> OpenAsync(string path) => Task.FromResult(true);

        public Task<bool> OpenUriAsync(Uri uri) => Task.FromResult(true);
    }

    private readonly TempDirectory _temp = new();
    private readonly ServerRegistry _registry;
    private readonly FakeServerRuntime _runtime = new();
    private readonly ControlledMetadata _metadata = new();
    private readonly Navigator _navigator = new();
    private readonly NewServerViewModel _wizard;
    private int _shownServers;

    public NewServerViewModelTests()
    {
        Localizer.Instance.SetLanguage(AppLanguage.English);
        var app = AppPaths.Resolve(k => k == "HOME" ? _temp.Path : null);
        Directory.CreateDirectory(app.DataDir);
        _registry = new ServerRegistry(app);
        var docker = new ServerCreationTests.SafeDocker();
        var ports = new ServerCreationTests.FixedPorts();
        var operations = new ServerOperationsService(_registry, _runtime, docker, NullLogger<ServerOperationsService>.Instance);
        var creation = new ServerCreationService(_registry, _runtime, operations, ports, TimeProvider.System, NullLogger<ServerCreationService>.Instance);
        _navigator.ServersRequested += (_, _) => _shownServers++;
        _wizard = new NewServerViewModel(
            Localizer.Instance, _metadata, creation, ports, new NoLauncher(), _navigator, TimeProvider.System,
            NullLogger<NewServerViewModel>.Instance, () => new MemoryInfo(16L << 30, 8L << 30));
    }

    public void Dispose()
    {
        _wizard.Dispose();
        _temp.Dispose();
    }

    private async Task FillValidAsync()
    {
        await _wizard.InitializeAsync();
        _wizard.DisplayName = "My world";
        _wizard.EulaAccepted = true;
    }

    [Fact]
    public async Task EulaIsUncheckedByDefaultAndRequired()
    {
        await _wizard.InitializeAsync();
        _wizard.DisplayName = "My world";

        Assert.False(_wizard.EulaAccepted);
        Assert.False(_wizard.CanCreate);
        Assert.Contains("Accept the Minecraft EULA to create a server.", _wizard.Errors);

        _wizard.EulaAccepted = true;
        Assert.True(_wizard.CanCreate);
    }

    [Fact]
    public async Task OlderLookupArrivingLateIsDiscarded()
    {
        await _wizard.InitializeAsync();
        _metadata.AutoResolve = false;

        _wizard.SelectedType = ServerType.Fabric; // lookup A
        _wizard.SelectedType = ServerType.Paper;  // lookup B supersedes A
        Assert.True(_wizard.IsResolving);
        Assert.False(_wizard.CanCreate);

        _metadata.Pending[(ServerType.Paper, "26.3")].SetResult(new ResolvedServerVersion(ServerType.Paper, "26.3", 25, null, null, 90, true));
        _metadata.Pending[(ServerType.Fabric, "26.3")].SetResult(new ResolvedServerVersion(ServerType.Fabric, "26.3", 25, "0.19.5", "1.1.2", null, true));
        await Task.Yield();

        Assert.Equal(ServerType.Paper, _wizard.Resolved!.Type);
        Assert.False(_wizard.IsResolving);
    }

    [Fact]
    public async Task FailureOfASupersededLookupIsDiscardedSafely()
    {
        await _wizard.InitializeAsync();
        _metadata.AutoResolve = false;

        _wizard.SelectedType = ServerType.Fabric; // lookup A
        _wizard.SelectedType = ServerType.Paper;  // lookup B supersedes A
        _metadata.Pending[(ServerType.Fabric, "26.3")].SetException(new InvalidOperationException("boom")); // must not escape async void
        _metadata.Pending[(ServerType.Paper, "26.3")].SetResult(new ResolvedServerVersion(ServerType.Paper, "26.3", 25, null, null, 90, true));
        await Task.Yield();

        Assert.Equal(ServerType.Paper, _wizard.Resolved!.Type);
        Assert.Null(_wizard.ResolutionError);
    }

    [Fact]
    public async Task ClearedNumberFieldBlocksCreateWithAMessage()
    {
        await FillValidAsync();
        Assert.True(_wizard.CanCreate);

        _wizard.Port = null;

        Assert.False(_wizard.CanCreate);
        Assert.False(_wizard.CreateCommand.CanExecute(null));
        Assert.Contains(_wizard.Errors, e => e.Contains("required", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DisposingTwiceIsSafe()
    {
        _wizard.Dispose();
        _wizard.Dispose();
    }

    [Fact]
    public void DisposedPageNoLongerListensToLanguageChanges()
    {
        var raised = 0;
        _wizard.PropertyChanged += (_, e) => raised += e.PropertyName == nameof(_wizard.Title) ? 1 : 0;
        _wizard.Dispose();

        try
        {
            Localizer.Instance.SetLanguage(AppLanguage.Polish);
        }
        finally
        {
            Localizer.Instance.SetLanguage(AppLanguage.English);
        }

        Assert.Equal(0, raised); // the singleton localizer no longer references it
    }

    [Fact]
    public async Task CreateIsDisabledWhileResolving()
    {
        await FillValidAsync();
        _metadata.AutoResolve = false;

        _wizard.SelectedVersion = _wizard.Versions[1];

        Assert.False(_wizard.CreateCommand.CanExecute(null));
    }

    [Fact]
    public async Task DoubleClickCreatesOnlyOneServer()
    {
        await FillValidAsync();
        var pulling = new TaskCompletionSource<ResolvedImage>();
        _runtime.Acquire = (_, _) => pulling.Task;

        var first = _wizard.CreateCommand.ExecuteAsync(null);
        Assert.False(_wizard.CreateCommand.CanExecute(null));
        _wizard.CreateCommand.Execute(null);
        pulling.SetResult(_runtime.Image);
        await first;

        Assert.Single(_runtime.Calls, c => c.StartsWith("pull:", StringComparison.Ordinal));
        Assert.Single(_registry.LoadAll());
        Assert.Equal(1, _shownServers);
    }

    [Fact]
    public async Task CreatedButNotStartedStaysOnTheWizardWithAMessage()
    {
        await FillValidAsync();
        _runtime.StartBehavior = _ => throw new ServerOperationException(ServerOperationError.PortInUse, "x");

        await _wizard.CreateCommand.ExecuteAsync(null);

        Assert.Equal(0, _shownServers);
        Assert.True(_wizard.ShowGoToServers);
        Assert.Contains("created but could not be started", _wizard.ResultMessage);
        Assert.Single(_registry.LoadAll());
    }

    [Fact]
    public async Task CancelBeforeTheRecordExistsReportsNothingCreated()
    {
        await FillValidAsync();
        var pulling = new TaskCompletionSource<ResolvedImage>();
        _runtime.Acquire = (_, ct) =>
        {
            ct.Register(() => pulling.TrySetCanceled(ct));
            return pulling.Task;
        };

        var create = _wizard.CreateCommand.ExecuteAsync(null);
        _wizard.CancelCreationCommand.Execute(null);
        await create;

        Assert.Equal("Cancelled. No server was created.", _wizard.ResultMessage);
        Assert.Empty(_registry.LoadAll());
    }

    [Fact]
    public async Task EmptyEnabledWhitelistIsExplained()
    {
        await FillValidAsync();

        _wizard.WhitelistEnabled = true;

        Assert.NotNull(_wizard.WhitelistEmptyNote);
        Assert.True(_wizard.CanCreate); // allowed, just explained
    }
}
