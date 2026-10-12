using Bonfire.App.Localization;
using Bonfire.App.Services;
using Bonfire.App.ViewModels;
using Bonfire.Core;
using Bonfire.Core.Docker;
using Bonfire.Core.Servers;
using Bonfire.Infrastructure;
using Bonfire.Infrastructure.Servers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Bonfire.Tests;

/// <summary>Navigation as the sidebar list drives it (the list writes back its selection).</summary>
[Collection(LocalizerCollection.Name)]
public sealed class MainWindowViewModelTests : IDisposable
{
    private sealed class ConnectedEngine : IDockerEngine
    {
        public Task<DockerProbeResult> ProbeAsync(CancellationToken cancellationToken) => Task.FromResult(new DockerProbeResult(
            DockerConnectionStatus.Connected, new DockerEndpoint("/var/run/docker.sock"), DateTimeOffset.UtcNow,
            new DockerDaemonInfo("29.8.0", new Version(1, 40), new Version(1, 56), new Version(1, 56), null, null, null),
            new DockerCapabilities(false, true, "builtin", "2", "cgroupfs")));
    }

    private sealed class Quiet : IDialogService, IFolderLauncher
    {
        public Task<bool> ConfirmAsync(string title, string message, string confirmText, bool dangerous) => Task.FromResult(true);

        public Task<bool> OpenAsync(string path) => Task.FromResult(true);

        public Task<bool> OpenUriAsync(Uri uri) => Task.FromResult(true);
    }

    private readonly TempDirectory _temp = new();
    private readonly FakeServerRuntime _runtime = new();
    private readonly ServerRegistry _registry;
    private readonly DockerStatusViewModel _docker;
    private readonly ServersPageViewModel _servers;
    private readonly Navigator _navigator = new();
    private readonly MainWindowViewModel _main;
    private readonly List<ServerDetailsViewModel> _created = [];

    public MainWindowViewModelTests()
    {
        Localizer.Instance.SetLanguage(AppLanguage.English);
        var app = AppPaths.Resolve(k => k == "HOME" ? _temp.Path : null);
        AppDirectories.Ensure(app);
        _registry = new ServerRegistry(app);
        var time = new FakeTimeProvider();
        _docker = new DockerStatusViewModel(new ConnectedEngine(), Localizer.Instance, NullLogger<DockerStatusViewModel>.Instance);
        var operations = new ServerOperationsService(_registry, _runtime, _docker, NullLogger<ServerOperationsService>.Instance);
        var quiet = new Quiet();
        _servers = new ServersPageViewModel(Localizer.Instance, _docker, _registry, _runtime, operations, quiet, _navigator, time,
            NullLogger<ServersPageViewModel>.Instance);
        var store = new SettingsStore(app.SettingsFile);
        var settings = new SettingsPageViewModel(Localizer.Instance, new SettingsService(store, store.Load(), NullLogger<SettingsService>.Instance));
        var about = new AboutPageViewModel(Localizer.Instance, app, quiet, NullLogger<AboutPageViewModel>.Instance);
        _main = new MainWindowViewModel(Localizer.Instance, _docker, _servers, settings, about, _navigator,
            () => throw new NotSupportedException(),
            item =>
            {
                var details = new ServerDetailsViewModel(Localizer.Instance, item,
                    new ServerLogViewModel(item.ServerId, _registry, _runtime, Localizer.Instance, time, NullLogger.Instance),
                    new ServerConsoleViewModel(item.ServerId, operations, Localizer.Instance, NullLogger.Instance),
                    new ServerPlayersViewModel(item.ServerId, operations, Localizer.Instance, time),
                    TestDetails.Settings(item.ServerId, _registry, operations, quiet),
                    TestDetails.Backups(item.ServerId, _registry, _runtime, operations, quiet, quiet),
                    TestDetails.Content(item.ServerId, _registry, _runtime, operations),
                    TestDetails.Connection(),
                    _registry, quiet, _navigator);
                _created.Add(details);
                return details;
            });
    }

    public void Dispose()
    {
        foreach (var d in _created)
        {
            d.Dispose();
        }

        _main.Dispose();
        _servers.Dispose();
        _docker.Dispose();
        _temp.Dispose();
    }

    [Fact]
    public void DisposedViewModelsNoLongerFollowLanguageChanges()
    {
        // Leftover subscriptions made windows of finished tests react to a later language change
        // on another thread (seen in CI); in the app they would keep disposed objects alive.
        var raised = new List<string?>();
        _main.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        _docker.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        _servers.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        _main.Dispose();
        _docker.Dispose();
        _servers.Dispose();
        try
        {
            Localizer.Instance.SetLanguage(AppLanguage.Polish);
            Assert.Empty(raised);
        }
        finally
        {
            Localizer.Instance.SetLanguage(AppLanguage.English);
        }
    }

    [Fact]
    public async Task OpeningDetailsSurvivesTheSidebarClearingItsSelection()
    {
        var record = ServerRecord.New(ServerTestData.Vanilla()) with { Phase = ProvisioningPhase.Ready, ContainerId = "c1", NetworkId = "n1" };
        await _registry.CreateAsync(record, TestContext.Current.CancellationToken);
        _runtime.Containers.Add(FakeServerRuntime.State(record.Definition.Id, "running", ContainerHealth.Healthy));
        await _docker.RefreshCommand.ExecuteAsync(null);
        await _servers.RefreshAsync();

        _navigator.ShowServer(record.Definition.Id); // must not throw (this crashed the app)
        var details = Assert.IsType<ServerDetailsViewModel>(_main.CurrentPage);
        Assert.Null(_main.SelectedNavPage);

        _main.SelectedNavPage = null; // what the sidebar ListBox writes back
        Assert.Same(details, _main.CurrentPage);

        _main.SelectedNavPage = _servers; // user clicks "Servers" in the sidebar
        Assert.Same(_servers, _main.CurrentPage);
        Assert.Same(_servers, _main.SelectedNavPage);
    }
}
