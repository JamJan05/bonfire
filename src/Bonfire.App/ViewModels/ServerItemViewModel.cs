using System.Globalization;
using Bonfire.App.Localization;
using Bonfire.App.Services;
using Bonfire.Core.Servers;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Bonfire.App.ViewModels;

/// <summary>
/// One server card. Instances are stable across polls (keyed by server id) and only receive new
/// snapshots, so bindings and running commands are never torn down by a refresh.
/// </summary>
public sealed partial class ServerItemViewModel : ViewModelBase
{
    /// <summary>After this long a stop offers "Force stop". Purely a UI escalation delay.</summary>
    public static readonly TimeSpan ForceStopOfferDelay = TimeSpan.FromSeconds(60);

    private readonly ServerOperationsService _operations;
    private readonly IDialogService _dialogs;
    private readonly Localizer _localizer;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Action _mutated;

    /// <summary>Set by the page: opens the details of this server.</summary>
    public Action<Guid>? OpenDetails { get; set; }
    private ServerSnapshot _snapshot;
    private string? _errorKey;
    private string? _errorDetail;

    public ServerItemViewModel(
        ServerSnapshot snapshot,
        ServerOperationsService operations,
        IDialogService dialogs,
        Localizer localizer,
        TimeProvider time,
        ILogger logger,
        Action mutated)
    {
        _snapshot = snapshot;
        _operations = operations;
        _dialogs = dialogs;
        _localizer = localizer;
        _time = time;
        _logger = logger;
        _mutated = mutated;
    }

    public Guid ServerId => _snapshot.ServerId;

    public ServerSnapshot Snapshot => _snapshot;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(StopCommand), nameof(RestartCommand),
        nameof(RemoveContainerCommand), nameof(RepairCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isStopping;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ForceStopCommand))]
    private bool _showForceStop;

    [ObservableProperty]
    private bool _isStale;

    public string Name => _snapshot.Record?.Definition.DisplayName ?? _localizer.Format("Server_Id", ShortId);

    public string ShortId => _snapshot.ServerId.ToString("N")[..8];

    public string IdText => _localizer.Format("Server_Id", _snapshot.ServerId.ToString("N"));

    public string Details
    {
        get
        {
            if (_snapshot.Record?.Definition is not { } d)
            {
                return _snapshot.RecordError ?? "";
            }

            var loader = d.Type switch
            {
                ServerType.Fabric => $" (loader {d.FabricLoaderVersion})",
                ServerType.Paper => $" (build {d.PaperBuild})",
                _ => "",
            };
            return $"{d.Type} {d.MinecraftVersion}{loader} · Java {d.JavaMajor}";
        }
    }

    public string? Address => _snapshot.Record?.Definition is { } d
        ? $"{(d.Exposure == PortExposure.Localhost ? "127.0.0.1" : "0.0.0.0")}:{d.Port}"
        : null;

    public string StatusText => _localizer[$"Status_{_snapshot.Status}"];

    // Visibility follows the status only (not IsBusy), so buttons do not jump around while working.
    public bool ShowStart => ServerCatalog.AllowedActions(_snapshot.Status).Contains(ServerAction.Start);

    public bool ShowRepair => ServerCatalog.AllowedActions(_snapshot.Status).Contains(ServerAction.Repair);

    public bool ShowRemoveContainer => ServerCatalog.AllowedActions(_snapshot.Status).Contains(ServerAction.RemoveContainer);

    public bool IsOk => _snapshot.Status == ServerStatus.Running;

    public bool IsWarning => _snapshot.Status is ServerStatus.Starting or ServerStatus.Restarting or ServerStatus.Paused
        or ServerStatus.Incomplete or ServerStatus.UnrecordedContainer or ServerStatus.ContainerMissing or ServerStatus.Orphan;

    public bool IsProblem => _snapshot.Status is ServerStatus.Unhealthy or ServerStatus.Dead or ServerStatus.Conflict
        or ServerStatus.DamagedRecord;

    public string? StateDetail
    {
        get
        {
            var container = _snapshot.Container;
            if (container is null)
            {
                return null;
            }

            if (_snapshot.IsActive && container.StartedAt is { } started)
            {
                return _localizer.Format("Server_Uptime", FormatDuration(_time.GetUtcNow() - started));
            }

            if (container.OomKilled)
            {
                return _localizer["Server_OomKilled"];
            }

            return container.ExitCode is { } code && _snapshot.Status == ServerStatus.Stopped
                ? _localizer.Format("Server_ExitCode", code)
                : null;
        }
    }

    internal string? ErrorKeyForTests => _errorKey;

    public string? ErrorMessage => _errorKey is null
        ? null
        : _errorDetail is null ? _localizer[_errorKey] : $"{_localizer[_errorKey]} ({_errorDetail})";

    public string? StoppingText => !IsStopping ? null
        : ShowForceStop ? _localizer["Server_StoppingLong"] : _localizer["Server_Stopping"];

    public void Update(ServerSnapshot snapshot, bool stale)
    {
        _snapshot = snapshot;
        IsStale = stale;
        RaiseAll();
    }

    public void RaiseAll()
    {
        foreach (var name in new[]
                 {
                     nameof(Name), nameof(IdText), nameof(Details), nameof(Address), nameof(StatusText), nameof(IsOk),
                     nameof(IsWarning), nameof(IsProblem), nameof(StateDetail), nameof(ErrorMessage), nameof(StoppingText),
                     nameof(ShowStart), nameof(ShowRepair), nameof(ShowRemoveContainer),
                 })
        {
            OnPropertyChanged(name);
        }

        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        RestartCommand.NotifyCanExecuteChanged();
        ForceStopCommand.NotifyCanExecuteChanged();
        RemoveContainerCommand.NotifyCanExecuteChanged();
        RepairCommand.NotifyCanExecuteChanged();
    }

    private bool Allowed(ServerAction action) => !IsBusy && ServerCatalog.AllowedActions(_snapshot.Status).Contains(action);

    private bool CanStart() => Allowed(ServerAction.Start);

    private bool CanStop() => Allowed(ServerAction.Stop);

    private bool CanRestart() => Allowed(ServerAction.Restart);

    private bool CanRemoveContainer() => Allowed(ServerAction.RemoveContainer);

    private bool CanRepair() => Allowed(ServerAction.Repair);

    // Available while a stop is pending (that is what it escalates), or for an active server.
    private bool CanForceStop() => ShowForceStop || Allowed(ServerAction.ForceStop);

    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task StartAsync() => RunAsync(ct => _operations.StartAsync(ServerId, ct));

    [RelayCommand(CanExecute = nameof(CanStop))]
    private Task StopAsync() => RunStoppingAsync(ct => _operations.StopAsync(ServerId, ct));

    [RelayCommand(CanExecute = nameof(CanRestart))]
    private Task RestartAsync() => RunStoppingAsync(ct => _operations.RestartAsync(ServerId, ct));

    [RelayCommand(CanExecute = nameof(CanForceStop), AllowConcurrentExecutions = true)]
    private async Task ForceStopAsync()
    {
        var confirmed = await _dialogs.ConfirmAsync(
            _localizer.Format("Confirm_ForceStop_Title", Name),
            _localizer.Format("Confirm_ForceStop_Message", _snapshot.ServerId.ToString("N")),
            _localizer["Server_Action_ForceStop"],
            dangerous: true);
        if (!confirmed)
        {
            return;
        }

        try
        {
            await _operations.ForceStopAsync(ServerId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            _mutated();
        }
    }

    [RelayCommand(CanExecute = nameof(CanRemoveContainer))]
    private async Task RemoveContainerAsync()
    {
        var confirmed = await _dialogs.ConfirmAsync(
            _localizer.Format("Confirm_Remove_Title", Name),
            _localizer.Format("Confirm_Remove_Message", _snapshot.ServerId.ToString("N"), DataLocation),
            _localizer["Server_Action_RemoveContainer"],
            dangerous: true);
        if (confirmed)
        {
            await RunAsync(ct => _operations.RemoveContainerAsync(ServerId, ct));
        }
    }

    [RelayCommand]
    private void ShowDetails() => OpenDetails?.Invoke(ServerId);

    [RelayCommand(CanExecute = nameof(CanRepair))]
    private Task RepairAsync() => RunAsync(ct => _operations.RepairAsync(ServerId, ct));

    /// <summary>Set by the page: where the server's data lives, shown in the removal dialog.</summary>
    public string DataLocation { get; set; } = "";

    private Task RunStoppingAsync(Func<CancellationToken, Task> operation) =>
        WithStoppingIndicatorAsync(async () =>
        {
            await RunAsync(operation);
            return true;
        });

    /// <summary>
    /// Runs an operation that stops the server (stop, restart, settings change) with the stopping
    /// indicator and the delayed "Force stop" offer. Exceptions reach the caller.
    /// </summary>
    public async Task<T> WithStoppingIndicatorAsync<T>(Func<Task<T>> operation)
    {
        IsStopping = true;
        using var offer = new CancellationTokenSource();
        _ = OfferForceStopLaterAsync(offer.Token);
        try
        {
            return await operation();
        }
        finally
        {
            await offer.CancelAsync();
            IsStopping = false;
            ShowForceStop = false;
            OnPropertyChanged(nameof(StoppingText));
        }
    }

    private async Task OfferForceStopLaterAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(ForceStopOfferDelay, _time, cancellationToken);
            ShowForceStop = true;
            OnPropertyChanged(nameof(StoppingText));
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task RunAsync(Func<CancellationToken, Task> operation)
    {
        IsBusy = true;
        _errorKey = null;
        _errorDetail = null;
        OnPropertyChanged(nameof(ErrorMessage));
        try
        {
            await operation(CancellationToken.None);
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            IsBusy = false;
            _mutated();
        }
    }

    private void ShowError(Exception ex)
    {
        if (ex is ServerOperationException op)
        {
            _errorKey = $"Error_{op.Error}";
            _errorDetail = op.Error is ServerOperationError.HardeningMismatch or ServerOperationError.Docker or ServerOperationError.Conflict
                ? Bound(op.Detail)
                : null;
        }
        else
        {
            _logger.LogError(ex, "Server operation failed unexpectedly");
            _errorKey = "Error_Unexpected";
            _errorDetail = ex.GetType().Name;
        }

        OnPropertyChanged(nameof(ErrorMessage));
    }

    private static string Bound(string text) => text.Length <= 300 ? text : text[..300] + "…";

    private static string FormatDuration(TimeSpan span) =>
        span.TotalDays >= 1
            ? string.Format(CultureInfo.CurrentCulture, "{0}d {1}h", (int)span.TotalDays, span.Hours)
            : span.TotalHours >= 1
                ? string.Format(CultureInfo.CurrentCulture, "{0}h {1}min", (int)span.TotalHours, span.Minutes)
                : string.Format(CultureInfo.CurrentCulture, "{0}min", Math.Max(0, (int)span.TotalMinutes));
}
