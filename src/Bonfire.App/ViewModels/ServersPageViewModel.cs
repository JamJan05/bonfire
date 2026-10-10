using System.Collections.ObjectModel;
using Bonfire.App.Localization;
using Bonfire.App.Services;
using Bonfire.Core.Servers;
using Bonfire.Infrastructure.Servers;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Bonfire.App.ViewModels;

/// <summary>
/// Servers page: Docker connection state plus the server list, refreshed by one non-overlapping
/// polling loop. A poll that started before an action completed is discarded, and a failed poll
/// keeps the last known state marked as stale instead of inventing "stopped".
/// </summary>
public sealed partial class ServersPageViewModel : PageViewModel, IDisposable
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    /// <summary>While Docker is unreachable or failing, re-probe it every this many polls.</summary>
    private const int ReprobeEveryPolls = 3;

    /// <summary>Even when everything works, re-check daemon capabilities about once a minute.</summary>
    private const int PeriodicReprobePolls = 12;

    private readonly ServerRegistry _registry;
    private readonly IServerRuntime _runtime;
    private readonly ServerOperationsService _operations;
    private readonly IDialogService _dialogs;
    private readonly Navigator _navigator;
    private readonly TimeProvider _time;
    private readonly ILogger<ServersPageViewModel> _logger;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Lock _pollLock = new();
    private Task? _running;
    private long _mutationVersion;
    private bool _refreshPending;
    private int _disconnectedPolls;
    private int _failedPolls;
    private int _pollsSinceProbe;

    public ServersPageViewModel(
        Localizer localizer,
        DockerStatusViewModel docker,
        ServerRegistry registry,
        IServerRuntime runtime,
        ServerOperationsService operations,
        IDialogService dialogs,
        Navigator navigator,
        TimeProvider time,
        ILogger<ServersPageViewModel> logger)
        : base(localizer, "Nav_Servers")
    {
        _navigator = navigator;
        Docker = docker;
        _registry = registry;
        _runtime = runtime;
        _operations = operations;
        _dialogs = dialogs;
        _time = time;
        _logger = logger;
        // Refresh as soon as Docker becomes reachable instead of waiting for the next tick.
        docker.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DockerStatusViewModel.IsConnected) && docker.IsConnected)
            {
                _ = RefreshAsync();
            }
        };
    }

    public DockerStatusViewModel Docker { get; }

    public ObservableCollection<ServerItemViewModel> Servers { get; } = [];

    [ObservableProperty]
    private bool _hasServers;

    [ObservableProperty]
    private bool _isStale;

    [RelayCommand]
    private void NewServer() => _navigator.ShowNewServer();

    protected override void OnLanguageChanged()
    {
        base.OnLanguageChanged();
        foreach (var item in Servers)
        {
            item.RaiseAll();
        }
    }

    /// <summary>Starts the polling loop; runs until <see cref="Dispose"/>. Call on the UI thread.</summary>
    public async Task RunPollingAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            await RefreshAsync();
            try
            {
                await Task.Delay(PollInterval, _time, _lifetime.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Refreshes the list. Calls while a poll is running are coalesced into one more poll after it;
    /// the returned task completes once that follow-up has run too.
    /// </summary>
    public Task RefreshAsync()
    {
        lock (_pollLock)
        {
            if (_running is { IsCompleted: false })
            {
                _refreshPending = true;
                return _running;
            }

            _running = RunPollsAsync();
            return _running;
        }
    }

    private async Task RunPollsAsync()
    {
        while (true)
        {
            lock (_pollLock)
            {
                _refreshPending = false;
            }

            await PollOnceAsync();

            lock (_pollLock)
            {
                if (!_refreshPending || _lifetime.IsCancellationRequested)
                {
                    return;
                }
            }
        }
    }

    private async Task PollOnceAsync()
    {
        try
        {
            var version = Interlocked.Read(ref _mutationVersion);
            IReadOnlyList<RegistryEntry> registry;
            try
            {
                registry = await Task.Run(_registry.LoadAll, _lifetime.Token);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Loading server records failed");
                MarkStale();
                return;
            }

            IReadOnlyList<ServerContainerState>? containers = null;
            var dockerFailed = false;
            if (Docker.IsConnected)
            {
                _disconnectedPolls = 0;
                try
                {
                    containers = await _runtime.ListAsync(_lifetime.Token);
                    _failedPolls = 0;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning("Listing server containers failed: {Message}", ex.Message);
                    dockerFailed = true;

                    // The daemon may have restarted with different capabilities: stop trusting the
                    // last probe (blocks start/repair) and re-probe now and then every few polls.
                    Docker.Invalidate();
                    if (_failedPolls++ % ReprobeEveryPolls == 0)
                    {
                        Reprobe();
                    }
                }

                if (++_pollsSinceProbe >= PeriodicReprobePolls)
                {
                    Reprobe();
                }
            }
            else if (++_disconnectedPolls % ReprobeEveryPolls == 0)
            {
                // Recover on our own: the poll loop is what notices Docker came back.
                Reprobe();
            }

            if (Interlocked.Read(ref _mutationVersion) != version)
            {
                lock (_pollLock)
                {
                    _refreshPending = true; // an action finished meanwhile; poll again with fresh state
                }

                return;
            }

            Apply(ServerCatalog.Build(registry, containers), stale: dockerFailed || containers is null, dockerKnown: containers is not null);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Reprobe()
    {
        _pollsSinceProbe = 0;
        Docker.RefreshCommand.Execute(null);
    }

    internal void Apply(IReadOnlyList<ServerSnapshot> snapshots, bool stale, bool dockerKnown)
    {
        var byId = Servers.ToDictionary(s => s.ServerId);
        var keep = new HashSet<Guid>();

        foreach (var snapshot in snapshots)
        {
            keep.Add(snapshot.ServerId);
            // Unknown docker state: keep showing the last known status, flagged as stale.
            var effective = snapshot.Status == ServerStatus.Unknown && byId.TryGetValue(snapshot.ServerId, out var known)
                ? known.Snapshot with { Record = snapshot.Record }
                : snapshot;

            if (byId.TryGetValue(snapshot.ServerId, out var item))
            {
                item.Update(effective, stale);
            }
            else
            {
                item = new ServerItemViewModel(effective, _operations, _dialogs, Localizer, _time, _logger, OnMutated)
                {
                    DataLocation = _registry.PathsFor(snapshot.ServerId).DataDir,
                    OpenDetails = _navigator.ShowServer,
                };
                item.Update(effective, stale);
                Servers.Add(item);
            }
        }

        foreach (var gone in Servers.Where(s => !keep.Contains(s.ServerId)).ToList())
        {
            // A card known only from Docker (orphan, conflict) disappears only when Docker says so.
            if (!dockerKnown && gone.Snapshot.Record is null && gone.Snapshot.RecordError is null)
            {
                gone.IsStale = true;
                continue;
            }

            Servers.Remove(gone);
        }

        HasServers = Servers.Count > 0;
        IsStale = stale;
    }

    /// <summary>The live card of a server, shared with its details page.</summary>
    public ServerItemViewModel? Find(Guid serverId) => Servers.FirstOrDefault(s => s.ServerId == serverId);

    private void MarkStale()
    {
        foreach (var item in Servers)
        {
            item.IsStale = true;
        }

        IsStale = true;
    }

    private void OnMutated()
    {
        Interlocked.Increment(ref _mutationVersion);
        _ = RefreshAsync();
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
