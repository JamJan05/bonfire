using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Bonfire.App.Localization;
using Bonfire.App.Services;
using Bonfire.App.ViewModels;
using Bonfire.App.Views;
using Bonfire.Core;
using Bonfire.Core.Metadata;
using Bonfire.Core.Servers;
using Bonfire.Infrastructure;
using Bonfire.Infrastructure.Servers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bonfire.Tests;

/// <summary>The exposure screens in real views: the wizard's address radio, the settings network card and the connection panel.</summary>
[Collection(LocalizerCollection.Name)]
public sealed class HeadlessNetworkTests
{
    private sealed class Quiet : IDialogService, IFolderLauncher
    {
        public Task<bool> ConfirmAsync(string title, string message, string confirmText, bool dangerous) => Task.FromResult(true);

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
    public Task ConnectionPanelAndNetworkSettingsRenderAndCopyWorksByClicking()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(HeadlessApp).Assembly);
        return session.Dispatch(DetailsAsync, TestContext.Current.CancellationToken);
    }

    [Fact]
    public Task WizardOffersTheAddressModeByClicking()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(HeadlessApp).Assembly);
        return session.Dispatch(WizardAsync, TestContext.Current.CancellationToken);
    }

    private static void Click(Window window, Control control)
    {
        control.BringIntoView(); // the network card sits below the first screenful
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame();
        var center = control.TranslatePoint(new Avalonia.Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(center, Avalonia.Input.MouseButton.Left);
        window.MouseUp(center, Avalonia.Input.MouseButton.Left);
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

    private static async Task<bool> DetailsAsync()
    {
        using var temp = new TempDirectory();
        Localizer.Instance.SetLanguage(AppLanguage.English);
        var app = AppPaths.Resolve(k => k == "HOME" ? temp.Path : null);
        AppDirectories.Ensure(app);
        var registry = new ServerRegistry(app);
        var runtime = new FakeServerRuntime();
        var hosts = FakeHostAddresses.Typical();
        var ports = new ServerCreationTests.FixedPorts();
        var time = TimeProvider.System;
        using var docker = new DockerStatusViewModel(new HeadlessConnectedEngine(), Localizer.Instance, NullLogger<DockerStatusViewModel>.Instance);
        var operations = new ServerOperationsService(registry, runtime, docker, NullLogger<ServerOperationsService>.Instance, hostAddresses: hosts, ports: ports);
        var quiet = new Quiet();
        using var servers = new ServersPageViewModel(Localizer.Instance, docker, registry, runtime, operations, quiet, new Navigator(), time,
            NullLogger<ServersPageViewModel>.Instance);
        var definition = ServerTestData.Vanilla() with { Exposure = PortExposure.SpecificAddress, HostAddress = "2001:db8::20", Port = 25565 };
        var record = ServerRecord.New(definition) with { Phase = ProvisioningPhase.Ready, ContainerId = "c1", NetworkId = "n1" };
        await registry.CreateAsync(record);
        runtime.Containers.Add(FakeServerRuntime.State(definition.Id, "exited"));
        await docker.RefreshCommand.ExecuteAsync(null);
        await servers.RefreshAsync();

        var clipboard = new TestDetails.FakeClipboard();
        var item = servers.Servers[0];
        using var page = new ServerDetailsViewModel(Localizer.Instance, item,
            new ServerLogViewModel(item.ServerId, registry, runtime, Localizer.Instance, time, NullLogger.Instance),
            new ServerConsoleViewModel(item.ServerId, operations, Localizer.Instance, NullLogger.Instance),
            new ServerPlayersViewModel(item.ServerId, operations, Localizer.Instance, time),
            TestDetails.Settings(item.ServerId, registry, operations, quiet, hosts, ports),
            TestDetails.Backups(item.ServerId, registry, runtime, operations, quiet, quiet),
            TestDetails.Content(item.ServerId, registry, runtime, operations),
            TestDetails.Connection(hosts, clipboard),
            registry, quiet, new Navigator());

        var window = new Window { Width = 1400, Height = 900, Content = new ServerDetailsView { DataContext = page } };
        window.Show();
        var tabs = window.GetVisualDescendants().OfType<TabControl>().Single();

        // Information tab: the panel lists the endpoint with brackets and Copy puts exactly that on the clipboard.
        tabs.SelectedIndex = ServerDetailsViewModel.InfoTabIndex;
        Dispatcher.UIThread.RunJobs();
        await Until(() => page.Connection.HasRows && !page.Connection.IsLoading);
        window.CaptureRenderedFrame();
        var copy = window.GetVisualDescendants().OfType<Button>()
            .Single(b => ReferenceEquals(b.Command, page.Connection.CopyCommand) && b.IsEffectivelyVisible);
        Click(window, copy);
        await Until(() => clipboard.Texts.Count == 1);
        Assert.Equal("[2001:db8::20]:25565", clipboard.Texts[0]);
        Assert.Contains("Copied", page.Connection.CopiedText);

        // Settings tab: the network card shows the saved address; switching to 0.0.0.0 by clicking shows the checklist.
        tabs.SelectedIndex = ServerDetailsViewModel.SettingsTabIndex;
        Dispatcher.UIThread.RunJobs();
        await Until(() => page.Settings.IsLoaded);
        window.CaptureRenderedFrame();
        Assert.True(page.Settings.IsSpecificAddress);
        Assert.Equal("2001:db8::20", page.Settings.SelectedAddress?.Address);
        var radios = window.GetVisualDescendants().OfType<RadioButton>().Where(r => r.GroupName == "settings-exposure").ToList();
        Assert.Equal(3, radios.Count);
        Click(window, radios[1]);
        Assert.True(page.Settings.IsAllInterfaces);
        Assert.True(page.Settings.ShowChecklist);
        window.CaptureRenderedFrame();
        Assert.Equal(4, page.Settings.Checklist.Count);
        Assert.True(page.Settings.SaveCommand.CanExecute(null)); // the checklist never blocks

        // Save by clicking: the patch reaches the operations service and the record changes.
        Click(window, window.GetVisualDescendants().OfType<Button>().Single(b => ReferenceEquals(b.Command, page.Settings.SaveCommand)));
        await Until(() => !page.Settings.IsApplying && registry.Load(item.ServerId)!.Record!.Definition.Exposure == PortExposure.AllInterfaces);
        Assert.Null(registry.Load(item.ServerId)!.Record!.Definition.HostAddress);
        window.Close();
        return true;
    }

    private static async Task<bool> WizardAsync()
    {
        using var temp = new TempDirectory();
        Localizer.Instance.SetLanguage(AppLanguage.English);
        var app = AppPaths.Resolve(k => k == "HOME" ? temp.Path : null);
        AppDirectories.Ensure(app);
        var registry = new ServerRegistry(app);
        var runtime = new FakeServerRuntime();
        var hosts = FakeHostAddresses.Typical();
        var ports = new ServerCreationTests.FixedPorts();
        using var docker = new DockerStatusViewModel(new HeadlessConnectedEngine(), Localizer.Instance, NullLogger<DockerStatusViewModel>.Instance);
        var operations = new ServerOperationsService(registry, runtime, docker, NullLogger<ServerOperationsService>.Instance, hostAddresses: hosts, ports: ports);
        var creation = new ServerCreationService(registry, runtime, operations, ports, TimeProvider.System, NullLogger<ServerCreationService>.Instance);
        var quiet = new Quiet();
        using var wizard = new NewServerViewModel(Localizer.Instance, new FixedMetadata(), creation, ports, hosts, quiet, new Navigator(), TimeProvider.System,
            NullLogger<NewServerViewModel>.Instance, () => null);
        await wizard.InitializeAsync();

        var window = new Window { Width = 1400, Height = 1000, Content = new NewServerView { DataContext = wizard } };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame();

        var radios = window.GetVisualDescendants().OfType<RadioButton>().Where(r => r.GroupName == "exposure").ToList();
        Assert.Equal(3, radios.Count);
        Click(window, radios[2]);
        Assert.True(wizard.IsSpecificAddress);
        window.CaptureRenderedFrame(); // the address combo and the locked-restart note render
        Assert.Equal("10.8.0.1", wizard.SelectedAddress?.Address);
        Assert.Contains(window.GetVisualDescendants().OfType<ComboBox>(), c => ReferenceEquals(c.ItemsSource, wizard.Addresses) && c.IsEffectivelyVisible);

        Click(window, radios[1]);
        Assert.True(wizard.IsAllInterfaces);
        window.CaptureRenderedFrame(); // the checklist renders
        Assert.True(wizard.ShowChecklist);
        window.Close();
        return true;
    }
}
