using System.Globalization;
using Bonfire.App.Localization;
using Bonfire.App.Services;
using Bonfire.Core.Servers;
using Bonfire.Infrastructure.Servers;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Bonfire.App.ViewModels;

/// <summary>
/// Settings editor. Values come from Bonfire's definition for managed fields and from the server's
/// own server.properties otherwise; saving takes ownership only of the fields the user changed.
/// </summary>
public sealed partial class ServerSettingsViewModel : ViewModelBase
{
    private readonly Guid _serverId;
    private readonly ServerRegistry _registry;
    private readonly ServerOperationsService _operations;
    private readonly IDialogService _dialogs;
    private readonly Localizer _localizer;
    private readonly ILogger _logger;
    private GameSettings? _loadedSettings;
    private Snapshot? _loaded;
    private string? _resultKey;
    private string? _resultDetail;

    public ServerSettingsViewModel(
        Guid serverId,
        ServerRegistry registry,
        ServerOperationsService operations,
        IDialogService dialogs,
        Localizer localizer,
        ILogger logger)
    {
        _serverId = serverId;
        _registry = registry;
        _operations = operations;
        _dialogs = dialogs;
        _localizer = localizer;
        _logger = logger;
    }

    /// <summary>Values as shown after loading, to know which fields the user changed.</summary>
    private sealed record Snapshot(
        string Motd, GameMode GameMode, Difficulty Difficulty, bool Pvp, int MaxPlayers, int ViewDistance,
        int SpawnProtection, bool AllowFlight, bool CommandBlocks, bool Whitelist, bool EnforceWhitelist);

    /// <summary>Set by the page: whether the server is running now (decides the confirmation).</summary>
    public Func<bool>? IsServerRunning { get; set; }

    /// <summary>
    /// Set by the page: runs the apply with the card's stopping indicator, so a hanging graceful
    /// stop can be escalated with "Force stop" (which aborts the change).
    /// </summary>
    public Func<Func<Task<SettingsApplyResult>>, Task<SettingsApplyResult>>? RunWithStoppingIndicator { get; set; }

    /// <summary>Set by the page: refresh after applying.</summary>
    public Action? Applied { get; set; }

    [ObservableProperty] private string _motd = "";
    [ObservableProperty] private Choice<GameMode>? _selectedGameMode;
    [ObservableProperty] private Choice<Difficulty>? _selectedDifficulty;
    [ObservableProperty] private bool _pvp;
    [ObservableProperty] private decimal? _maxPlayers;
    [ObservableProperty] private decimal? _viewDistance;
    [ObservableProperty] private decimal? _spawnProtection;
    [ObservableProperty] private bool _allowFlight;
    [ObservableProperty] private bool _commandBlocks;
    [ObservableProperty] private bool _whitelistEnabled;
    [ObservableProperty] private bool _enforceWhitelist;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private bool _isApplying;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private bool _isLoaded;

    public IReadOnlyList<Choice<GameMode>> GameModes =>
        Enum.GetValues<GameMode>().Select(m => new Choice<GameMode>(m, _localizer[$"GameMode_{m}"])).ToList();

    public IReadOnlyList<Choice<Difficulty>> Difficulties =>
        Enum.GetValues<Difficulty>().Select(d => new Choice<Difficulty>(d, _localizer[$"Difficulty_{d}"])).ToList();

    public string? ResultMessage => _resultKey is null ? null
        : _resultDetail is null ? _localizer[_resultKey] : _localizer.Format(_resultKey, _resultDetail);

    [RelayCommand]
    public async Task LoadAsync()
    {
        var record = _registry.Load(_serverId)?.Record;
        if (record is null)
        {
            IsLoaded = false;
            return;
        }

        var file = await Task.Run(() => ServerPropertiesFile.Read(_registry.PathsFor(_serverId))) ?? new Dictionary<string, string>();
        var g = record.Definition.Game;

        string FileValue(string key) => file.TryGetValue(key, out var v) ? v : "";
        bool FileBool(string key, bool fallback) => bool.TryParse(FileValue(key), out var b) ? b : fallback;
        int FileInt(string key, int fallback) => int.TryParse(FileValue(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : fallback;
        // Untrusted text: numbers like "4" parse as enums but are not defined modes.
        GameMode FileMode() => Enum.TryParse<GameMode>(FileValue("gamemode"), ignoreCase: true, out var m) && Enum.IsDefined(m)
            ? m
            : GameMode.Survival;

        // Managed fields come from the definition; unmanaged ones from the server's file (or Minecraft's default).
        _loaded = new Snapshot(
            g.Motd ?? FileValue("motd"),
            g.GameMode ?? FileMode(),
            g.Difficulty,
            g.Pvp ?? FileBool("pvp", true),
            g.MaxPlayers,
            g.ViewDistance ?? FileInt("view-distance", 10),
            g.SpawnProtection ?? FileInt("spawn-protection", 16),
            g.AllowFlight ?? FileBool("allow-flight", false),
            g.EnableCommandBlock ?? FileBool("enable-command-block", false),
            g.WhitelistEnabled,
            g.EnforceWhitelist ?? FileBool("enforce-whitelist", false));
        _loadedSettings = g;

        Motd = _loaded.Motd;
        SelectedGameMode = GameModes.First(m => m.Value == _loaded.GameMode);
        SelectedDifficulty = Difficulties.First(d => d.Value == _loaded.Difficulty);
        Pvp = _loaded.Pvp;
        MaxPlayers = _loaded.MaxPlayers;
        ViewDistance = _loaded.ViewDistance;
        SpawnProtection = _loaded.SpawnProtection;
        AllowFlight = _loaded.AllowFlight;
        CommandBlocks = _loaded.CommandBlocks;
        WhitelistEnabled = _loaded.Whitelist;
        EnforceWhitelist = _loaded.EnforceWhitelist;
        IsLoaded = true;
    }

    /// <summary>
    /// The settings to save: a field is managed (gets the shown value) if it already was managed or
    /// the user changed it; otherwise it stays null — saving one field never claims the others.
    /// </summary>
    internal GameSettings? BuildSettings()
    {
        if (_loaded is null || _loadedSettings is not { } g
            || MaxPlayers is not { } players || ViewDistance is not { } view || SpawnProtection is not { } spawn)
        {
            return null;
        }

        static T? Own<T>(T? existing, T loaded, T current)
            where T : struct =>
            existing is not null || !EqualityComparer<T>.Default.Equals(current, loaded) ? current : null;

        return g with
        {
            Difficulty = SelectedDifficulty?.Value ?? g.Difficulty,
            MaxPlayers = (int)Math.Clamp(players, 0, int.MaxValue),
            WhitelistEnabled = WhitelistEnabled,
            Motd = g.Motd is not null || Motd != _loaded.Motd ? Motd : null,
            GameMode = Own(g.GameMode, _loaded.GameMode, SelectedGameMode?.Value ?? _loaded.GameMode),
            Pvp = Own(g.Pvp, _loaded.Pvp, Pvp),
            ViewDistance = Own(g.ViewDistance, _loaded.ViewDistance, (int)Math.Clamp(view, 0, int.MaxValue)),
            SpawnProtection = Own(g.SpawnProtection, _loaded.SpawnProtection, (int)Math.Clamp(spawn, 0, int.MaxValue)),
            AllowFlight = Own(g.AllowFlight, _loaded.AllowFlight, AllowFlight),
            EnableCommandBlock = Own(g.EnableCommandBlock, _loaded.CommandBlocks, CommandBlocks),
            EnforceWhitelist = Own(g.EnforceWhitelist, _loaded.EnforceWhitelist, EnforceWhitelist),
        };
    }

    private bool CanSave() => IsLoaded && !IsApplying;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (BuildSettings() is not { } settings)
        {
            SetResult("Settings_Failed", _localizer["Validation_Required"]);
            return;
        }

        if (IsServerRunning?.Invoke() == true)
        {
            var name = _registry.Load(_serverId)?.Record?.Definition.DisplayName ?? "";
            if (!await _dialogs.ConfirmAsync(
                    _localizer.Format("Settings_ConfirmTitle", name),
                    _localizer.Format("Settings_ConfirmMessage", _serverId.ToString("N")),
                    _localizer["Settings_Save"],
                    dangerous: false))
            {
                return;
            }
        }

        IsApplying = true;
        SetResult("Settings_Applying", null);
        try
        {
            Task<SettingsApplyResult> Apply() => _operations.ApplySettingsAsync(_serverId, settings, CancellationToken.None);
            var result = IsServerRunning?.Invoke() == true && RunWithStoppingIndicator is { } withIndicator
                ? await withIndicator(Apply)
                : await Apply();
            SetResult(
                $"Settings_Result_{result.Outcome}",
                result.StartError is { } startError ? _localizer[$"Error_{startError.Error}"] : null);
            await LoadAsync();
        }
        catch (SettingsValidationException ex)
        {
            SetResult("Settings_Failed", string.Join(" ", ex.Errors.Select(k => _localizer[k])));
        }
        catch (ServerOperationException ex)
        {
            SetResult("Settings_Failed", _localizer[$"Error_{ex.Error}"]);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Applying settings failed unexpectedly");
            SetResult("Settings_Failed", _localizer["Error_Unexpected"]);
        }
        finally
        {
            IsApplying = false;
            Applied?.Invoke();
        }
    }

    private void SetResult(string? key, string? detail)
    {
        _resultKey = key;
        _resultDetail = detail;
        OnPropertyChanged(nameof(ResultMessage));
    }

    public void OnLanguageChanged()
    {
        var mode = SelectedGameMode?.Value;
        var difficulty = SelectedDifficulty?.Value;
        OnPropertyChanged(nameof(GameModes));
        OnPropertyChanged(nameof(Difficulties));
        SelectedGameMode = GameModes.FirstOrDefault(m => m.Value == mode);
        SelectedDifficulty = Difficulties.FirstOrDefault(d => d.Value == difficulty);
        OnPropertyChanged(nameof(ResultMessage));
    }
}
