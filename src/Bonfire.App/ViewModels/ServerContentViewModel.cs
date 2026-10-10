using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using Bonfire.App.Localization;
using Bonfire.App.Services;
using Bonfire.Core.Content;
using Bonfire.Core.Metadata;
using Bonfire.Core.Servers;
using Bonfire.Infrastructure.Content;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Bonfire.App.ViewModels;

public sealed class ContentItemViewModel(InstalledItem item, string title, string details, string source, string? problem, UpdateInfo? update, string? updateText)
{
    public InstalledItem Item { get; } = item;

    public UpdateInfo? Update { get; } = update;

    public string? UpdateText { get; } = updateText;

    public bool CanUpdate => Update is { Status: UpdateStatus.Available };

    public bool IsPinned => Item.State == ItemState.Tracked && Item.Entry is { Pinned: true };

    public bool CanPin => Item.IsKnown && !IsPinned;

    public string Title { get; } = title;

    public string Details { get; } = details;

    public string Source { get; } = source;

    public string? Problem { get; } = problem;

    public bool IsDisabled => Item.Disabled;

    public bool IsEnabled => !Item.Disabled;
}

public sealed record HistoryItemViewModel(string Title, string Details);

public sealed class SearchHitViewModel(ModrinthProject project, string details, string? versionId = null)
{
    public ModrinthProject Project { get; } = project;

    /// <summary>The exact version to install (an optional dependency may declare one).</summary>
    public string? VersionId { get; } = versionId;

    public string Title => Project.Title;

    public string Description => Project.Description;

    public string Details { get; } = details;
}

/// <summary>
/// Add-ons tab (Fabric mods, Paper plugins): installed files, Modrinth search with an install plan
/// to confirm, local JARs, remove / disable / enable. Changes need a stopped server.
/// </summary>
public sealed partial class ServerContentViewModel : ViewModelBase
{
    private readonly Guid _serverId;
    private readonly ContentService _content;
    private readonly IDialogService _dialogs;
    private readonly IFilePicker _picker;
    private readonly Localizer _localizer;
    private readonly ILogger _logger;
    private CancellationTokenSource? _work;
    private string? _result;
    private int _offset;
    private string _lastQuery = "";
    private SearchSort _lastSort;

    public ServerContentViewModel(
        Guid serverId, ContentService content, IDialogService dialogs, IFilePicker picker, Localizer localizer, ILogger logger)
    {
        _serverId = serverId;
        _content = content;
        _dialogs = dialogs;
        _picker = picker;
        _localizer = localizer;
        _logger = logger;
        Profile = content.ProfileFor(serverId);
    }

    /// <summary>Set by the page: whether the server runs now.</summary>
    public Func<bool>? IsServerRunning { get; set; }

    /// <summary>Set by the page: refresh the server list after a change.</summary>
    public Action? Changed { get; set; }

    public ContentProfile? Profile { get; }

    public bool IsSupported => Profile is not null;

    public string KindText => Profile?.ServerType == ServerType.Paper ? _localizer["Content_Plugins"] : _localizer["Content_Mods"];

    public ObservableCollection<ContentItemViewModel> Items { get; } = [];

    public ObservableCollection<SearchHitViewModel> Results { get; } = [];

    public ObservableCollection<HistoryItemViewModel> History { get; } = [];

    private Guid? _undoTarget;

    [ObservableProperty]
    private bool _hasHistory;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UndoCommand))]
    private bool _canUndo;

    [ObservableProperty]
    private string? _undoBlockerText;

    /// <summary>Optional add-ons suggested by what was just installed.</summary>
    public ObservableCollection<SearchHitViewModel> Suggestions { get; } = [];

    private Dictionary<string, UpdateInfo> _updates = new(StringComparer.Ordinal);

    [ObservableProperty]
    private bool _hasSuggestions;

    [ObservableProperty]
    private bool _includePrereleases;

    [ObservableProperty]
    private int _updatesAvailable;

    public string UpdateAllText => _localizer.Format("Content_UpdateAll", UpdatesAvailable);

    public bool HasUpdates => UpdatesAvailable > 0;

    /// <summary>Offers were made under the other option: they no longer apply.</summary>
    partial void OnIncludePrereleasesChanged(bool value)
    {
        _updates.Clear();
        UpdatesAvailable = 0;
        if (!IsWorking && IsSupported)
        {
            _ = LoadAsync();
        }
    }

    partial void OnUpdatesAvailableChanged(int value)
    {
        OnPropertyChanged(nameof(UpdateAllText));
        OnPropertyChanged(nameof(HasUpdates));
    }

    [ObservableProperty]
    private bool _hasItems;

    [ObservableProperty]
    private bool _hasMore;

    [ObservableProperty]
    private string? _notice;

    [ObservableProperty]
    private string _query = "";

    [ObservableProperty]
    private int _sortIndex;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchCommand), nameof(MoreCommand), nameof(InstallCommand), nameof(RemoveCommand),
        nameof(DisableCommand), nameof(EnableCommand), nameof(AddLocalCommand), nameof(CancelCommand), nameof(LoadCommand),
        nameof(CheckUpdatesCommand), nameof(UpdateCommand), nameof(UpdateAllCommand), nameof(PinCommand), nameof(UnpinCommand),
        nameof(UndoCommand))]
    private bool _isWorking;

    [ObservableProperty]
    private double? _percent;

    public string? ResultMessage => _result;

    private bool CanStart() => !IsWorking && IsSupported;

    [RelayCommand(CanExecute = nameof(CanStart))]
    public Task LoadAsync() => RunAsync(async ct => await RefreshAsync(ct), keepResult: true);

    private async Task RefreshAsync(CancellationToken ct)
    {
        var inventory = await _content.InventoryAsync(_serverId, ct);
        // An update check is only valid for exactly the files it looked at.
        _updates = _updates.Where(u => inventory.Find(u.Value.Item.FileName, false)?.Sha512 == u.Value.Item.Sha512
                                       && inventory.Find(u.Value.Item.FileName, false)?.Entry?.Pinned == u.Value.Item.Entry?.Pinned)
            .ToDictionary(u => u.Key, u => u.Value, StringComparer.Ordinal);
        UpdatesAvailable = _updates.Values.Count(u => u.Status == UpdateStatus.Available);
        Items.Clear();
        foreach (var item in inventory.Items.OrderBy(i => i.Disabled).ThenBy(i => i.Title, StringComparer.CurrentCultureIgnoreCase))
        {
            Items.Add(ToViewModel(item));
        }

        HasItems = Items.Count > 0;
        var notes = new List<string>();
        if (inventory.IdentificationFailed)
        {
            notes.Add(_localizer["Content_IdentificationFailed"]);
        }

        if (inventory.HasUnverifiedActive)
        {
            notes.Add(_localizer["Content_UnverifiedPresent"]);
        }

        if (inventory.Missing.Count > 0)
        {
            notes.Add(_localizer.Format("Content_MissingFiles", string.Join(", ", inventory.Missing.Select(m => m.FileName))));
        }

        if (inventory.Skipped.Count > 0)
        {
            notes.Add(_localizer.Format("Content_SkippedFiles", string.Join(", ", inventory.Skipped)));
        }

        Notice = notes.Count > 0 ? string.Join("\n", notes) : null;
        await LoadHistoryAsync();
    }

    private async Task LoadHistoryAsync()
    {
        var view = await _content.HistoryAsync(_serverId);
        History.Clear();
        foreach (var e in view.Entries)
        {
            History.Add(new HistoryItemViewModel(
                $"{e.Time.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)} — {_localizer[$"Content_Kind_{e.Kind}"]}",
                string.Join(", ", e.Summary)));
        }

        HasHistory = History.Count > 0;
        _undoTarget = view.CanUndoNewest ? view.Entries[0].TxId : null;
        CanUndo = view.CanUndoNewest;
        UndoBlockerText = view.NewestBlocker is { } blocker ? _localizer[$"Content_UndoBlocked_{blocker}"] : null;
    }

    private bool CanRunUndo() => CanStart() && CanUndo;

    [RelayCommand(CanExecute = nameof(CanRunUndo))]
    private async Task UndoAsync()
    {
        if (_undoTarget is not { } target || History.Count == 0 || StopFirst()
            || !await _dialogs.ConfirmAsync(_localizer.Format("Content_ConfirmUndoTitle", History[0].Title), _localizer["Content_ConfirmUndoMessage"],
                _localizer["Content_Undo"], dangerous: true))
        {
            return;
        }

        await RunAsync(async ct =>
        {
            await _content.UndoAsync(_serverId, target, ct);
            _updates.Clear();
            UpdatesAvailable = 0;
            SetResult(_localizer["Content_Undone"]);
            await RefreshAsync(CancellationToken.None);
        }, keepResult: false);
    }

    private ContentItemViewModel ToViewModel(InstalledItem item)
    {
        var parts = new List<string>();
        if (item.VersionNumber is { Length: > 0 } version)
        {
            parts.Add(version);
        }

        parts.Add(item.FileName);
        parts.Add(Size(item.Size));
        var source = item.IsKnown
            ? item.State == ItemState.Tracked && item.Entry?.Origin == ContentOrigin.Installed ? _localizer["Content_Source_Modrinth"] : _localizer["Content_Source_Detected"]
            : item.IsLocal ? _localizer["Content_Source_Local"] : _localizer["Content_Source_Unverified"];
        if (item.Disabled)
        {
            source += " · " + _localizer["Content_Disabled"];
        }

        if (item.State == ItemState.Tracked && item.Entry is { Pinned: true })
        {
            source += " · " + _localizer["Content_PinnedTag"];
        }

        var problem = item.State == ItemState.Modified ? _localizer["Content_Modified"] : null;
        var update = item.Disabled ? null : _updates.GetValueOrDefault(item.FileName);
        var updateText = update?.Status switch
        {
            UpdateStatus.Available => _localizer.Format("Content_UpdateAvailable", update.Latest!.VersionNumber),
            UpdateStatus.UpToDate => _localizer["Content_UpToDate"],
            UpdateStatus.CurrentUnavailable => _localizer["Content_CurrentUnavailable"],
            UpdateStatus.CheckFailed => _localizer["Content_CheckFailed"],
            _ => null,
        };
        return new ContentItemViewModel(item, item.Title, string.Join(" · ", parts), source, problem, update, updateText);
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task SearchAsync()
    {
        _lastQuery = Query;
        _lastSort = SortIndex switch { 1 => SearchSort.Downloads, 2 => SearchSort.Updated, _ => SearchSort.Relevance };
        _offset = 0;
        Results.Clear();
        return RunAsync(SearchPageAsync, keepResult: false);
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task MoreAsync() => RunAsync(SearchPageAsync, keepResult: false);

    private async Task SearchPageAsync(CancellationToken ct)
    {
        // Later pages use the query and order of the search that produced the list.
        var page = await _content.Modrinth.SearchAsync(_lastQuery, Profile!, _lastSort, _offset, ct);
        foreach (var p in page.Hits)
        {
            var details = new List<string>();
            if (p.Author is { Length: > 0 } author)
            {
                details.Add(_localizer.Format("Content_By", author));
            }

            details.Add(_localizer.Format("Content_Downloads", p.Downloads.ToString("N0", CultureInfo.CurrentCulture)));
            if (p.Updated != default)
            {
                details.Add(_localizer.Format("Content_Updated", p.Updated.ToLocalTime().ToString("d", CultureInfo.CurrentCulture)));
            }

            if (p.License is { Length: > 0 } license)
            {
                details.Add(license);
            }

            if (ContentRules.Environment(p.Environment, p.ServerSide) == EnvironmentSupport.ClientOnly)
            {
                details.Add(_localizer["Content_ClientOnly"]);
            }

            Results.Add(new SearchHitViewModel(p, string.Join(" · ", details)));
        }

        _offset += page.Hits.Count;
        HasMore = page.Hits.Count > 0 && _offset < page.Total;
        if (Results.Count == 0)
        {
            SetResult(_localizer["Content_NoResults"]);
        }
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task InstallAsync(SearchHitViewModel? hit) =>
        hit is null
            ? Task.CompletedTask
            : PlanAndApplyAsync(hit.Title, ct => _content.PlanInstallAsync(_serverId, hit.Project.Id, hit.VersionId, ct),
                "Content_ConfirmInstallTitle", "Content_Install", "Content_Installed");

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task CheckUpdatesAsync()
    {
        IReadOnlyList<UpdateInfo>? found = null;
        var prereleases = IncludePrereleases;
        await RunAsync(async ct => found = await _content.CheckUpdatesAsync(_serverId, prereleases, ct), keepResult: false);
        if (found is null)
        {
            return;
        }

        if (prereleases != IncludePrereleases)
        {
            // The option changed meanwhile: these offers do not apply, and the list must not show old ones.
            _updates.Clear();
            UpdatesAvailable = 0;
            await RunAsync(RefreshAsync, keepResult: true);
            return;
        }

        _updates = found.ToDictionary(u => u.Item.FileName, StringComparer.Ordinal);
        UpdatesAvailable = found.Count(u => u.Status == UpdateStatus.Available);
        SetResult(found.Any(u => u.Status == UpdateStatus.CheckFailed) ? _localizer["Content_CheckIncomplete"]
            : UpdatesAvailable > 0 ? _localizer.Format("Content_UpdatesFound", UpdatesAvailable)
            : found.Any(u => u.Status == UpdateStatus.CurrentUnavailable) ? _localizer["Content_NoUpdatesSomeUnavailable"]
            : _localizer["Content_AllUpToDate"]);
        await RunAsync(RefreshAsync, keepResult: true);
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task UpdateAsync(ContentItemViewModel? item) =>
        item?.Update is not { Status: UpdateStatus.Available } update
        || !_updates.TryGetValue(item.Item.FileName, out var current) || !ReferenceEquals(current, update) // only a current offer
            ? Task.CompletedTask
            : PlanAndApplyAsync(item.Title, ct => _content.PlanUpdateAsync(_serverId, [update], ct),
                "Content_ConfirmUpdateTitle", "Content_Update", "Content_UpdateDone");

    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task UpdateAllAsync()
    {
        var all = _updates.Values.Where(u => u.Status == UpdateStatus.Available).ToList();
        return all.Count == 0
            ? Task.CompletedTask
            : PlanAndApplyAsync(_localizer.Format("Content_UpdateAll", all.Count), ct => _content.PlanUpdateAsync(_serverId, all, ct),
                "Content_ConfirmUpdateTitle", "Content_Update", "Content_UpdateDone");
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task PinAsync(ContentItemViewModel? item) => SetPinnedAsync(item, true);

    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task UnpinAsync(ContentItemViewModel? item) => SetPinnedAsync(item, false);

    private Task SetPinnedAsync(ContentItemViewModel? item, bool pinned) =>
        item is null
            ? Task.CompletedTask
            : RunAsync(async ct =>
            {
                await _content.SetPinnedAsync(_serverId, item.Item.FileName, item.Item.Disabled, pinned, ct);
                _updates.Remove(item.Item.FileName); // its update status changes with the pin
                SetResult(_localizer.Format(pinned ? "Content_Pinned" : "Content_Unpinned", item.Title));
                await RefreshAsync(CancellationToken.None);
            }, keepResult: false);

    /// <summary>Plan → show problems or ask for confirmation → download and apply → offer optional add-ons.</summary>
    private async Task PlanAndApplyAsync(string subject, Func<CancellationToken, Task<InstallPlan>> makePlan, string titleKey, string confirmKey, string doneKey)
    {
        if (StopFirst())
        {
            return;
        }

        InstallPlan? plan = null;
        await RunAsync(async ct => plan = await makePlan(ct), keepResult: false);
        if (plan is null)
        {
            return;
        }

        if (!plan.CanInstall)
        {
            SetResult(_localizer.Format("Content_PlanBlocked", subject, string.Join("\n", plan.Problems.Select(ProblemText))));
            return;
        }

        if (!await _dialogs.ConfirmAsync(_localizer.Format(titleKey, subject), PlanText(plan), _localizer[confirmKey], dangerous: plan.Warnings.Count > 0))
        {
            return;
        }

        await RunAsync(async ct =>
        {
            await _content.InstallAsync(_serverId, plan, new Progress<DownloadProgress>(p =>
                Percent = p.BytesTotal > 0 ? Math.Min(100, 100.0 * p.BytesDone / p.BytesTotal) : null), ct);
            SetResult(_localizer.Format(doneKey, subject));
            foreach (var replaced in plan.ReplacedItems)
            {
                _updates.Remove(replaced.FileName);
            }

            UpdatesAvailable = _updates.Values.Count(u => u.Status == UpdateStatus.Available);
            await RefreshAsync(CancellationToken.None);
            await SuggestAsync(plan.OptionalDependencies, CancellationToken.None);
        }, keepResult: false);
    }

    private async Task SuggestAsync(IReadOnlyList<OptionalDependency> optional, CancellationToken ct)
    {
        Suggestions.Clear();
        foreach (var dep in optional.Take(10))
        {
            try
            {
                var project = await _content.Modrinth.GetProjectAsync(dep.ProjectId, ct);
                Suggestions.Add(new SearchHitViewModel(project, _localizer["Content_Suggested"], dep.VersionId));
            }
            catch (MetadataException)
            {
                // a suggestion is not essential
            }
        }

        HasSuggestions = Suggestions.Count > 0;
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task AddLocalAsync()
    {
        if (StopFirst() || await _picker.PickJarAsync(_localizer["Content_PickTitle"]) is not { } path)
        {
            return;
        }

        LocalCandidate? candidate = null;
        await RunAsync(async ct => candidate = await _content.PrepareLocalAsync(_serverId, path, ct), keepResult: false);
        if (candidate is null)
        {
            return;
        }

        var text = new StringBuilder();
        text.AppendLine(_localizer.Format("Content_LocalInfo",
            candidate.Identified?.Name ?? candidate.Info.Name ?? candidate.InstallName,
            candidate.Info.Version ?? "—", candidate.InstallName, Size(candidate.Info.Size)));
        foreach (var warning in candidate.Warnings)
        {
            text.AppendLine("⚠ " + _localizer.Format($"Content_LocalWarning_{warning}", string.Join(", ", candidate.MissingDependencies)));
        }

        if (!await _dialogs.ConfirmAsync(_localizer["Content_ConfirmLocalTitle"], text.ToString().TrimEnd(), _localizer["Content_Install"],
                dangerous: candidate.Warnings.Count > 0))
        {
            await DiscardAsync(candidate);
            return;
        }

        await RunAsync(async ct =>
        {
            try
            {
                await _content.InstallLocalAsync(_serverId, candidate, ct);
            }
            catch
            {
                await DiscardAsync(candidate);
                throw;
            }

            SetResult(_localizer.Format("Content_Installed", candidate.InstallName));
            await RefreshAsync(CancellationToken.None);
        }, keepResult: false);
    }

    private async Task DiscardAsync(LocalCandidate candidate)
    {
        try
        {
            await _content.DiscardLocalAsync(_serverId, candidate);
        }
        catch (Exception ex) when (ex is ServerOperationException or IOException)
        {
            _logger.LogWarning(ex, "Could not discard a staged local file; it is cleaned up later");
        }
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task RemoveAsync(ContentItemViewModel? item)
    {
        if (item is null || StopFirst()
            || !await _dialogs.ConfirmAsync(_localizer.Format("Content_ConfirmRemoveTitle", item.Title),
                _localizer[item.Item.IsKnown ? "Content_ConfirmRemoveMessage" : "Content_ConfirmRemoveUnknownMessage"],
                _localizer["Content_Remove"], dangerous: true))
        {
            return;
        }

        await RunAsync(async ct =>
        {
            await _content.RemoveAsync(_serverId, item.Item.FileName, item.Item.Disabled, ct);
            SetResult(_localizer.Format("Content_Removed", item.Title));
            await RefreshAsync(CancellationToken.None);
        }, keepResult: false);
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task DisableAsync(ContentItemViewModel? item) => SetEnabledAsync(item, false);

    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task EnableAsync(ContentItemViewModel? item) => SetEnabledAsync(item, true);

    private async Task SetEnabledAsync(ContentItemViewModel? item, bool enable)
    {
        if (item is null || StopFirst())
        {
            return;
        }

        await RunAsync(async ct =>
        {
            await _content.SetEnabledAsync(_serverId, item.Item.FileName, enable, ct);
            SetResult(_localizer.Format(enable ? "Content_Enabled" : "Content_DisabledDone", item.Title));
            await RefreshAsync(CancellationToken.None);
        }, keepResult: false);
    }

    private bool CanCancel() => IsWorking;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _work?.Cancel();

    private bool StopFirst()
    {
        if (IsServerRunning?.Invoke() == true)
        {
            SetResult(_localizer["Error_StillRunning"]);
            return true;
        }

        return false;
    }

    private string PlanText(InstallPlan plan)
    {
        var text = new StringBuilder();
        foreach (var old in plan.ReplacedItems)
        {
            text.Append("− ").Append(old.Title).Append(' ').Append(old.VersionNumber).Append(" (").Append(old.FileName).AppendLine(")");
        }

        foreach (var item in plan.Items)
        {
            text.Append("+ ").Append(item.ProjectTitle).Append(' ').Append(item.Version.VersionNumber)
                .Append(" (").Append(Size(item.File.Size)).Append(')');
            if (item.IsDependency)
            {
                text.Append(" — ").Append(_localizer["Content_Dependency"]);
            }

            text.AppendLine();
        }

        text.AppendLine(_localizer.Format("Content_Total", Size(plan.TotalBytes)));
        foreach (var warning in plan.Warnings)
        {
            text.AppendLine("⚠ " + _localizer.Format($"PlanWarning_{warning.Kind}", warning.Subject));
        }

        if (plan.OptionalDependencies.Count > 0)
        {
            text.AppendLine(_localizer.Format("Content_Optional", plan.OptionalDependencies.Count));
        }

        if (plan.ReplacedItems.Count > 0)
        {
            text.AppendLine(_localizer["Content_UpdateNote"]);
        }

        return text.ToString().TrimEnd();
    }

    private string ProblemText(PlanProblem p) => "• " + _localizer.Format($"PlanProblem_{p.Kind}", p.Subject ?? "", p.Other ?? "");

    private int _running;

    /// <summary>Runs work with its own cancellation; overlapping runs (e.g. a reload) never share state.</summary>
    private async Task RunAsync(Func<CancellationToken, Task> work, bool keepResult)
    {
        _running++;
        IsWorking = true;
        Percent = null;
        if (!keepResult)
        {
            SetResult(null);
        }

        var cts = new CancellationTokenSource();
        _work = cts;
        try
        {
            await work(cts.Token);
        }
        catch (OperationCanceledException)
        {
            SetResult(_localizer["Content_Cancelled"]);
        }
        catch (ContentException ex)
        {
            SetResult(_localizer.Format($"ContentError_{ex.Error}", string.Join(", ", ex.Subjects)));
        }
        catch (ContentRecoveryException)
        {
            SetResult(_localizer["Content_RecoveryFailed"]);
        }
        catch (ServerOperationException ex)
        {
            SetResult(_localizer[$"Error_{ex.Error}"]);
        }
        catch (MetadataException ex)
        {
            SetResult(_localizer[$"Content_Network_{ex.Error}"]);
        }
        catch (InvalidJarException)
        {
            SetResult(_localizer["Content_InvalidJar"]);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Add-on operation failed on disk");
            SetResult(_localizer.Format("Content_DiskError", ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Add-on operation failed unexpectedly");
            SetResult(_localizer["Error_Unexpected"]);
        }
        finally
        {
            if (ReferenceEquals(_work, cts))
            {
                _work = null;
            }

            cts.Dispose();
            if (--_running == 0)
            {
                IsWorking = false;
                Percent = null;
            }

            Changed?.Invoke();
        }
    }

    private void SetResult(string? text)
    {
        _result = text;
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
        OnPropertyChanged(nameof(KindText));
        OnPropertyChanged(nameof(UpdateAllText));
        if (!IsWorking && IsSupported)
        {
            _ = LoadAsync();
        }
    }
}
