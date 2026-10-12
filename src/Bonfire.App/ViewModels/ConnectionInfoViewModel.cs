using System.Collections.ObjectModel;
using System.Net.Sockets;
using Bonfire.App.Localization;
using Bonfire.App.Services;
using Bonfire.Core.Servers;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Bonfire.App.ViewModels;

/// <summary>One address players could use, or — when <see cref="IsPublished"/> is false — one they cannot.</summary>
public sealed record ConnectionRow(string Endpoint, string Note, bool IsPublished);

/// <summary>
/// "Where do players connect?" for the Information tab: the server's endpoints on the host's
/// current addresses, with copy buttons, and the facts about reachability that the exposure
/// mode implies. Addresses are read afresh on every load.
/// </summary>
public sealed partial class ConnectionInfoViewModel : ObservableObject
{
    private readonly Localizer _localizer;
    private readonly IHostAddresses _hostAddresses;
    private readonly IClipboardService _clipboard;
    private readonly ILogger _logger;
    private ServerDefinition? _definition;
    private string? _copiedKey;
    private string? _copiedArgument;

    public ConnectionInfoViewModel(Localizer localizer, IHostAddresses hostAddresses, IClipboardService clipboard, ILogger logger)
    {
        _localizer = localizer;
        _hostAddresses = hostAddresses;
        _clipboard = clipboard;
        _logger = logger;
    }

    public ObservableCollection<ConnectionRow> Rows { get; } = [];

    public ObservableCollection<string> Notes { get; } = [];

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _loadError;

    public string? CopiedText => _copiedKey is null ? null : _localizer.Format(_copiedKey, _copiedArgument);

    public bool HasRows => Rows.Count > 0;

    /// <summary>Rebuilds the panel for the definition; a null definition (damaged record) clears it.</summary>
    public async Task LoadAsync(ServerDefinition? definition)
    {
        // Loads overlap (tab switch, refresh, language change): only the newest one may publish its result.
        var generation = ++_loadGeneration;
        _definition = definition;
        _copiedKey = null;
        OnPropertyChanged(nameof(CopiedText));
        if (definition is null)
        {
            Rows.Clear();
            Notes.Clear();
            OnPropertyChanged(nameof(HasRows));
            IsLoading = false;
            return;
        }

        IsLoading = true;
        try
        {
            IReadOnlyList<HostInterface> interfaces;
            string? loadError = null;
            try
            {
                interfaces = await Task.Run(_hostAddresses.Read);
            }
            catch (HostAddressException ex)
            {
                _logger.LogWarning(ex, "Could not read the host's addresses");
                interfaces = [];
                loadError = _localizer["Exposure_AddressesUnavailable"];
            }

            if (generation != _loadGeneration)
            {
                return;
            }

            LoadError = loadError;
            var (rows, notes) = Build(definition, interfaces, _localizer);
            Rows.Clear();
            foreach (var row in rows)
            {
                Rows.Add(row);
            }

            Notes.Clear();
            foreach (var note in notes)
            {
                Notes.Add(note);
            }

            OnPropertyChanged(nameof(HasRows));
        }
        finally
        {
            if (generation == _loadGeneration)
            {
                IsLoading = false;
            }
        }
    }

    private int _loadGeneration;

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync(_definition);

    [RelayCommand]
    private async Task CopyAsync(ConnectionRow? row)
    {
        if (row is null)
        {
            return;
        }

        var ok = await _clipboard.SetTextAsync(row.Endpoint);
        _copiedKey = ok ? "Connection_Copied" : "Connection_CopyFailed";
        _copiedArgument = row.Endpoint;
        OnPropertyChanged(nameof(CopiedText));
    }

    /// <summary>Pure: which endpoints exist for the binding and what players must know about reaching them.</summary>
    public static (IReadOnlyList<ConnectionRow> Rows, IReadOnlyList<string> Notes) Build(
        ServerDefinition definition, IReadOnlyList<HostInterface> interfaces, Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(interfaces);
        ArgumentNullException.ThrowIfNull(localizer);
        var rows = new List<ConnectionRow>();
        var notes = new List<string>();
        var port = definition.Port;

        switch (definition.Exposure)
        {
            case PortExposure.Localhost:
                rows.Add(new ConnectionRow(HostAddressRules.Endpoint("127.0.0.1", port), localizer["Connection_ThisComputer"], true));
                notes.Add(localizer["Connection_LocalhostNote"]);
                break;

            case PortExposure.AllInterfaces:
                rows.Add(new ConnectionRow(HostAddressRules.Endpoint("127.0.0.1", port), localizer["Connection_ThisComputer"], true));
                foreach (var s in HostAddressRules.Suggestions(interfaces))
                {
                    var published = s.Family == AddressFamily.InterNetwork; // an explicit 0.0.0.0 binding is IPv4 only (verified on Docker 29.8)
                    var note = !published ? localizer.Format("Connection_NotPublishedIpv6", s.Interface)
                        : s.LooksLikeVpn ? localizer.Format("Connection_InterfaceVpn", s.Interface)
                        : localizer.Format("Connection_Interface", s.Interface);
                    rows.Add(new ConnectionRow(HostAddressRules.Endpoint(s.Address, port), note, published));
                }

                notes.Add(localizer["Connection_AllNote_Lan"]);
                notes.Add(localizer["Connection_AllNote_Internet"]);
                notes.Add(localizer["Connection_AllNote_Firewall"]);
                notes.Add(localizer["Connection_AllNote_Ipv6"]);
                break;

            case PortExposure.SpecificAddress:
                var address = definition.HostAddress ?? "";
                var carrier = HostAddressRules.Find(interfaces, address);
                var vpn = carrier is not null && HostAddressRules.LooksLikeVpn(carrier.Name, address);
                var carrierNote = carrier is null
                    ? localizer["Connection_AddressAbsent"]
                    : vpn
                        ? localizer.Format("Connection_InterfaceVpn", carrier.Name)
                        : localizer.Format("Connection_Interface", carrier.Name);
                rows.Add(new ConnectionRow(HostAddressRules.Endpoint(address, port), carrierNote, carrier is not null));
                // A specific address is a destination restriction, not VPN isolation: the VPN wording
                // applies only when the address really lives on a VPN-looking interface.
                notes.Add(localizer[vpn ? "Connection_SpecificNoteVpn" : "Connection_SpecificNote"]);
                if (carrier is null)
                {
                    notes.Add(localizer["Connection_SpecificAbsentNote"]);
                }

                break;
        }

        return (rows, notes);
    }

    public void OnLanguageChanged()
    {
        OnPropertyChanged(nameof(CopiedText));
        _ = LoadAsync(_definition);
    }
}
