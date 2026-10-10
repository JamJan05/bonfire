using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Bonfire.App.Localization;
using Bonfire.App.Services;
using Bonfire.Infrastructure;
using Bonfire.App.ViewModels;
using Bonfire.App.Views;
using Bonfire.Core.Docker;
using Bonfire.Infrastructure.Docker;
using Bonfire.Infrastructure.Linux;
using Bonfire.Infrastructure.Servers;
using Bonfire.Core.Servers;
using Bonfire.Core.Metadata;
using Bonfire.Infrastructure.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog.Extensions.Logging;

namespace Bonfire.App;

public sealed class App : Application
{
    private readonly Startup? _startup;
    private ServiceProvider? _services;

    // Required by the XAML loader and the designer.
    public App() { }

    public App(Startup? startup) => _startup = startup;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// <summary>Set by <c>--smoke-test</c>: exit after verifying the main window instead of waiting for the user.</summary>
    public static bool SmokeTest { get; set; }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && _startup is not null)
        {
            if (_startup.Succeeded)
            {
                Localizer.Instance.SetLanguage(_startup.Settings!.Settings.Language);
                _services = BuildServices(_startup);
                var mainViewModel = _services.GetRequiredService<MainWindowViewModel>();
                desktop.MainWindow = new MainWindow { DataContext = mainViewModel };
                if (SmokeTest)
                {
                    var window = desktop.MainWindow;
                    window.Opened += async (_, _) => desktop.Shutdown(await SmokeTestAsync(window, mainViewModel));
                }

                // First Docker check runs in the background; the window is usable meanwhile.
                mainViewModel.Docker.RefreshCommand.Execute(null);
                _ = _services.GetRequiredService<ServersPageViewModel>().RunPollingAsync();
                desktop.Exit += (_, _) =>
                {
                    // Let queued settings saves finish (they run on the thread pool, so blocking
                    // here cannot deadlock). The instance lock is held until Main returns.
                    if (!_services.GetRequiredService<SettingsService>().FlushAsync().Wait(TimeSpan.FromSeconds(10)))
                    {
                        _services.GetRequiredService<ILogger<App>>().LogWarning("Settings save did not finish before exit");
                    }

                    _services.Dispose();
                };
            }
            else
            {
                // Settings are not available yet, so the default language (English) is used.
                desktop.MainWindow = new StartupErrorWindow(
                    Localizer.Instance[_startup.ErrorTitleKey!],
                    Localizer.Instance[_startup.ErrorMessageKey!],
                    _startup.ErrorDetail is null ? null : $"{Localizer.Instance["Startup_Details"]}: {_startup.ErrorDetail}");
                if (SmokeTest)
                {
                    desktop.MainWindow.Opened += (_, _) =>
                    {
                        Console.Error.WriteLine($"SMOKE FAIL: startup error window: {_startup.ErrorTitleKey} {_startup.ErrorDetail}");
                        desktop.Shutdown(2);
                    };
                }
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>The main window is up: visit every sidebar page and check the Polish resources load.</summary>
    private static async Task<int> SmokeTestAsync(Avalonia.Controls.Window window, MainWindowViewModel main)
    {
        try
        {
            await Task.Delay(500);
            foreach (var page in main.Pages)
            {
                main.SelectedNavPage = page;
                await Task.Delay(300);
                // The page's view must really be in the window and visible, not just selected in the view model.
                var shown = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window)
                    .OfType<Avalonia.Controls.UserControl>()
                    .Any(c => ReferenceEquals(c.DataContext, page) && c.IsEffectivelyVisible && c.Bounds.Width > 0);
                if (!shown)
                {
                    Console.Error.WriteLine($"SMOKE FAIL: page {page.GetType().Name} not shown");
                    return 3;
                }
            }

            var language = Localizer.Instance.Language;
            Localizer.Instance.SetLanguage(Bonfire.Core.AppLanguage.Polish);
            var polish = Localizer.Instance["Nav_Servers"];
            Localizer.Instance.SetLanguage(language);
            if (polish != "Serwery")
            {
                Console.Error.WriteLine($"SMOKE FAIL: Polish resources missing ({polish})");
                return 4;
            }

            if (Environment.GetEnvironmentVariable("BONFIRE_SMOKE_NETWORK") == "1")
            {
                // TLS through the system OpenSSL and CA certificates, with the production client.
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var project = await new Bonfire.Infrastructure.Content.ModrinthClient(new MetadataHttpClient())
                    .GetProjectAsync("gvQqBUqZ", timeout.Token);
                Console.WriteLine($"SMOKE HTTPS OK ({project.Slug})");
            }

            Console.WriteLine($"SMOKE OK {Bonfire.Core.AppIdentity.Version}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"SMOKE FAIL: {ex}");
            return 5;
        }
    }

    private static ServiceProvider BuildServices(Startup startup)
    {
        var services = new ServiceCollection();

        services.AddLogging(builder => builder.AddProvider(new SerilogLoggerProvider(dispose: false)));
        services.AddSingleton(startup.Paths!);
        services.AddSingleton(Localizer.Instance);
        services.AddSingleton(startup.SettingsStore!);
        services.AddSingleton(startup.Settings!);
        services.AddSingleton<SettingsService>();
        services.AddSingleton<IFolderLauncher, AvaloniaFolderLauncher>();
        services.AddSingleton<IFilePicker, AvaloniaFilePicker>();

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IDockerEngine>(sp => new DockerEngine(
            DockerEndpoint.Resolve(Environment.GetEnvironmentVariable),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<DockerEngine>>()));
        services.AddSingleton<DockerStatusViewModel>();
        services.AddSingleton<IDockerStatusSource>(sp => sp.GetRequiredService<DockerStatusViewModel>());

        // Servers. An unsupported DOCKER_HOST still gets a connection object; every operation is
        // refused by the capability policy because the probe reports the endpoint as unsupported.
        services.AddSingleton(new ServerRegistry(startup.Paths!));
        services.AddSingleton(_ => new DockerConnection(
            DockerEndpoint.Resolve(Environment.GetEnvironmentVariable) ?? new DockerEndpoint(DockerEndpoint.DefaultSocketPath)));
        services.AddSingleton<IServerRuntime>(sp =>
        {
            var registry = sp.GetRequiredService<ServerRegistry>();
            return new DockerServerRuntime(
                sp.GetRequiredService<DockerConnection>(),
                d => registry.PathsFor(d.Id),
                startup.InstallationId,
                HostUser.Uid,
                HostUser.Gid,
                sp.GetRequiredService<ILogger<DockerServerRuntime>>());
        });
        services.AddSingleton<RestoreEngine>();
        services.AddSingleton<ContentEngine>();
        services.AddSingleton(new Bonfire.Infrastructure.Backups.BackupStore(startup.Paths!));
        services.AddSingleton<ServerOperationsService>(sp => new ServerOperationsService(
            sp.GetRequiredService<ServerRegistry>(),
            sp.GetRequiredService<IServerRuntime>(),
            sp.GetRequiredService<IDockerStatusSource>(),
            sp.GetRequiredService<ILogger<ServerOperationsService>>(),
            sp.GetRequiredService<RestoreEngine>(),
            sp.GetRequiredService<ContentEngine>())); // explicit: optional parameters must not silently be null
        services.AddSingleton<BackupService>();
        services.AddSingleton<IPortAvailability, PortAvailability>();
        services.AddSingleton(_ => new MetadataHttpClient());
        services.AddSingleton<IServerMetadata, ServerMetadataService>();
        services.AddSingleton<Bonfire.Core.Content.IModrinth, Bonfire.Infrastructure.Content.ModrinthClient>();
        services.AddSingleton<Func<Bonfire.Infrastructure.Content.ContentDownloader>>(_ => () => new Bonfire.Infrastructure.Content.ContentDownloader());
        services.AddSingleton<ContentService>();
        services.AddSingleton<ServerCreationService>();
        services.AddSingleton<Navigator>();
        services.AddTransient<NewServerViewModel>(sp => ActivatorUtilities.CreateInstance<NewServerViewModel>(sp));
        services.AddSingleton<Func<NewServerViewModel>>(sp => () => sp.GetRequiredService<NewServerViewModel>());
        services.AddSingleton<Func<ServerItemViewModel, ServerDetailsViewModel>>(sp => item => new ServerDetailsViewModel(
            sp.GetRequiredService<Localizer>(),
            item,
            new ServerLogViewModel(
                item.ServerId,
                sp.GetRequiredService<ServerRegistry>(),
                sp.GetRequiredService<IServerRuntime>(),
                sp.GetRequiredService<Localizer>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ILogger<ServerLogViewModel>>()),
            new ServerConsoleViewModel(
                item.ServerId,
                sp.GetRequiredService<ServerOperationsService>(),
                sp.GetRequiredService<Localizer>(),
                sp.GetRequiredService<ILogger<ServerConsoleViewModel>>()),
            new ServerPlayersViewModel(
                item.ServerId,
                sp.GetRequiredService<ServerOperationsService>(),
                sp.GetRequiredService<Localizer>(),
                sp.GetRequiredService<TimeProvider>()),
            new ServerSettingsViewModel(
                item.ServerId,
                sp.GetRequiredService<ServerRegistry>(),
                sp.GetRequiredService<ServerOperationsService>(),
                sp.GetRequiredService<IDialogService>(),
                sp.GetRequiredService<Localizer>(),
                sp.GetRequiredService<ILogger<ServerSettingsViewModel>>())
            {
                Applied = () => _ = sp.GetRequiredService<ServersPageViewModel>().RefreshAsync(),
            },
            new ServerBackupsViewModel(
                item.ServerId,
                sp.GetRequiredService<BackupService>(),
                sp.GetRequiredService<ServerOperationsService>(),
                sp.GetRequiredService<IServerRuntime>(),
                sp.GetRequiredService<ServerRegistry>(),
                sp.GetRequiredService<IDialogService>(),
                sp.GetRequiredService<IFolderLauncher>(),
                sp.GetRequiredService<Localizer>(),
                sp.GetRequiredService<ILogger<ServerBackupsViewModel>>())
            {
                Changed = () => _ = sp.GetRequiredService<ServersPageViewModel>().RefreshAsync(),
            },
            new ServerContentViewModel(
                item.ServerId,
                sp.GetRequiredService<ContentService>(),
                sp.GetRequiredService<IDialogService>(),
                sp.GetRequiredService<IFilePicker>(),
                sp.GetRequiredService<Localizer>(),
                sp.GetRequiredService<ILogger<ServerContentViewModel>>())
            {
                Changed = () => _ = sp.GetRequiredService<ServersPageViewModel>().RefreshAsync(),
            },
            sp.GetRequiredService<ServerRegistry>(),
            sp.GetRequiredService<IFolderLauncher>(),
            sp.GetRequiredService<Navigator>()));
        services.AddSingleton<IDialogService, AvaloniaDialogService>();

        services.AddSingleton<ServersPageViewModel>();
        services.AddSingleton<SettingsPageViewModel>();
        services.AddSingleton<AboutPageViewModel>();
        services.AddSingleton<MainWindowViewModel>();

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }
}
