using System.Globalization;
using Bonfire.App.Localization;
using Bonfire.App.Services;
using Bonfire.Core.Servers;
using Bonfire.Infrastructure.Servers;
using CommunityToolkit.Mvvm.Input;

namespace Bonfire.App.ViewModels;

/// <summary>Details of one server: shared card state and actions, live log and static information.</summary>
public sealed partial class ServerDetailsViewModel : PageViewModel, IDisposable
{
    private readonly ServerRegistry _registry;
    private readonly IFolderLauncher _launcher;
    private readonly Navigator _navigator;
    private bool _disposed;

    public ServerDetailsViewModel(
        Localizer localizer,
        ServerItemViewModel server,
        ServerLogViewModel logs,
        ServerConsoleViewModel console,
        ServerPlayersViewModel players,
        ServerSettingsViewModel settings,
        ServerBackupsViewModel backups,
        ServerContentViewModel content,
        ServerRegistry registry,
        IFolderLauncher launcher,
        Navigator navigator)
        : base(localizer, "Server_Action_Details")
    {
        Server = server;
        Logs = logs;
        Console = console;
        Players = players;
        Settings = settings;
        Settings.IsServerRunning = () => Server.Snapshot.IsActive;
        Settings.RunWithStoppingIndicator = Server.WithStoppingIndicatorAsync;
        Backups = backups;
        Backups.IsServerRunning = () => Server.Snapshot.IsActive;
        Backups.RunWithStoppingIndicator = Server.WithStoppingIndicatorAsync;
        Content = content;
        Content.IsServerRunning = () => Server.Snapshot.IsActive;
        // "stop" typed in the console behaves exactly like the Stop button (indicator, force stop).
        Console.RequestStop = () =>
        {
            if (!Server.StopCommand.CanExecute(null))
            {
                return Task.FromResult(false);
            }

            _ = Server.StopCommand.ExecuteAsync(null);
            return Task.FromResult(true);
        };
        _registry = registry;
        _launcher = launcher;
        _navigator = navigator;
        Server.PropertyChanged += OnServerChanged;
    }

    public ServerItemViewModel Server { get; }

    public ServerLogViewModel Logs { get; }

    public ServerConsoleViewModel Console { get; }

    public ServerPlayersViewModel Players { get; }

    public ServerSettingsViewModel Settings { get; }

    public ServerBackupsViewModel Backups { get; }

    public ServerContentViewModel Content { get; }

    public const int PlayersTabIndex = 2;
    public const int SettingsTabIndex = 3;
    public const int BackupsTabIndex = 4;
    public const int ContentTabIndex = 5;

    private int _selectedTab;

    /// <summary>Players are polled only while their tab is visible.</summary>
    public int SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (SetProperty(ref _selectedTab, value))
            {
                Players.SetActive(value == PlayersTabIndex);
                if (value == SettingsTabIndex && !Settings.IsApplying)
                {
                    _ = Settings.LoadAsync(); // always show what is saved now
                }

                if (value == BackupsTabIndex && !Backups.IsWorking)
                {
                    Backups.Load();
                }

                if (value == ContentTabIndex && Content.LoadCommand.CanExecute(null))
                {
                    Content.LoadCommand.Execute(null);
                }
            }
        }
    }

    public IReadOnlyList<DetailRow> Info
    {
        get
        {
            var rows = new List<DetailRow>();
            var record = Server.Snapshot.Record;
            if (record is null)
            {
                return rows;
            }

            var d = record.Definition;
            var heapMax = d.Resources.HeapMaxMb;
            rows.Add(new(Localizer["Info_Software"], Server.Details));
            rows.Add(new(Localizer["Info_Address"], Server.Address ?? ""));
            rows.Add(new(Localizer["Info_Memory"], Localizer.Format("Info_MemoryValue",
                d.Resources.HeapMinMb, heapMax, d.Resources.ContainerMemoryBytes / (1024 * 1024))));
            rows.Add(new(Localizer["Info_Cpu"], d.Resources.CpuLimit.ToString("0.##", CultureInfo.CurrentCulture)));
            rows.Add(new(Localizer["Info_RestartPolicy"], Localizer[$"Restart_{d.RestartPolicy}"]));
            rows.Add(new(Localizer["Info_Image"], $"{d.Image.Repository}:{d.Image.Tag}@{d.Image.Digest[..19]}…"));
            rows.Add(new(Localizer["Info_ContainerId"], record.ContainerId is { } id ? id[..Math.Min(12, id.Length)] : "—"));
            rows.Add(new(Localizer["Info_Created"], d.CreatedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)));
            rows.Add(new(Localizer["Info_DataDir"], DataDir));
            rows.Add(new(Localizer["Info_Security"], Localizer.Format("Info_SecurityValue", ServerLabels.HardeningProfileVersion)));
            return rows;
        }
    }

    public string DataDir => _registry.PathsFor(Server.ServerId).DataDir;

    [RelayCommand]
    private Task OpenDataDirAsync() => _launcher.OpenAsync(DataDir);

    [RelayCommand]
    private void Back() => _navigator.ShowServers();

    public void Start() => Logs.Start();

    protected override void OnLanguageChanged()
    {
        base.OnLanguageChanged();
        OnPropertyChanged(nameof(Info));
        Settings.OnLanguageChanged();
        Backups.OnLanguageChanged();
        Content.OnLanguageChanged();
    }

    private void OnServerChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ServerItemViewModel.StatusText) or nameof(ServerItemViewModel.Details))
        {
            OnPropertyChanged(nameof(Info));
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DetachFromLocalizer();
        Server.PropertyChanged -= OnServerChanged;
        Logs.Dispose();
        Players.Dispose();
    }
}
