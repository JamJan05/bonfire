using System.Collections.ObjectModel;
using System.Globalization;
using Bonfire.App.Localization;
using Bonfire.App.Services;
using Bonfire.Core.Backups;
using Bonfire.Core.Servers;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Bonfire.App.ViewModels;

public sealed partial class BackupItemViewModel(BackupInfo info, string title, string details, string? problem, bool isNewestGood) : ObservableObject
{
    public BackupInfo Info { get; } = info;

    public string Title { get; } = title;

    public string Details { get; } = details;

    public string? Problem { get; } = problem;

    public bool CanRestore => Info.IsUsable;

    public bool CanDelete => !IsNewestGood && Info.BackupId != Guid.Empty;

    public bool IsNewestGood { get; } = isNewestGood;
}

/// <summary>Backups tab: list, create (stopping a running server first), restore, delete.</summary>
public sealed partial class ServerBackupsViewModel : ViewModelBase
{
    private readonly Guid _serverId;
    private readonly BackupService _backups;
    private readonly ServerOperationsService _operations;
    private readonly IServerRuntime _runtime;
    private readonly Infrastructure.Servers.ServerRegistry _registry;
    private readonly IDialogService _dialogs;
    private readonly IFolderLauncher _launcher;
    private readonly Localizer _localizer;
    private readonly ILogger _logger;
    private CancellationTokenSource? _work;
    private string? _resultKey;
    private string? _resultDetail;

    public ServerBackupsViewModel(
        Guid serverId,
        BackupService backups,
        ServerOperationsService operations,
        IServerRuntime runtime,
        Infrastructure.Servers.ServerRegistry registry,
        IDialogService dialogs,
        IFolderLauncher launcher,
        Localizer localizer,
        ILogger logger)
    {
        _serverId = serverId;
        _backups = backups;
        _operations = operations;
        _runtime = runtime;
        _registry = registry;
        _dialogs = dialogs;
        _launcher = launcher;
        _localizer = localizer;
        _logger = logger;
    }

    /// <summary>Set by the page: whether the server runs now.</summary>
    public Func<bool>? IsServerRunning { get; set; }

    /// <summary>Set by the page: runs a stop with the card's stopping indicator and Force stop.</summary>
    public Func<Func<Task<bool>>, Task<bool>>? RunWithStoppingIndicator { get; set; }

    /// <summary>Set by the page: refresh the server list after a change.</summary>
    public Action? Changed { get; set; }

    public ObservableCollection<BackupItemViewModel> Backups { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand), nameof(RestoreCommand), nameof(DeleteCommand), nameof(CancelCommand))]
    private bool _isWorking;

    [ObservableProperty]
    private double? _percent;

    [ObservableProperty]
    private bool _hasBackups;

    public string? ResultMessage => _resultKey is null ? null
        : _resultDetail is null ? _localizer[_resultKey] : _localizer.Format(_resultKey, _resultDetail);

    public string? RollbackText => RollbackDir is { } dir ? _localizer.Format("Backups_Rollback", dir) : null;

    /// <summary>The previous data of the newest rollback set (its add-on state sits next to it in "….content").</summary>
    public string? RollbackDir => Directory.EnumerateDirectories(_registry.PathsFor(_serverId).ServerDir, RestoreEngine.RollbackPrefix + "*")
        .Where(d => !d.EndsWith(".content", StringComparison.Ordinal))
        .OrderDescending(StringComparer.Ordinal).FirstOrDefault();

    [RelayCommand]
    public void Load()
    {
        var list = _backups.List(_serverId);
        var newestGood = list.FirstOrDefault(b => b.IsUsable)?.BackupId;
        Backups.Clear();
        foreach (var b in list)
        {
            var title = $"{b.CreatedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)} — {_localizer[$"Backups_Kind_{b.Kind}"]}";
            var details = _localizer.Format("Backups_Size", Size(b.ArchiveBytes), Size(b.DataBytes), b.FileCount);
            var problem = b.IsUsable ? null : _localizer.Format("Backups_Unusable", b.Error);
            Backups.Add(new BackupItemViewModel(b, title, details, problem, b.BackupId == newestGood));
        }

        HasBackups = Backups.Count > 0;
        OnPropertyChanged(nameof(RollbackText));
    }

    private bool CanStart() => !IsWorking;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task CreateAsync()
    {
        var wasRunning = IsServerRunning?.Invoke() == true;
        if (wasRunning && !await _dialogs.ConfirmAsync(
                _localizer.Format("Backups_ConfirmStopTitle", ServerName),
                _localizer.Format("Backups_ConfirmStopMessage", _serverId.ToString("N")),
                _localizer["Backups_Create"],
                dangerous: false))
        {
            return;
        }

        await RunAsync(async ct =>
        {
            // The stop itself is not cancellable (Force stop is the escalation); Cancel only
            // aborts the backup. After a confirmed graceful stop the server is always started
            // again, whatever happens to the backup.
            var stoppedGracefully = false;
            try
            {
                if (wasRunning)
                {
                    await StopAsync(CancellationToken.None);
                    if (!await StoppedCleanlyAsync(CancellationToken.None))
                    {
                        // Killed, crashed or unknown: the world may not be saved — no backup, no restart.
                        SetResult("Backups_ForceStopped", null);
                        return;
                    }

                    stoppedGracefully = true;
                }

                await _backups.CreateAsync(_serverId, BackupKind.Manual, Progress(), ct);
                SetResult("Backups_Created", null);
            }
            finally
            {
                if (stoppedGracefully)
                {
                    await _operations.StartAsync(_serverId, CancellationToken.None);
                }
            }
        });
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task RestoreAsync(BackupItemViewModel? item)
    {
        if (item is null || !item.CanRestore)
        {
            return;
        }

        if (IsServerRunning?.Invoke() == true)
        {
            SetResult("Backups_StopFirst", null);
            return;
        }

        if (!await _dialogs.ConfirmAsync(
                _localizer.Format("Backups_ConfirmRestoreTitle", ServerName, item.Info.CreatedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)),
                _localizer.Format("Backups_ConfirmRestoreMessage", _serverId.ToString("N")),
                _localizer["Backups_Restore"],
                dangerous: true))
        {
            return;
        }

        await RunAsync(async ct =>
        {
            await _backups.RestoreAsync(_serverId, item.Info.BackupId, Progress(), ct);
            // An older backup has no add-on lock or disabled files: the current ones went to the rollback set.
            SetResult(item.Info.Record!.Manifest.HasContent ? "Backups_Restored" : "Backups_RestoredWithoutContent", null);
        });
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task DeleteAsync(BackupItemViewModel? item)
    {
        if (item is null || !item.CanDelete
            || !await _dialogs.ConfirmAsync(
                _localizer.Format("Backups_ConfirmDeleteTitle", item.Info.CreatedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)),
                _localizer["Backups_ConfirmDeleteMessage"],
                _localizer["Backups_Delete"],
                dangerous: true))
        {
            return;
        }

        await RunAsync(async _ =>
        {
            await _backups.DeleteAsync(_serverId, item.Info.BackupId);
            SetResult("Backups_Deleted", null);
        });
    }

    private bool CanCancel() => IsWorking;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _work?.Cancel();

    [RelayCommand]
    private Task OpenRollbackAsync() => RollbackDir is { } dir ? _launcher.OpenAsync(dir) : Task.CompletedTask;

    private string ServerName => _registry.Load(_serverId)?.Record?.Definition.DisplayName ?? "";

    private async Task StopAsync(CancellationToken cancellationToken)
    {
        Task<bool> Stop() => _operations.StopAsync(_serverId, cancellationToken).ContinueWith(t =>
        {
            t.GetAwaiter().GetResult();
            return true;
        }, TaskScheduler.Default);

        _ = RunWithStoppingIndicator is { } withIndicator ? await withIndicator(Stop) : await Stop();
    }

    /// <summary>Only a confirmed stop with exit code 0 counts as graceful (137 = killed, others = failure).</summary>
    private async Task<bool> StoppedCleanlyAsync(CancellationToken cancellationToken)
    {
        var containerId = _registry.Load(_serverId)?.Record?.ContainerId;
        var state = (await _runtime.ListAsync(cancellationToken)).FirstOrDefault(s => s.ContainerId == containerId);
        return state is { IsRunning: false, ExitCode: 0 };
    }

    private Progress<BackupProgress> Progress() =>
        new(p => Percent = p.BytesTotal > 0 ? Math.Min(100, 100.0 * p.BytesDone / p.BytesTotal) : null);

    private async Task RunAsync(Func<CancellationToken, Task> work)
    {
        IsWorking = true;
        Percent = null;
        SetResult(null, null);
        _work = new CancellationTokenSource();
        try
        {
            await work(_work.Token);
        }
        catch (OperationCanceledException)
        {
            SetResult("Backups_Cancelled", null);
        }
        catch (BackupException ex)
        {
            SetResult("Backups_Failed", ex.Error == BackupError.UnsupportedEntry
                ? _localizer.Format("BackupError_UnsupportedEntry", ex.Detail)
                : _localizer[$"BackupError_{ex.Error}"]);
        }
        catch (ServerOperationException ex)
        {
            SetResult("Backups_Failed", _localizer[$"Error_{ex.Error}"]);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Backup operation failed unexpectedly");
            SetResult("Backups_Failed", _localizer["Error_Unexpected"]);
        }
        finally
        {
            IsWorking = false;
            Percent = null;
            _work.Dispose();
            _work = null;
            Load();
            Changed?.Invoke();
        }
    }

    private void SetResult(string? key, string? detail)
    {
        _resultKey = key;
        _resultDetail = detail;
        OnPropertyChanged(nameof(ResultMessage));
    }

    private static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):0} kB",
        _ => $"{bytes} B",
    };

    public void OnLanguageChanged()
    {
        Load();
        OnPropertyChanged(nameof(ResultMessage));
    }
}
