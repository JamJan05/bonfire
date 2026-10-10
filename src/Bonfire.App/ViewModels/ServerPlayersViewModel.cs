using Bonfire.App.Localization;
using Bonfire.App.Services;
using Bonfire.Core.Rcon;
using Bonfire.Core.Servers;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bonfire.App.ViewModels;

/// <summary>Players via "list", refreshed only while the Players tab is visible.</summary>
public sealed partial class ServerPlayersViewModel : ViewModelBase, IDisposable
{
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(15);

    private readonly Guid _serverId;
    private readonly ServerOperationsService _operations;
    private readonly Localizer _localizer;
    private readonly TimeProvider _time;
    private CancellationTokenSource? _polling;
    private PlayerList? _players;
    private string? _errorKey;

    public ServerPlayersViewModel(Guid serverId, ServerOperationsService operations, Localizer localizer, TimeProvider time)
    {
        _serverId = serverId;
        _operations = operations;
        _localizer = localizer;
        _time = time;
    }

    [ObservableProperty]
    private bool _isRefreshing;

    public string? CountText => _players switch
    {
        { Online: { } n, Max: { } m } => _localizer.Format("Players_Count", n, m),
        null => null,
        _ => _localizer["Players_Unknown"],
    };

    public IReadOnlyList<string> Names => _players?.Names ?? [];

    public string? NamesNote => _players is { Online: > 0, Names: null } ? _localizer["Players_NamesUnavailable"] : null;

    public string? ErrorText => _errorKey is null ? null : _localizer[_errorKey];

    /// <summary>Starts or stops periodic refreshing (the view calls this when the tab is shown or hidden).</summary>
    public void SetActive(bool active)
    {
        _polling?.Cancel();
        _polling = null;
        if (active)
        {
            _polling = new CancellationTokenSource();
            _ = PollAsync(_polling.Token);
        }
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await RefreshAsync();
                await Task.Delay(RefreshInterval, _time, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsRefreshing)
        {
            return;
        }

        IsRefreshing = true;
        try
        {
            _players = await _operations.QueryPlayersAsync(_serverId, CancellationToken.None);
            _errorKey = null;
        }
        catch (ServerOperationException ex)
        {
            // Busy during start/stop or not running: keep the last list, show why.
            _errorKey = $"Error_{ex.Error}";
        }
        catch (Exception)
        {
            // E.g. the Docker connection dropped: keep the last list; polling continues.
            _errorKey = "Error_Docker";
        }
        finally
        {
            IsRefreshing = false;
            OnPropertyChanged(nameof(CountText));
            OnPropertyChanged(nameof(Names));
            OnPropertyChanged(nameof(NamesNote));
            OnPropertyChanged(nameof(ErrorText));
        }
    }

    public void Dispose() => SetActive(false);
}
