using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Bonfire.App.Localization;
using Bonfire.App.Services;
using Bonfire.App.ViewModels;
using Bonfire.App.Views;
using Bonfire.Core;
using Bonfire.Core.Docker;
using Bonfire.Core.Metadata;
using Bonfire.Core.Servers;
using Bonfire.Infrastructure;
using Bonfire.Infrastructure.Servers;
using Microsoft.Extensions.Logging.Abstractions;

[assembly: AvaloniaTestApplication(typeof(Bonfire.Tests.HeadlessApp))]

namespace Bonfire.Tests;

public static class HeadlessApp
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<Bonfire.App.App>().UseHeadless(new AvaloniaHeadlessPlatformOptions()).WithInterFont();
}

/// <summary>
/// Real views and bindings in a headless Avalonia: catches failures that ViewModel tests cannot,
/// such as the sidebar ListBox writing back a null selection and closing the page just opened.
/// </summary>
[Collection(LocalizerCollection.Name)]
public sealed class HeadlessViewTests
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
        public Task<bool> ConfirmAsync(string title, string message, string confirmText, bool dangerous) => Task.FromResult(false);

        public Task<bool> OpenAsync(string path) => Task.FromResult(true);

        public Task<bool> OpenUriAsync(Uri uri) => Task.FromResult(true);
    }

    private sealed class FixedMetadata : IServerMetadata
    {
        public Task<IReadOnlyList<GameVersion>> GetReleasesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<GameVersion>>([new GameVersion("26.3", DateTimeOffset.UtcNow)]);

        public Task<ResolvedServerVersion> ResolveAsync(ServerType type, string minecraftVersion, CancellationToken cancellationToken) =>
            Task.FromResult(new ResolvedServerVersion(type, minecraftVersion, 25, null, null, null, true));

        public void Invalidate()
        {
        }
    }

    [Fact]
    public Task DetailsButtonOpensThePageAndEveryTabRenders()
    {
        // One headless Avalonia per assembly; the test body runs on its UI thread.
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(HeadlessApp).Assembly);
        return session.Dispatch(RunAsync, TestContext.Current.CancellationToken);
    }

    private static async Task<bool> RunAsync()
    {
        using var temp = new TempDirectory();
        Localizer.Instance.SetLanguage(AppLanguage.English);
        var app = AppPaths.Resolve(k => k == "HOME" ? temp.Path : null);
        AppDirectories.Ensure(app);
        var registry = new ServerRegistry(app);
        var runtime = new FakeServerRuntime();
        var time = TimeProvider.System;
        using var docker = new DockerStatusViewModel(new ConnectedEngine(), Localizer.Instance, NullLogger<DockerStatusViewModel>.Instance);
        var operations = new ServerOperationsService(registry, runtime, docker, NullLogger<ServerOperationsService>.Instance);
        var quiet = new Quiet();
        var navigator = new Navigator();
        using var servers = new ServersPageViewModel(Localizer.Instance, docker, registry, runtime, operations, quiet, navigator, time,
            NullLogger<ServersPageViewModel>.Instance);
        var store = new SettingsStore(app.SettingsFile);
        var settingsService = new SettingsService(store, store.Load(), NullLogger<SettingsService>.Instance);
        var ports = new ServerCreationTests.FixedPorts();
        var creation = new ServerCreationService(registry, runtime, operations, ports, time, NullLogger<ServerCreationService>.Instance);
        using var main = new MainWindowViewModel(
            Localizer.Instance, docker, servers,
            new SettingsPageViewModel(Localizer.Instance, settingsService),
            new AboutPageViewModel(Localizer.Instance, app, quiet, NullLogger<AboutPageViewModel>.Instance),
            navigator,
            () => new NewServerViewModel(Localizer.Instance, new FixedMetadata(), creation, ports, quiet, navigator, time,
                NullLogger<NewServerViewModel>.Instance, () => null),
            item => new ServerDetailsViewModel(Localizer.Instance, item,
                new ServerLogViewModel(item.ServerId, registry, runtime, Localizer.Instance, time, NullLogger.Instance),
                new ServerConsoleViewModel(item.ServerId, operations, Localizer.Instance, NullLogger.Instance),
                new ServerPlayersViewModel(item.ServerId, operations, Localizer.Instance, time),
                new ServerSettingsViewModel(item.ServerId, registry, operations, quiet, Localizer.Instance, NullLogger.Instance),
                TestDetails.Backups(item.ServerId, registry, runtime, operations, quiet, quiet),
                TestDetails.Content(item.ServerId, registry, runtime, operations),
                registry, quiet, navigator));

        var record = ServerRecord.New(ServerTestData.Vanilla()) with { Phase = ProvisioningPhase.Ready, ContainerId = "c1", NetworkId = "n1" };
        await registry.CreateAsync(record);
        runtime.Containers.Add(FakeServerRuntime.State(record.Definition.Id, "running", ContainerHealth.Healthy));
        await docker.RefreshCommand.ExecuteAsync(null);
        await servers.RefreshAsync();

        var window = new MainWindow { DataContext = main };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // Click "Details" on the server card.
        var details = window.GetVisualDescendants().OfType<Button>()
            .Single(b => ReferenceEquals(b.Command, servers.Servers[0].ShowDetailsCommand));
        window.CaptureRenderedFrame(); // layout, so the button has a position
        var center = details.TranslatePoint(new Point(details.Bounds.Width / 2, details.Bounds.Height / 2), window)!.Value;
        window.MouseDown(center, MouseButton.Left);
        window.MouseUp(center, MouseButton.Left); // a real click, as the user did
        Dispatcher.UIThread.RunJobs();

        var page = Assert.IsType<ServerDetailsViewModel>(main.CurrentPage);
        var tabs = window.GetVisualDescendants().OfType<TabControl>().Single();
        Assert.Equal(7, tabs.ItemCount);
        for (var i = 0; i < tabs.ItemCount; i++)
        {
            tabs.SelectedIndex = i;
            Dispatcher.UIThread.RunJobs();
            window.CaptureRenderedFrame(); // forces layout and rendering of the tab
        }

        Assert.Same(page, main.CurrentPage); // never closed by the sidebar

        // The new-server wizard opens and stays open as well.
        navigator.ShowNewServer();
        Dispatcher.UIThread.RunJobs();
        await Task.Delay(50);
        Dispatcher.UIThread.RunJobs();
        var wizard = Assert.IsType<NewServerViewModel>(main.CurrentPage);
        window.CaptureRenderedFrame();
        Assert.NotEmpty(wizard.Versions);

        // Back to servers by clicking the sidebar item.
        var item = window.GetVisualDescendants().OfType<ListBoxItem>().Single(i => ReferenceEquals(i.DataContext, servers));
        var itemCenter = item.TranslatePoint(new Point(item.Bounds.Width / 2, item.Bounds.Height / 2), window)!.Value;
        window.MouseDown(itemCenter, MouseButton.Left);
        window.MouseUp(itemCenter, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Same(servers, main.CurrentPage);
        Assert.Same(servers, main.SelectedNavPage);
        window.Close();
        return true;
    }

    [Fact]
    public Task ContentTabSearchesAndInstallsWithRealClicks()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(HeadlessApp).Assembly);
        return session.Dispatch(ContentAsync, TestContext.Current.CancellationToken);
    }

    private static void Click(Window window, Control control)
    {
        window.CaptureRenderedFrame();
        var center = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(center, MouseButton.Left);
        window.MouseUp(center, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    private static async Task<bool> ContentAsync()
    {
        using var temp = new TempDirectory();
        Localizer.Instance.SetLanguage(AppLanguage.English);
        var app = AppPaths.Resolve(k => k == "HOME" ? temp.Path : null);
        AppDirectories.Ensure(app);
        var registry = new ServerRegistry(app);
        var runtime = new FakeServerRuntime();
        var time = TimeProvider.System;
        using var docker = new DockerStatusViewModel(new ConnectedEngine(), Localizer.Instance, NullLogger<DockerStatusViewModel>.Instance);
        var operations = new ServerOperationsService(registry, runtime, docker, NullLogger<ServerOperationsService>.Instance);
        var quiet = new Quiet();
        using var servers = new ServersPageViewModel(Localizer.Instance, docker, registry, runtime, operations, quiet, new Navigator(), time,
            NullLogger<ServersPageViewModel>.Instance);
        var definition = ServerTestData.Vanilla() with { Type = ServerType.Fabric, FabricLoaderVersion = "0.19.5", FabricLauncherVersion = "1.1.2" };
        var record = ServerRecord.New(definition) with { Phase = ProvisioningPhase.Ready, ContainerId = "c1", NetworkId = "n1" };
        var paths = await registry.CreateAsync(record);
        runtime.Containers.Add(FakeServerRuntime.State(definition.Id, "exited"));
        await docker.RefreshCommand.ExecuteAsync(null);
        await servers.RefreshAsync();

        var modrinth = new FakeModrinth();
        modrinth.Add("Api", "api1", fileName: "fabric-api.jar");
        modrinth.Add("Mod", "mod1", fileName: "mod.jar", deps: [FakeModrinth.Requires("Api")]);
        var item = servers.Servers[0];
        using var page = new ServerDetailsViewModel(Localizer.Instance, item,
            new ServerLogViewModel(item.ServerId, registry, runtime, Localizer.Instance, time, NullLogger.Instance),
            new ServerConsoleViewModel(item.ServerId, operations, Localizer.Instance, NullLogger.Instance),
            new ServerPlayersViewModel(item.ServerId, operations, Localizer.Instance, time),
            new ServerSettingsViewModel(item.ServerId, registry, operations, quiet, Localizer.Instance, NullLogger.Instance),
            TestDetails.Backups(item.ServerId, registry, runtime, operations, quiet, quiet),
            TestDetails.Content(item.ServerId, registry, runtime, operations, modrinth),
            registry, quiet, new Navigator());

        var window = new Window { Width = 1400, Height = 900, Content = new ServerDetailsView { DataContext = page } };
        window.Show();
        var tabs = window.GetVisualDescendants().OfType<TabControl>().Single();
        tabs.SelectedIndex = ServerDetailsViewModel.ContentTabIndex;
        Dispatcher.UIThread.RunJobs();
        await Until(() => !page.Content.IsWorking);
        Assert.True(page.Content.IsSupported);
        Assert.False(page.Content.HasItems);

        // Search, then install the first hit — all by clicking.
        page.Content.Query = "mod";
        Click(window, window.GetVisualDescendants().OfType<Button>().Single(b => ReferenceEquals(b.Command, page.Content.SearchCommand)));
        await Until(() => page.Content.Results.Count > 0 && !page.Content.IsWorking);
        window.CaptureRenderedFrame();
        var install = window.GetVisualDescendants().OfType<Button>()
            .First(b => ReferenceEquals(b.Command, page.Content.InstallCommand) && b.DataContext is SearchHitViewModel { Project.Id: "Mod" });
        Click(window, install);
        await Until(() => page.Content.HasItems && !page.Content.IsWorking);

        Assert.Equal(["fabric-api.jar", "mod.jar"], Directory.GetFiles(Path.Combine(paths.DataDir, "mods")).Select(f => Path.GetFileName(f)!).Order());
        Assert.Equal(2, page.Content.Items.Count);
        Assert.Contains("Installed", page.Content.ResultMessage, StringComparison.Ordinal);
        window.CaptureRenderedFrame(); // the installed list renders
        Assert.Equal(2, window.GetVisualDescendants().OfType<Button>().Count(b => ReferenceEquals(b.Command, page.Content.RemoveCommand)));

        // A newer version appears: check, then update by clicking.
        modrinth.Add("Mod", "mod2", day: 5, fileName: "mod.jar", deps: [FakeModrinth.Requires("Api")]);
        Click(window, window.GetVisualDescendants().OfType<Button>().Single(b => ReferenceEquals(b.Command, page.Content.CheckUpdatesCommand)));
        await Until(() => page.Content.UpdatesAvailable == 1 && !page.Content.IsWorking);
        window.CaptureRenderedFrame();
        var update = window.GetVisualDescendants().OfType<Button>().Single(b => ReferenceEquals(b.Command, page.Content.UpdateCommand) && b.IsEffectivelyVisible);
        Click(window, update);
        await Until(() => page.Content.UpdatesAvailable == 0 && !page.Content.IsWorking);
        Assert.Equal(["Mod-mod2.jar", "fabric-api.jar"],
            Directory.GetFiles(Path.Combine(paths.DataDir, "mods")).Select(f => Path.GetFileName(f)!).Order(StringComparer.Ordinal));

        // Pin the library by clicking; the item shows it.
        window.CaptureRenderedFrame();
        var pin = window.GetVisualDescendants().OfType<Button>()
            .First(b => ReferenceEquals(b.Command, page.Content.PinCommand) && b.DataContext is ContentItemViewModel { Item.FileName: "fabric-api.jar" });
        Click(window, pin);
        await Until(() => !page.Content.IsWorking && page.Content.Items.Any(i => i.IsPinned));
        window.CaptureRenderedFrame();

        // The pin changed the state: the update cannot be undone until it is unpinned again.
        Assert.False(page.Content.CanUndo);
        Assert.NotNull(page.Content.UndoBlockerText);
        Click(window, window.GetVisualDescendants().OfType<Button>().First(b => ReferenceEquals(b.Command, page.Content.UnpinCommand)));
        await Until(() => !page.Content.IsWorking && page.Content.CanUndo);

        // Open the history and undo the update by clicking.
        window.GetVisualDescendants().OfType<Expander>().Single().IsExpanded = true;
        Dispatcher.UIThread.RunJobs();
        Click(window, window.GetVisualDescendants().OfType<Button>().Single(b => ReferenceEquals(b.Command, page.Content.UndoCommand)));
        await Until(() => !page.Content.IsWorking && File.Exists(Path.Combine(paths.DataDir, "mods", "mod.jar")));
        Assert.Equal(["fabric-api.jar", "mod.jar"],
            Directory.GetFiles(Path.Combine(paths.DataDir, "mods")).Select(f => Path.GetFileName(f)!).Order(StringComparer.Ordinal));
        window.CaptureRenderedFrame();
        window.Close();
        return true;
    }
}
