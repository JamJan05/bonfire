using Bonfire.App.Localization;
using Bonfire.App.Services;
using Bonfire.App.ViewModels;
using Bonfire.Core;
using Bonfire.Core.Backups;
using Bonfire.Core.Metadata;
using Bonfire.Core.Servers;
using Bonfire.Infrastructure.Linux;
using Bonfire.Infrastructure.Servers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bonfire.Tests;

[Collection(LocalizerCollection.Name)]
public sealed class NetworkExposureViewModelTests : IDisposable
{
    private sealed class Metadata : IServerMetadata
    {
        public Task<IReadOnlyList<GameVersion>> GetReleasesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<GameVersion>>([new GameVersion("26.3", DateTimeOffset.UtcNow)]);

        public Task<ResolvedServerVersion> ResolveAsync(ServerType type, string minecraftVersion, CancellationToken cancellationToken) =>
            Task.FromResult(new ResolvedServerVersion(type, minecraftVersion, 25, null, null, null, true));

        public void Invalidate()
        {
        }
    }

    private sealed class NoLauncher : IFolderLauncher
    {
        public Task<bool> OpenAsync(string path) => Task.FromResult(true);

        public Task<bool> OpenUriAsync(Uri uri) => Task.FromResult(true);
    }

    private readonly TempDirectory _temp = new();
    private readonly ServerRegistry _registry;
    private readonly FakeServerRuntime _runtime = new();
    private readonly FakeHostAddresses _hosts = FakeHostAddresses.Typical();
    private readonly ServerCreationTests.FixedPorts _ports = new();
    private readonly ServerOperationsService _operations;
    private readonly ServerCreationService _creation;

    public NetworkExposureViewModelTests()
    {
        Localizer.Instance.SetLanguage(AppLanguage.English);
        var app = AppPaths.Resolve(k => k == "HOME" ? _temp.Path : null);
        Directory.CreateDirectory(app.DataDir);
        _registry = new ServerRegistry(app);
        _operations = new ServerOperationsService(_registry, _runtime, new ServerCreationTests.SafeDocker(), NullLogger<ServerOperationsService>.Instance,
            hostAddresses: _hosts, ports: _ports);
        _creation = new ServerCreationService(_registry, _runtime, _operations, _ports, TimeProvider.System, NullLogger<ServerCreationService>.Instance);
    }

    public void Dispose() => _temp.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private NewServerViewModel Wizard() => new(
        Localizer.Instance, new Metadata(), _creation, _ports, _hosts, new NoLauncher(), new Navigator(), TimeProvider.System,
        NullLogger<NewServerViewModel>.Instance, () => new MemoryInfo(16L << 30, 8L << 30));

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    // ---- Wizard -------------------------------------------------------------------------

    [Fact]
    public async Task WizardListsVpnAddressesFirstAndNeverTheDockerBridge()
    {
        using var wizard = Wizard();
        await wizard.InitializeAsync();

        Assert.Equal(["10.8.0.1", "192.168.1.20", "2001:db8::20"], wizard.Addresses.Select(a => a.Address));
        Assert.True(wizard.Addresses[0].LooksLikeVpn);
        Assert.Contains("wg0", wizard.Addresses[0].Label);
        Assert.Equal("10.8.0.1", wizard.SelectedAddress?.Address);
        Assert.Null(wizard.AddressesNote);
    }

    [Fact]
    public async Task ChoosingAnAddressLocksTheRestartPolicyAndShowsTheEndpoint()
    {
        using var wizard = Wizard();
        await wizard.InitializeAsync();
        wizard.DisplayName = "Friends";
        wizard.EulaAccepted = true;
        await Until(() => wizard.Resolved is not null);
        wizard.SelectedRestartPolicy = wizard.RestartPolicies.First(p => p.Value == RestartPolicyKind.UnlessStopped);

        wizard.IsSpecificAddress = true;

        Assert.Equal(PortExposure.SpecificAddress, wizard.Exposure);
        Assert.True(wizard.RestartPolicyLocked);
        Assert.Equal(RestartPolicyKind.No, wizard.SelectedRestartPolicy?.Value);
        Assert.Contains(wizard.Summary, l => l.EndsWith("10.8.0.1:25565", StringComparison.Ordinal));
        Assert.Empty(wizard.Errors);
        Assert.True(wizard.CanCreate);

        wizard.SelectedAddress = wizard.Addresses.First(a => a.Address == "2001:db8::20");
        Assert.Contains(wizard.Summary, l => l.EndsWith("[2001:db8::20]:25565", StringComparison.Ordinal));

        wizard.IsLocalhost = true;
        Assert.False(wizard.RestartPolicyLocked);
        Assert.Contains(wizard.Summary, l => l.EndsWith("127.0.0.1:25565", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WithoutAnAddressTheWizardReportsItInsteadOfGuessing()
    {
        _hosts.Interfaces.Clear();
        _hosts.Interfaces.Add(FakeHostAddresses.Interface("lo", "127.0.0.1"));
        using var wizard = Wizard();
        await wizard.InitializeAsync();
        wizard.DisplayName = "x";
        wizard.EulaAccepted = true;
        await Until(() => wizard.Resolved is not null);

        wizard.IsSpecificAddress = true;

        Assert.Empty(wizard.Addresses);
        Assert.NotNull(wizard.AddressesNote);
        Assert.Contains(Localizer.Instance["Validation_HostAddressInvalid"], wizard.Errors);
        Assert.False(wizard.CanCreate);
    }

    [Fact]
    public async Task CreatedServerCarriesTheAddress()
    {
        using var wizard = Wizard();
        await wizard.InitializeAsync();
        wizard.DisplayName = "Friends";
        wizard.EulaAccepted = true;
        wizard.StartAfterCreate = false;
        await Until(() => wizard.Resolved is not null);
        wizard.IsSpecificAddress = true;

        await wizard.CreateCommand.ExecuteAsync(null);

        var definition = Assert.Single(_runtime.Provisioned);
        Assert.Equal((PortExposure.SpecificAddress, "10.8.0.1", RestartPolicyKind.No), (definition.Exposure, definition.HostAddress, definition.RestartPolicy));
        Assert.Equal("10.8.0.1", _ports.Checks.Last().HostIp);
    }

    [Fact]
    public async Task ChecklistFollowsTheFormAndNeverBlocksCreating()
    {
        using var wizard = Wizard();
        await wizard.InitializeAsync();
        wizard.DisplayName = "Public";
        wizard.EulaAccepted = true;
        await Until(() => wizard.Resolved is not null);
        Assert.False(wizard.ShowChecklist);

        wizard.IsAllInterfaces = true;
        wizard.OnlineMode = false;

        Assert.True(wizard.ShowChecklist);
        Assert.Equal([false, false, false], wizard.Checklist.Select(i => i.Ok)); // online mode off, no whitelist, no backup yet
        Assert.True(wizard.CanCreate);

        wizard.OnlineMode = true;
        wizard.WhitelistEnabled = true;
        wizard.WhitelistText = "Steve";
        Assert.Equal([true, true, false], wizard.Checklist.Select(i => i.Ok));
    }

    [Fact]
    public async Task RefreshKeepsASelectedAddressThatDisappeared()
    {
        using var wizard = Wizard();
        await wizard.InitializeAsync();
        wizard.DisplayName = "Friends";
        wizard.EulaAccepted = true;
        await Until(() => wizard.Resolved is not null);
        wizard.IsSpecificAddress = true;
        Assert.Equal("10.8.0.1", wizard.SelectedAddress?.Address);
        Assert.True(wizard.CanCreate);

        _hosts.Interfaces.RemoveAll(i => i.Name == "wg0");
        await wizard.RefreshAddressesCommand.ExecuteAsync(null);

        Assert.Equal("10.8.0.1", wizard.SelectedAddress?.Address); // still shown — not swapped for the LAN address
        Assert.Contains("not on any interface", wizard.Addresses[0].Label); // kept as an item, so the ComboBox keeps it too
        Assert.True(wizard.CanCreate); // the address is a valid literal; presence is checked at start, where it is refused
    }

    // ---- Settings -----------------------------------------------------------------------

    private async Task<Guid> CreateServerAsync(ServerDefinition definition)
    {
        var record = ServerRecord.New(definition) with { Phase = ProvisioningPhase.Ready, ContainerId = "c1", NetworkId = "n1" };
        await _registry.CreateAsync(record, Ct);
        _runtime.Containers.Add(FakeServerRuntime.State(definition.Id, "exited"));
        return definition.Id;
    }

    private ServerSettingsViewModel Settings(Guid id, Func<IReadOnlyList<BackupInfo>>? backups = null) =>
        TestDetails.Settings(id, _registry, _operations, new TestDetails.Confirm(true), _hosts, _ports, backups);

    [Fact]
    public async Task SettingsLoadTheBindingAndSaveItAsAPatch()
    {
        var id = await CreateServerAsync(ServerTestData.Vanilla());
        var editor = Settings(id);
        await editor.LoadAsync();
        Assert.True(editor.IsLocalhost);
        Assert.Equal(25599, editor.Port);

        editor.IsSpecificAddress = true;
        editor.Port = 25600;
        var patch = editor.BuildPatch()!;

        Assert.Equal((PortExposure.SpecificAddress, "10.8.0.1", 25600, RestartPolicyKind.No), (patch.Exposure, patch.HostAddress, patch.Port, patch.RestartPolicy));
        await editor.SaveCommand.ExecuteAsync(null);
        var saved = _registry.Load(id)!.Record!.Definition;
        Assert.Equal("10.8.0.1", saved.HostAddress);
        Assert.Equal("10.8.0.1", Assert.Single(_runtime.Provisioned).HostAddress);
    }

    [Fact]
    public async Task GameOnlySaveKeepsTheBindingUntouched()
    {
        var id = await CreateServerAsync(ServerTestData.Vanilla() with { Exposure = PortExposure.SpecificAddress, HostAddress = "fd00::1" });
        _hosts.Interfaces.Add(FakeHostAddresses.Interface("wg1", "fd00::1"));
        var editor = Settings(id);
        await editor.LoadAsync();

        editor.MaxPlayers = 4;
        var patch = editor.BuildPatch()!;

        Assert.Equal(("fd00::1", PortExposure.SpecificAddress, 25599), (patch.HostAddress, patch.Exposure, patch.Port));
        Assert.Equal(4, patch.Game.MaxPlayers);
    }

    [Fact]
    public async Task AnAbsentSavedAddressStaysSelectedAndIsMarked()
    {
        var id = await CreateServerAsync(ServerTestData.Vanilla() with { Exposure = PortExposure.SpecificAddress, HostAddress = "10.9.9.9" });
        var editor = Settings(id);
        await editor.LoadAsync();

        Assert.Equal("10.9.9.9", editor.SelectedAddress?.Address);
        Assert.Contains("not on any interface", editor.SelectedAddress!.Label);
        Assert.Equal("10.9.9.9", editor.BuildPatch()!.HostAddress); // saving unrelated fields does not move the server
    }

    [Fact]
    public async Task SettingsChecklistReadsTheFormAndTheNewestBackup()
    {
        var id = await CreateServerAsync(ServerTestData.Vanilla() with { Exposure = PortExposure.AllInterfaces });
        var newest = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        BackupInfo Backup(DateTimeOffset at, bool usable) => new(Guid.NewGuid(), BackupKind.Manual, at, 1, 1, 1, "/b", usable ? new BackupRecord(null!, "x", 1) : null, usable ? null : "broken");
        var editor = Settings(id, () => [Backup(newest.AddDays(1), false), Backup(newest, true)]);
        await editor.LoadAsync();

        Assert.True(editor.ShowChecklist);
        Assert.Equal(4, editor.Checklist.Count);
        Assert.Equal([true, false, false, true], editor.Checklist.Select(i => i.Ok)); // online, whitelist off, not enforced, backup exists
        Assert.Contains(newest.ToLocalTime().Year.ToString(System.Globalization.CultureInfo.CurrentCulture), editor.Checklist[3].Text);

        editor.OnlineMode = false; // unsaved values count, not the saved record
        editor.WhitelistEnabled = true;
        editor.EnforceWhitelist = true;
        Assert.Equal([false, true, true, true], editor.Checklist.Select(i => i.Ok));
        Assert.True(editor.SaveCommand.CanExecute(null));
    }

    [Fact]
    public async Task SettingsPortCheckIgnoresTheServerItself()
    {
        var id = await CreateServerAsync(ServerTestData.Vanilla());
        var editor = Settings(id);
        await editor.LoadAsync();
        Assert.Empty(_ports.Checks); // unchanged binding: nothing to check

        editor.IsAllInterfaces = true;
        await Until(() => _ports.Checks.Count > 0);

        var (hostIp, port, scope) = _ports.Checks.Last();
        Assert.Equal(("0.0.0.0", 25599), (hostIp, port));
        Assert.Equal((id, "c1", false), (scope.IgnoreServerId, scope.IgnoreContainerId, scope.ProbeHost));
    }

    // ---- Connection panel ---------------------------------------------------------------

    [Fact]
    public void ConnectionPanelForAllInterfacesListsIpv4AsPublishedAndIpv6AsNot()
    {
        var definition = ServerTestData.Vanilla() with { Exposure = PortExposure.AllInterfaces, Port = 25565 };

        var (rows, notes) = ConnectionInfoViewModel.Build(definition, _hosts.Interfaces, Localizer.Instance);

        Assert.Equal(["127.0.0.1:25565", "10.8.0.1:25565", "192.168.1.20:25565", "[2001:db8::20]:25565"], rows.Select(r => r.Endpoint));
        Assert.Equal([true, true, true, false], rows.Select(r => r.IsPublished));
        Assert.Contains("VPN", rows[1].Note);
        Assert.Contains("IPv4-only", rows[3].Note);
        Assert.Equal(4, notes.Count);
        Assert.Contains(notes, n => n.Contains("firewall", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(notes, n => n.Contains("carrier-grade NAT", StringComparison.Ordinal));
    }

    [Fact]
    public void ConnectionPanelForASpecificAddressSaysWhereItLives()
    {
        var definition = ServerTestData.Vanilla() with { Exposure = PortExposure.SpecificAddress, HostAddress = "10.8.0.1", Port = 25565 };

        var (rows, notes) = ConnectionInfoViewModel.Build(definition, _hosts.Interfaces, Localizer.Instance);
        var row = Assert.Single(rows);
        Assert.Equal("10.8.0.1:25565", row.Endpoint);
        Assert.True(row.IsPublished);
        Assert.Contains("wg0", row.Note);
        Assert.Single(notes);

        _hosts.Interfaces.RemoveAll(i => i.Name == "wg0");
        var (absentRows, absentNotes) = ConnectionInfoViewModel.Build(definition, _hosts.Interfaces, Localizer.Instance);
        Assert.False(Assert.Single(absentRows).IsPublished);
        Assert.Equal(2, absentNotes.Count);
    }

    [Fact]
    public void ConnectionPanelForLocalhostHasOneRow()
    {
        var (rows, notes) = ConnectionInfoViewModel.Build(ServerTestData.Vanilla() with { Port = 25565 }, _hosts.Interfaces, Localizer.Instance);
        Assert.Equal("127.0.0.1:25565", Assert.Single(rows).Endpoint);
        Assert.Single(notes);
    }

    [Fact]
    public async Task CopyPutsTheEndpointOnTheClipboardAndReportsFailure()
    {
        var clipboard = new TestDetails.FakeClipboard();
        var panel = TestDetails.Connection(_hosts, clipboard);
        await panel.LoadAsync(ServerTestData.Vanilla() with { Exposure = PortExposure.SpecificAddress, HostAddress = "2001:db8::20", Port = 25565 });
        Assert.True(panel.HasRows);

        await panel.CopyCommand.ExecuteAsync(panel.Rows[0]);
        Assert.Equal(["[2001:db8::20]:25565"], clipboard.Texts);
        Assert.Contains("Copied", panel.CopiedText);

        clipboard.Fail = true;
        await panel.CopyCommand.ExecuteAsync(panel.Rows[0]);
        Assert.Contains("Could not copy", panel.CopiedText);
    }

    [Fact]
    public async Task UnreadableInterfacesStillShowTheServersOwnEndpoint()
    {
        _hosts.Fail = true;
        var panel = TestDetails.Connection(_hosts);
        await panel.LoadAsync(ServerTestData.Vanilla() with { Exposure = PortExposure.SpecificAddress, HostAddress = "10.8.0.1", Port = 25565 });

        Assert.NotNull(panel.LoadError);
        Assert.Equal("10.8.0.1:25565", Assert.Single(panel.Rows).Endpoint);
    }
}
