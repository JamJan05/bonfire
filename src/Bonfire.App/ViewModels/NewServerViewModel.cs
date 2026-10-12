using System.Collections.ObjectModel;
using Bonfire.App.Localization;
using Bonfire.App.Services;
using Bonfire.Core.Metadata;
using Bonfire.Core.Servers;
using Bonfire.Infrastructure.Linux;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Bonfire.App.ViewModels;

/// <summary>Non-generic view of a choice, for XAML item templates.</summary>
public interface IChoice
{
    string Label { get; }
}

public sealed record Choice<T>(T Value, string Label) : IChoice;

/// <summary>A host address offered for publishing the port: "10.8.0.1 — wg0 (VPN)".</summary>
public sealed record AddressChoice(string Address, string Interface, bool LooksLikeVpn, string Label) : IChoice;

/// <summary>One advisory line of the exposure checklist; never blocks anything.</summary>
public sealed record ChecklistItem(string Text, bool Ok)
{
    public string Mark => Ok ? "✓" : "✗";
}

/// <summary>Shared between the wizard and the settings editor: address choices and the 0.0.0.0 checklist.</summary>
internal static class ExposureHelpers
{
    public static List<AddressChoice> Choices(IReadOnlyList<HostInterface> interfaces, Localizer localizer) =>
        HostAddressRules.Suggestions(interfaces)
            .Select(s => new AddressChoice(
                s.Address,
                s.Interface,
                s.LooksLikeVpn,
                s.LooksLikeVpn
                    ? localizer.Format("Exposure_AddressVpn", s.Address, s.Interface)
                    : localizer.Format("Exposure_Address", s.Address, s.Interface)))
            .ToList();

    /// <summary>Online mode, whitelist, enforced whitelist and a backup: read, never enforced.</summary>
    public static List<ChecklistItem> Checklist(Localizer localizer, bool onlineMode, bool whitelist, bool? enforceWhitelist, string backupText, bool backupOk)
    {
        var items = new List<ChecklistItem>
        {
            new(localizer["Checklist_OnlineMode"], onlineMode),
            new(localizer["Checklist_Whitelist"], whitelist),
        };
        if (enforceWhitelist is { } enforce)
        {
            items.Add(new(localizer["Checklist_EnforceWhitelist"], whitelist && enforce));
        }

        items.Add(new(backupText, backupOk));
        return items;
    }
}

/// <summary>
/// The new-server form. Version lookups run asynchronously and a newer lookup always invalidates
/// older ones; "Create" freezes an immutable request and can only run once at a time.
/// </summary>
public sealed partial class NewServerViewModel : PageViewModel, IDisposable
{
    public static readonly Uri EulaUri = new("https://aka.ms/MinecraftEULA");

    private readonly IServerMetadata _metadata;
    private readonly ServerCreationService _creation;
    private readonly IPortAvailability _ports;
    private readonly IHostAddresses _hostAddresses;
    private readonly IFolderLauncher _launcher;
    private readonly Navigator _navigator;
    private readonly TimeProvider _time;
    private readonly ILogger<NewServerViewModel> _logger;
    private readonly Func<MemoryInfo?> _memory;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _resolution;
    private CancellationTokenSource? _portCheck;
    private CancellationTokenSource? _creationCts;
    private int _resolutionGeneration;
    private int _portGeneration;
    private string? _resolutionErrorKey;
    private string? _resolutionErrorDetail;
    private string? _resultKey;
    private string? _resultDetail;

    public NewServerViewModel(
        Localizer localizer,
        IServerMetadata metadata,
        ServerCreationService creation,
        IPortAvailability ports,
        IHostAddresses hostAddresses,
        IFolderLauncher launcher,
        Navigator navigator,
        TimeProvider time,
        ILogger<NewServerViewModel> logger,
        Func<MemoryInfo?>? memory = null)
        : base(localizer, "Wizard_Title")
    {
        _metadata = metadata;
        _creation = creation;
        _ports = ports;
        _hostAddresses = hostAddresses;
        _launcher = launcher;
        _navigator = navigator;
        _time = time;
        _logger = logger;
        _memory = memory ?? (() => HostMemory.Read());
        _cpuLimit = Math.Min(2, Environment.ProcessorCount);
    }

    // ---- Basics -------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary), nameof(Errors), nameof(CanCreate))]
    private string _displayName = "";

    public IReadOnlyList<ServerType> Types { get; } = [ServerType.Vanilla, ServerType.Fabric, ServerType.Paper];

    [ObservableProperty]
    private ServerType _selectedType = ServerType.Vanilla;

    public ObservableCollection<GameVersion> Versions { get; } = [];

    [ObservableProperty]
    private GameVersion? _selectedVersion;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ResolvedText), nameof(ShowPaperUnsupported), nameof(ShowFabricNote), nameof(Summary), nameof(Errors), nameof(CanCreate))]
    private ResolvedServerVersion? _resolved;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCreate))]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand))]
    private bool _isResolving;

    public string? ResolvedText => Resolved switch
    {
        null => null,
        { Type: ServerType.Fabric } r => Localizer.Format("Wizard_Resolved_Fabric", r.FabricLoaderVersion, r.FabricLauncherVersion, r.JavaMajor),
        { Type: ServerType.Paper } r => Localizer.Format("Wizard_Resolved_Paper", r.PaperBuild, r.JavaMajor),
        var r => Localizer.Format("Wizard_Resolved_Vanilla", r.MinecraftVersion, r.JavaMajor),
    };

    public string? ResolutionError => _resolutionErrorKey is null ? null
        : _resolutionErrorKey == "Metadata_Unsupported" ? Localizer.Format(_resolutionErrorKey, _resolutionErrorDetail)
        : Localizer[_resolutionErrorKey];

    public bool ShowPaperUnsupported => Resolved is { Type: ServerType.Paper, PaperVersionSupported: false };

    public bool ShowFabricNote => Resolved?.Type == ServerType.Fabric;

    // ---- Resources ----------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary), nameof(Errors), nameof(CanCreate), nameof(ContainerMemoryText), nameof(MemoryWarning))]
    private decimal? _heapMinMb = 1024;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary), nameof(Errors), nameof(CanCreate), nameof(ContainerMemoryText), nameof(MemoryWarning))]
    private decimal? _heapMaxMb = 2048;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary), nameof(Errors), nameof(CanCreate))]
    private decimal? _cpuLimit;

    public decimal MaxCpu => Environment.ProcessorCount;

    public string? ContainerMemoryText => Resources() is { } r
        ? Localizer.Format("Wizard_ContainerMemory", r.ContainerMemoryBytes / (1024 * 1024))
        : null;

    /// <summary>Advisory only: compares the whole container limit with memory available right now.</summary>
    public string? MemoryWarning => Resources() is { } r && _memory() is { } m && r.ContainerMemoryBytes > m.AvailableBytes
        ? Localizer.Format("Wizard_MemoryHigh", m.AvailableBytes / (1024 * 1024))
        : null;

    // ---- Network ------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary), nameof(Errors), nameof(CanCreate))]
    private decimal? _port = PortRules.SearchStart;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary), nameof(Errors), nameof(CanCreate), nameof(IsLocalhost), nameof(IsAllInterfaces),
        nameof(IsSpecificAddress), nameof(ShowChecklist), nameof(RestartPolicyLocked))]
    private PortExposure _exposure = PortExposure.Localhost;

    // Three radio buttons over one value; setting one to true selects it, "false" (the other radio) is ignored.
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

    /// <summary>Host addresses the port can be published on (VPN-looking ones first); refreshed on demand.</summary>
    public ObservableCollection<AddressChoice> Addresses { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary), nameof(Errors), nameof(CanCreate))]
    private AddressChoice? _selectedAddress;

    [ObservableProperty]
    private string? _addressesNote;

    /// <summary>Advisory checklist for 0.0.0.0: read from the form, never enforced.</summary>
    public bool ShowChecklist => Exposure == PortExposure.AllInterfaces;

    public IReadOnlyList<ChecklistItem> Checklist =>
        ExposureHelpers.Checklist(Localizer, OnlineMode, WhitelistEnabled, null, Localizer["Checklist_BackupNewServer"], false);

    /// <summary>
    /// A specific address forbids automatic restarts: a daemon restart before the VPN is up fails
    /// the bind, after which Docker runs the container without publishing the port at all.
    /// </summary>
    public bool RestartPolicyLocked => Exposure == PortExposure.SpecificAddress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PortStatus))]
    private PortCheck? _portState;

    public string? PortStatus => PortState is { } p ? Localizer[$"Port_{p}"] : null;

    public bool PortProblem => PortState is PortCheck.UsedByBonfireServer or PortCheck.UsedByContainer or PortCheck.UsedByHost;

    // ---- Game ---------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary), nameof(Checklist))]
    private bool _onlineMode = true;

    public IReadOnlyList<Choice<Difficulty>> Difficulties =>
        Enum.GetValues<Difficulty>().Select(d => new Choice<Difficulty>(d, Localizer[$"Difficulty_{d}"])).ToList();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    private Choice<Difficulty>? _selectedDifficulty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary), nameof(Errors), nameof(CanCreate))]
    private decimal? _maxPlayers = 10;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary), nameof(Errors), nameof(CanCreate), nameof(WhitelistEmptyNote), nameof(Checklist))]
    private bool _whitelistEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary), nameof(Errors), nameof(CanCreate), nameof(WhitelistEmptyNote))]
    private string _whitelistText = "";

    public string? WhitelistEmptyNote => WhitelistEnabled && WhitelistEntries().Count == 0 ? Localizer["Wizard_WhitelistEmpty"] : null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary), nameof(Errors), nameof(CanCreate))]
    private string _seed = "";

    public IReadOnlyList<Choice<RestartPolicyKind>> RestartPolicies =>
        Enum.GetValues<RestartPolicyKind>().Select(p => new Choice<RestartPolicyKind>(p, Localizer[$"Restart_{p}"])).ToList();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    private Choice<RestartPolicyKind>? _selectedRestartPolicy;

    [ObservableProperty]
    private bool _startAfterCreate = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Errors), nameof(CanCreate))]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand))]
    private bool _eulaAccepted; // deliberately unchecked by default

    // ---- Creation -----------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCreate))]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand), nameof(CancelCreationCommand))]
    private bool _isCreating;

    [ObservableProperty]
    private string? _phaseText;

    [ObservableProperty]
    private double? _pullPercent;

    [ObservableProperty]
    private bool _showGoToServers;

    public string? ResultMessage => _resultKey is null ? null
        : _resultDetail is null ? Localizer[_resultKey] : Localizer.Format(_resultKey, _resultDetail);

    public IReadOnlyList<string> Errors => CurrentRequest(DateTimeOffset.UtcNow) is { } request
        ? _creation.Validate(request).Select(k => Localizer[k]).ToList()
        : Resolved is null ? [] : [Localizer["Validation_Required"]]; // e.g. a cleared number field

    public bool CanCreate => !IsCreating && !IsResolving && Resolved is not null && EulaAccepted
        && CurrentRequest(DateTimeOffset.UtcNow) is not null && Errors.Count == 0;

    public IReadOnlyList<string> Summary
    {
        get
        {
            var lines = new List<string>();
            if (ResolvedText is { } resolved)
            {
                lines.Add($"{DisplayName} — {resolved}");
            }

            if (Resources() is { } r)
            {
                lines.Add($"{Localizer["Wizard_Resources"]}: {r.HeapMinMb}–{r.HeapMaxMb} MB, CPU {r.CpuLimit:0.##}");
            }

            if (Port is { } p && HostIp is { } hostIp)
            {
                lines.Add($"{Localizer["Wizard_Port"]}: {HostAddressRules.Endpoint(hostIp, (int)Math.Clamp(p, 0, int.MaxValue))}");
            }

            lines.Add($"{Localizer["Wizard_OnlineMode"]}: {(OnlineMode ? "✓" : "✗")}");
            if (WhitelistEnabled)
            {
                lines.Add($"{Localizer["Wizard_Whitelist"]}: {WhitelistEntries().Count}");
            }

            if (SelectedRestartPolicy is { } policy)
            {
                lines.Add($"{Localizer["Wizard_RestartPolicy"]}: {policy.Label}");
            }

            return lines;
        }
    }

    /// <summary>The binding's host address, or null while a specific address is chosen but none selected.</summary>
    private string? HostIp => Exposure == PortExposure.SpecificAddress ? SelectedAddress?.Address : PortRules.HostIp(Exposure, null);

    /// <summary>Loads versions, the host's addresses and a free port. Call once when the page is shown.</summary>
    public async Task InitializeAsync()
    {
        if (_disposed)
        {
            return;
        }

        SelectedDifficulty = Difficulties.First(d => d.Value == Difficulty.Normal);
        SelectedRestartPolicy = RestartPolicies.First(p => p.Value == RestartPolicyKind.No);
        await Task.WhenAll(LoadVersionsAsync(), FindPortAsync(), LoadAddressesAsync());
    }

    [RelayCommand]
    private async Task RefreshAddressesAsync() => await LoadAddressesAsync();

    private async Task LoadAddressesAsync()
    {
        IReadOnlyList<HostInterface> interfaces;
        try
        {
            interfaces = await Task.Run(_hostAddresses.Read, _lifetime.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (HostAddressException ex)
        {
            _logger.LogWarning(ex, "Could not read the host's addresses");
            AddressesNote = Localizer["Exposure_AddressesUnavailable"];
            return;
        }

        // Captured before Clear(): the ComboBox's two-way binding nulls the selection when its items go.
        var previous = SelectedAddress?.Address;
        Addresses.Clear();
        foreach (var choice in ExposureHelpers.Choices(interfaces, Localizer))
        {
            Addresses.Add(choice);
        }

        // A selected address that vanished stays selected, marked as absent — never silently replaced by another one.
        if (previous is not null && Addresses.All(a => a.Address != previous))
        {
            Addresses.Insert(0, new AddressChoice(previous, "", false, Localizer.Format("Exposure_AddressAbsent", previous)));
        }

        SelectedAddress = previous is null ? Addresses.FirstOrDefault() : Addresses.First(a => a.Address == previous);
        AddressesNote = Addresses.Count == 0 ? Localizer["Exposure_NoAddresses"] : null;
    }

    [RelayCommand]
    private async Task RefreshVersionsAsync()
    {
        _metadata.Invalidate();
        await LoadVersionsAsync();
    }

    private async Task LoadVersionsAsync()
    {
        try
        {
            var releases = await _metadata.GetReleasesAsync(_lifetime.Token);
            var previous = SelectedVersion?.Id;
            Versions.Clear();
            foreach (var r in releases)
            {
                Versions.Add(r);
            }

            var target = Versions.FirstOrDefault(v => v.Id == previous) ?? Versions.FirstOrDefault();
            if (Equals(SelectedVersion, target))
            {
                Resolve(); // same selection: the change handler will not fire, so look up explicitly
            }
            else
            {
                SelectedVersion = target; // the change handler starts the lookup
            }
        }
        catch (MetadataException ex)
        {
            SetResolutionError(ex);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task FindPortAsync()
    {
        try
        {
            if (HostIp is { } hostIp && await _ports.FindFreeAsync(hostIp, _lifetime.Token) is { } free)
            {
                Port = free;
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    partial void OnSelectedTypeChanged(ServerType value) => Resolve();

    partial void OnSelectedVersionChanged(GameVersion? value) => Resolve();

    partial void OnPortChanged(decimal? value) => CheckPort();

    partial void OnExposureChanged(PortExposure value)
    {
        if (value == PortExposure.SpecificAddress)
        {
            SelectedRestartPolicy = RestartPolicies.First(p => p.Value == RestartPolicyKind.No); // visible, not rewritten on save
        }

        CheckPort();
    }

    partial void OnSelectedAddressChanged(AddressChoice? value) => CheckPort();

    /// <summary>Starts a lookup; any older lookup's result is discarded when it arrives.</summary>
    private async void Resolve()
    {
        _resolution?.Cancel();
        _resolution = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var token = _resolution.Token;
        var generation = ++_resolutionGeneration;

        Resolved = null;
        _resolutionErrorKey = null;
        OnPropertyChanged(nameof(ResolutionError));
        if (SelectedVersion is not { } version)
        {
            return;
        }

        IsResolving = true;
        try
        {
            var resolved = await _metadata.ResolveAsync(SelectedType, version.Id, token);
            if (generation == _resolutionGeneration)
            {
                Resolved = resolved;
            }
        }
        catch (Exception ex)
        {
            // async void: nothing may escape. A superseded lookup's failure is simply discarded.
            if (generation == _resolutionGeneration && ex is not OperationCanceledException)
            {
                if (ex is MetadataException metadataError)
                {
                    SetResolutionError(metadataError);
                }
                else
                {
                    _logger.LogError(ex, "Version lookup failed unexpectedly");
                    _resolutionErrorKey = "Metadata_InvalidResponse";
                    OnPropertyChanged(nameof(ResolutionError));
                }
            }
        }
        finally
        {
            if (generation == _resolutionGeneration)
            {
                IsResolving = false;
            }
        }
    }

    private async void CheckPort()
    {
        _portCheck?.Cancel();
        _portCheck = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var token = _portCheck.Token;
        var generation = ++_portGeneration;
        PortState = null;
        if (Port is not { } port || port < ServerDefinitionValidator.MinPort || port > ServerDefinitionValidator.MaxPort || HostIp is not { } hostIp)
        {
            return;
        }

        try
        {
            var state = await _ports.CheckAsync(hostIp, (int)port, PortCheckScope.All, token);
            if (generation == _portGeneration)
            {
                PortState = state;
                OnPropertyChanged(nameof(PortProblem));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Port check failed");
            if (generation == _portGeneration)
            {
                PortState = PortCheck.Unknown;
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void SetResolutionError(MetadataException ex)
    {
        _resolutionErrorKey = $"Metadata_{ex.Error}";
        _resolutionErrorDetail = ex.Detail;
        OnPropertyChanged(nameof(ResolutionError));
    }

    [RelayCommand]
    private Task OpenEulaAsync() => _launcher.OpenUriAsync(EulaUri);

    [RelayCommand]
    private void Back() => _navigator.ShowServers();

    [RelayCommand]
    private void GoToServers() => _navigator.ShowServers();

    private bool CanRunCreate() => CanCreate;

    [RelayCommand(CanExecute = nameof(CanRunCreate))]
    private async Task CreateAsync()
    {
        // Frozen now: later edits or lookups cannot change what is being created.
        if (!CanCreate || CurrentRequest(_time.GetUtcNow()) is not { } request)
        {
            return;
        }

        IsCreating = true;
        ShowGoToServers = false;
        SetResult(null, null);
        _creationCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var progress = new Progress<CreationProgress>(OnProgress);
        try
        {
            var result = await _creation.CreateAsync(request, progress, _creationCts.Token);
            if (result.StartCancelled)
            {
                SetResult("Wizard_CreatedStartUnknown", null);
                ShowGoToServers = true;
            }
            else if (result.StartError is { } startError)
            {
                // Address and publication problems carry the specific fact (which address, what Docker did).
                var detail = startError.Error is ServerOperationError.AddressNotPresent or ServerOperationError.AddressCheckFailed
                    or ServerOperationError.PortNotPublished or ServerOperationError.ContainmentFailed
                    ? $"{Localizer[$"Error_{startError.Error}"]} ({startError.Detail})"
                    : Localizer[$"Error_{startError.Error}"];
                SetResult("Wizard_CreatedNotStarted", detail);
                ShowGoToServers = true;
            }
            else
            {
                _navigator.ShowServers();
            }
        }
        catch (OperationCanceledException)
        {
            SetResult("Wizard_Cancelled_NothingCreated", null);
        }
        catch (CreationInterruptedException ex) when (ex.InnerException is OperationCanceledException)
        {
            SetResult("Wizard_Cancelled_Incomplete", null);
            ShowGoToServers = true;
        }
        catch (CreationInterruptedException ex)
        {
            SetResult("Wizard_Failed", ex.InnerException is ServerOperationException op ? Localizer[$"Error_{op.Error}"] : Localizer["Error_Unexpected"]);
            ShowGoToServers = true;
        }
        catch (CreationValidationException ex)
        {
            SetResult("Wizard_Failed", string.Join(" ", ex.Errors.Select(k => Localizer[k])));
        }
        catch (ServerOperationException ex)
        {
            SetResult("Wizard_Failed", Localizer[$"Error_{ex.Error}"]);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Creating a server failed unexpectedly");
            SetResult("Wizard_Failed", Localizer["Error_Unexpected"]);
        }
        finally
        {
            IsCreating = false;
            PhaseText = null;
            PullPercent = null;
            _creationCts.Dispose();
            _creationCts = null;
        }
    }

    private bool CanCancelCreation() => IsCreating;

    [RelayCommand(CanExecute = nameof(CanCancelCreation))]
    private void CancelCreation() => _creationCts?.Cancel();

    private readonly Dictionary<string, (long Current, long Total)> _layers = [];

    private void OnProgress(CreationProgress progress)
    {
        PhaseText = Localizer[$"Wizard_Phase_{progress.Phase}"];
        if (progress.Pull is { Layer: { } layer, Total: > 0 } pull)
        {
            _layers[layer] = (pull.Current ?? 0, pull.Total!.Value);
            var total = _layers.Values.Sum(l => l.Total);
            PullPercent = total > 0 ? 100.0 * _layers.Values.Sum(l => Math.Min(l.Current, l.Total)) / total : null;
        }
    }

    private void SetResult(string? key, string? detail)
    {
        _resultKey = key;
        _resultDetail = detail;
        OnPropertyChanged(nameof(ResultMessage));
    }

    private ResourceLimits? Resources() =>
        HeapMinMb is { } min && HeapMaxMb is { } max && CpuLimit is { } cpu
            ? new ResourceLimits((int)Math.Clamp(min, 0, int.MaxValue), (int)Math.Clamp(max, 0, int.MaxValue), (double)cpu)
            : null;

    private List<string> WhitelistEntries() =>
        WhitelistText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    /// <summary>The request the current form describes, or null while required parts are missing.</summary>
    private CreationRequest? CurrentRequest(DateTimeOffset eulaAcceptedAt)
    {
        if (Resolved is not { } resolved || Resources() is not { } resources || Port is not { } port || MaxPlayers is not { } players)
        {
            return null;
        }

        return new CreationRequest(
            DisplayName,
            resolved,
            resources,
            (int)Math.Clamp(port, 0, int.MaxValue),
            Exposure,
            RestartPolicyLocked ? RestartPolicyKind.No : SelectedRestartPolicy?.Value ?? RestartPolicyKind.No,
            new GameSettings(
                OnlineMode,
                SelectedDifficulty?.Value ?? Difficulty.Normal,
                (int)Math.Clamp(players, 0, int.MaxValue),
                WhitelistEnabled,
                WhitelistEnabled ? WhitelistEntries() : [],
                string.IsNullOrWhiteSpace(Seed) ? null : Seed.Trim()),
            EulaAccepted ? eulaAcceptedAt : default,
            StartAfterCreate,
            Exposure == PortExposure.SpecificAddress ? SelectedAddress?.Address : null);
    }

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName == nameof(CanCreate))
        {
            CreateCommand.NotifyCanExecuteChanged();
        }
    }

    protected override void OnLanguageChanged()
    {
        base.OnLanguageChanged();
        var difficulty = SelectedDifficulty?.Value;
        var policy = SelectedRestartPolicy?.Value;
        OnPropertyChanged(nameof(Difficulties));
        OnPropertyChanged(nameof(RestartPolicies));
        SelectedDifficulty = Difficulties.FirstOrDefault(d => d.Value == difficulty);
        SelectedRestartPolicy = RestartPolicies.FirstOrDefault(p => p.Value == policy);
        var address = SelectedAddress?.Address;
        var relabelled = Addresses.Select(a => a with
        {
            Label = a.LooksLikeVpn ? Localizer.Format("Exposure_AddressVpn", a.Address, a.Interface) : Localizer.Format("Exposure_Address", a.Address, a.Interface),
        }).ToList();
        Addresses.Clear();
        foreach (var choice in relabelled)
        {
            Addresses.Add(choice);
        }

        SelectedAddress = Addresses.FirstOrDefault(a => a.Address == address);
        if (AddressesNote is not null)
        {
            AddressesNote = Localizer[Addresses.Count == 0 ? "Exposure_NoAddresses" : "Exposure_AddressesUnavailable"];
        }

        foreach (var name in new[]
                 {
                     nameof(ResolvedText), nameof(ResolutionError), nameof(ContainerMemoryText), nameof(MemoryWarning),
                     nameof(PortStatus), nameof(WhitelistEmptyNote), nameof(Errors), nameof(Summary), nameof(ResultMessage),
                     nameof(Checklist),
                 })
        {
            OnPropertyChanged(name);
        }
    }

    private bool _disposed;

    /// <summary>Safe to call more than once (the page and the DI container may both dispose it).</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DetachFromLocalizer();
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
