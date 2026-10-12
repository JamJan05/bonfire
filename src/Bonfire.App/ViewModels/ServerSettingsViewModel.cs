using System.Collections.ObjectModel;
using System.Globalization;
using Bonfire.App.Localization;
using Bonfire.App.Services;
using Bonfire.Core.Backups;
using Bonfire.Core.Servers;
using Bonfire.Infrastructure.Servers;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Bonfire.App.ViewModels;

/// <summary>
/// Settings editor. Game values come from Bonfire's definition for managed fields and from the
/// server's own server.properties otherwise; saving takes ownership only of the fields the user
/// changed. The network binding (port, exposure, address) and the restart policy are always Bonfire's.
/// </summary>
public sealed partial class ServerSettingsViewModel : ViewModelBase
{
    private readonly Guid _serverId;
    private readonly ServerRegistry _registry;
    private readonly ServerOperationsService _operations;
    private readonly IDialogService _dialogs;
    private readonly IHostAddresses _hostAddresses;
    private readonly IPortAvailability _ports;
    private readonly Func<IReadOnlyList<BackupInfo>>? _listBackups;
    private readonly Localizer _localizer;
    private readonly ILogger _logger;
    private GameSettings? _loadedSettings;
    private Snapshot? _loaded;
    private string? _containerId;
    private string? _resultKey;
    private string? _resultDetail;
    private CancellationTokenSource? _portCheck;
    private int _portGeneration;
    private BackupInfo? _newestBackup;
    private bool _backupsUnreadable;

    public ServerSettingsViewModel(
        Guid serverId,
        ServerRegistry registry,
        ServerOperationsService operations,
        IDialogService dialogs,
        IHostAddresses hostAddresses,
        IPortAvailability ports,
        Func<IReadOnlyList<BackupInfo>>? listBackups,
        Localizer localizer,
        ILogger logger)
    {
        _serverId = serverId;
        _registry = registry;
        _operations = operations;
        _dialogs = dialogs;
        _hostAddresses = hostAddresses;
        _ports = ports;
        _listBackups = listBackups;
        _localizer = localizer;
        _logger = logger;
    }

    /// <summary>Values as shown after loading, to know which fields the user changed.</summary>
    private sealed record Snapshot(
        string Motd, GameMode GameMode, Difficulty Difficulty, bool Pvp, int MaxPlayers, int ViewDistance,
        int SpawnProtection, bool AllowFlight, bool CommandBlocks, bool Whitelist, bool EnforceWhitelist,
        bool OnlineMode, int Port, PortExposure Exposure, string? HostAddress, RestartPolicyKind RestartPolicy);

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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Checklist))]
    private bool _whitelistEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Checklist))]
    private bool _enforceWhitelist;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Checklist))]
    private bool _onlineMode;

    // ---- Network ------------------------------------------------------------------------

    [ObservableProperty]
    private decimal? _port;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLocalhost), nameof(IsAllInterfaces), nameof(IsSpecificAddress), nameof(ShowChecklist), nameof(RestartPolicyLocked))]
    private PortExposure _exposure;

    public bool IsLocalhost
    {
        get => Exposure == PortExposure.Localhost;
        set => SelectExposure(value, PortExposure.Localhost);
    }

    public bool IsAllInterfaces
    {
        get => Exposure == PortExposure.AllInterfaces;
        set => SelectExposure(value, PortExposure.AllInterfaces);
    }

    public bool IsSpecificAddress
    {
        get => Exposure == PortExposure.SpecificAddress;
        set => SelectExposure(value, PortExposure.SpecificAddress);
    }

    private void SelectExposure(bool selected, PortExposure exposure)
    {
        if (selected)
        {
            Exposure = exposure;
        }
    }

    public ObservableCollection<AddressChoice> Addresses { get; } = [];

    [ObservableProperty]
    private AddressChoice? _selectedAddress;

    [ObservableProperty]
    private string? _addressesNote;

    public IReadOnlyList<Choice<RestartPolicyKind>> RestartPolicies =>
        Enum.GetValues<RestartPolicyKind>().Select(p => new Choice<RestartPolicyKind>(p, _localizer[$"Restart_{p}"])).ToList();

    [ObservableProperty]
    private Choice<RestartPolicyKind>? _selectedRestartPolicy;

    /// <summary>See <see cref="NewServerViewModel.RestartPolicyLocked"/>.</summary>
    public bool RestartPolicyLocked => Exposure == PortExposure.SpecificAddress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PortStatus), nameof(PortProblem))]
    private PortCheck? _portState;

    public string? PortStatus => PortState is { } p ? _localizer[$"Port_{p}"] : null;

    public bool PortProblem => PortState is PortCheck.UsedByBonfireServer or PortCheck.UsedByContainer or PortCheck.UsedByHost;

    public bool ShowChecklist => Exposure == PortExposure.AllInterfaces;

    /// <summary>Reflects the unsaved form values and the newest backup; advisory only.</summary>
    public IReadOnlyList<ChecklistItem> Checklist
    {
        get
        {
            var (text, ok) = _backupsUnreadable
                ? (_localizer["Checklist_BackupUnreadable"], false)
                : _newestBackup is { } newest
                    ? (_localizer.Format("Checklist_BackupNewest", newest.CreatedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)), true)
                    : (_localizer["Checklist_BackupNone"], false);
            return ExposureHelpers.Checklist(_localizer, OnlineMode, WhitelistEnabled, EnforceWhitelist, text, ok);
        }
    }

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
        var d = record.Definition;
        var g = d.Game;

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
            g.EnforceWhitelist ?? FileBool("enforce-whitelist", false),
            g.OnlineMode,
            d.Port,
            d.Exposure,
            d.HostAddress,
            d.RestartPolicy);
        _loadedSettings = g;
        _containerId = record.ContainerId;

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
        OnlineMode = _loaded.OnlineMode;
        Port = _loaded.Port;
        Exposure = _loaded.Exposure;
        SelectedRestartPolicy = RestartPolicies.First(p => p.Value == _loaded.RestartPolicy);
        await LoadAddressesAsync(_loaded.HostAddress);
        await LoadBackupsAsync();
        PortState = null;
        IsLoaded = true;
    }

    [RelayCommand]
    private Task RefreshAddressesAsync() => LoadAddressesAsync(SelectedAddress?.Address ?? _loaded?.HostAddress);

    private async Task LoadAddressesAsync(string? keep)
    {
        IReadOnlyList<HostInterface> interfaces;
        string? note = null;
        try
        {
            interfaces = await Task.Run(_hostAddresses.Read);
        }
        catch (HostAddressException ex)
        {
            // The saved address must survive an enumeration failure: a game-only save still carries it.
            _logger.LogWarning(ex, "Could not read the host's addresses");
            interfaces = [];
            note = _localizer["Exposure_AddressesUnavailable"];
        }

        Addresses.Clear();
        foreach (var choice in ExposureHelpers.Choices(interfaces, _localizer))
        {
            Addresses.Add(choice);
        }

        // The saved address stays selectable even while it is absent (VPN down), shown as such; it is never swapped.
        if (keep is not null && Addresses.All(a => a.Address != keep))
        {
            Addresses.Insert(0, new AddressChoice(keep, "", false, _localizer.Format("Exposure_AddressAbsent", keep)));
        }

        SelectedAddress = keep is null ? Addresses.FirstOrDefault() : Addresses.First(a => a.Address == keep);
        AddressesNote = note ?? (Addresses.Count == 0 ? _localizer["Exposure_NoAddresses"] : null);
    }

    private async Task LoadBackupsAsync()
    {
        _newestBackup = null;
        _backupsUnreadable = _listBackups is null;
        if (_listBackups is { } list)
        {
            try
            {
                _newestBackup = (await Task.Run(list)).FirstOrDefault(b => b.IsUsable);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not list backups for the checklist");
                _backupsUnreadable = true;
            }
        }

        OnPropertyChanged(nameof(Checklist));
    }

    partial void OnPortChanged(decimal? value) => CheckPort();

    partial void OnExposureChanged(PortExposure value)
    {
        if (value == PortExposure.SpecificAddress)
        {
            SelectedRestartPolicy = RestartPolicies.First(p => p.Value == RestartPolicyKind.No);
        }

        CheckPort();
    }

    partial void OnSelectedAddressChanged(AddressChoice? value) => CheckPort();

    private string? HostIp => Exposure == PortExposure.SpecificAddress ? SelectedAddress?.Address : PortRules.HostIp(Exposure, null);

    /// <summary>Advisory check of the binding as edited; the server's own record and container are not conflicts.</summary>
    private async void CheckPort()
    {
        _portCheck?.Cancel();
        _portCheck = new CancellationTokenSource();
        var token = _portCheck.Token;
        var generation = ++_portGeneration;
        PortState = null;
        if (_loaded is not { } loaded || Port is not { } port || port < ServerDefinitionValidator.MinPort || port > ServerDefinitionValidator.MaxPort
            || HostIp is not { } hostIp)
        {
            return;
        }

        var currentHostIp = PortRules.HostIp(loaded.Exposure, loaded.HostAddress);
        if ((int)port == loaded.Port && hostIp == currentHostIp)
        {
            return; // unchanged: nothing to check
        }

        try
        {
            var scope = new PortCheckScope(_serverId, _containerId, ProbeHost: !PortRules.Overlaps(hostIp, (int)port, currentHostIp, loaded.Port));
            var state = await _ports.CheckAsync(hostIp, (int)port, scope, token);
            if (generation == _portGeneration)
            {
                PortState = state;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Port check failed");
            if (generation == _portGeneration)
            {
                PortState = PortCheck.Unknown;
            }
        }
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
            OnlineMode = OnlineMode,
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

    /// <summary>Game settings plus the network binding as edited; null while a required field is empty.</summary>
    internal ServerSettingsPatch? BuildPatch()
    {
        if (BuildSettings() is not { } game || _loaded is not { } loaded || Port is not { } port)
        {
            return null;
        }

        var specific = Exposure == PortExposure.SpecificAddress;
        return new ServerSettingsPatch(
            game,
            (int)Math.Clamp(port, 0, int.MaxValue),
            Exposure,
            specific ? SelectedAddress?.Address : null,
            specific ? RestartPolicyKind.No : SelectedRestartPolicy?.Value ?? loaded.RestartPolicy);
    }

    private bool CanSave() => IsLoaded && !IsApplying;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (BuildPatch() is not { } patch)
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
            Task<SettingsApplyResult> Apply() => _operations.ApplySettingsAsync(_serverId, patch, CancellationToken.None);
            var result = IsServerRunning?.Invoke() == true && RunWithStoppingIndicator is { } withIndicator
                ? await withIndicator(Apply)
                : await Apply();
            SetResult(
                $"Settings_Result_{result.Outcome}",
                result.StartError is { } startError ? ErrorText(startError) : null);
            await LoadAsync();
        }
        catch (SettingsValidationException ex)
        {
            SetResult("Settings_Failed", string.Join(" ", ex.Errors.Select(k => _localizer[k])));
        }
        catch (ServerOperationException ex)
        {
            SetResult("Settings_Failed", ErrorText(ex));
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

    private string ErrorText(ServerOperationException ex) =>
        ex.Error is ServerOperationError.AddressNotPresent or ServerOperationError.AddressCheckFailed
            or ServerOperationError.PortNotPublished or ServerOperationError.ContainmentFailed
            ? $"{_localizer[$"Error_{ex.Error}"]} ({ex.Detail})"
            : _localizer[$"Error_{ex.Error}"];

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
        var policy = SelectedRestartPolicy?.Value;
        OnPropertyChanged(nameof(GameModes));
        OnPropertyChanged(nameof(Difficulties));
        OnPropertyChanged(nameof(RestartPolicies));
        SelectedGameMode = GameModes.FirstOrDefault(m => m.Value == mode);
        SelectedDifficulty = Difficulties.FirstOrDefault(d => d.Value == difficulty);
        SelectedRestartPolicy = RestartPolicies.FirstOrDefault(p => p.Value == policy);
        OnPropertyChanged(nameof(ResultMessage));
        OnPropertyChanged(nameof(PortStatus));
        OnPropertyChanged(nameof(Checklist));
    }
}
