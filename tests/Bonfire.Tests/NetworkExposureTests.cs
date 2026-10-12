using Bonfire.App.Localization;
using Bonfire.App.Services;
using Bonfire.Core;
using Bonfire.Core.Servers;
using Bonfire.Infrastructure.Servers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bonfire.Tests;

/// <summary>The third exposure mode: a specific host address (VPN), from the model to the start guard.</summary>
public sealed class NetworkExposureModelTests
{
    private static readonly ServerPaths Paths = new("/srv/s");
    private static readonly Guid Installation = Guid.NewGuid();

    private static ServerDefinition OnAddress(string address) =>
        ServerTestData.Vanilla() with { Exposure = PortExposure.SpecificAddress, HostAddress = address, RestartPolicy = RestartPolicyKind.No };

    [Theory]
    [InlineData("10.8.0.1")]
    [InlineData("fd00::1")]
    public void SpecificAddressBecomesTheHostIpOfTheBinding(string address)
    {
        var spec = ContainerSpecBuilder.Build(OnAddress(address), Paths, 1000, 1000, Installation);

        Assert.Equal(address, spec.Port.HostIp);
        Assert.Equal(ContainerSpecBuilder.MinecraftPort, spec.Port.ContainerPort);
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.1.1")]
    [InlineData("fe80::1")]
    [InlineData("ff02::1")]
    [InlineData("::ffff:10.8.0.1")] // not canonical: must be stored as 10.8.0.1
    [InlineData("FD00::1")]         // not canonical: lower case
    [InlineData("fe80::1%wlan0")]
    [InlineData("[fd00::1]")]
    [InlineData("010.8.0.1")]
    [InlineData("")]
    [InlineData(null)]
    public void SpecificAddressRequiresACanonicalBindableLiteral(string? address) =>
        Assert.Contains("Validation_HostAddressInvalid", ServerDefinitionValidator.Validate(OnAddress(address!)));

    [Fact]
    public void OtherModesMustNotCarryAnAddress()
    {
        Assert.Contains("Validation_ExposureInvalid", ServerDefinitionValidator.Validate(ServerTestData.Vanilla() with { HostAddress = "10.8.0.1" }));
        Assert.Contains("Validation_ExposureInvalid", ServerDefinitionValidator.Validate(
            ServerTestData.Vanilla() with { Exposure = PortExposure.AllInterfaces, HostAddress = "10.8.0.1" }));
        Assert.Empty(ServerDefinitionValidator.Validate(ServerTestData.Vanilla() with { Exposure = PortExposure.AllInterfaces }));
    }

    [Fact]
    public void UndefinedEnumValuesAreInvalidNotWildcards()
    {
        Assert.Contains("Validation_ExposureInvalid", ServerDefinitionValidator.Validate(ServerTestData.Vanilla() with { Exposure = (PortExposure)7 }));
        Assert.Contains("Validation_RestartPolicyInvalid", ServerDefinitionValidator.Validate(ServerTestData.Vanilla() with { RestartPolicy = (RestartPolicyKind)7 }));
        Assert.Throws<ArgumentException>(() => ContainerSpecBuilder.Build(ServerTestData.Vanilla() with { Exposure = (PortExposure)7 }, Paths, 1000, 1000, Installation));
    }

    [Theory]
    [InlineData(RestartPolicyKind.OnFailure)]
    [InlineData(RestartPolicyKind.UnlessStopped)]
    public void SpecificAddressForbidsAutomaticRestarts(RestartPolicyKind policy) =>
        Assert.Contains("Validation_SpecificAddressRestartPolicy", ServerDefinitionValidator.Validate(OnAddress("10.8.0.1") with { RestartPolicy = policy }));

    [Fact]
    public void ValidSpecificAddressDefinitionsPass()
    {
        Assert.Empty(ServerDefinitionValidator.Validate(OnAddress("10.8.0.1")));
        Assert.Empty(ServerDefinitionValidator.Validate(OnAddress("fd00::1")));
        Assert.Empty(ServerDefinitionValidator.Validate(OnAddress("100.101.102.103")));
    }

    [Fact]
    public void PublishedPortMustMatchTheBindingExactly()
    {
        var spec = ContainerSpecBuilder.Build(OnAddress("fd00::1"), Paths, 1000, 1000, Installation);
        var p = spec.Port;

        Assert.Null(HardeningVerifier.VerifyPublishedPort(spec, [p]));
        Assert.Null(HardeningVerifier.VerifyPublishedPort(spec, [p with { HostIp = "fd00:0:0:0:0:0:0:1" }])); // Docker's spelling
        Assert.NotNull(HardeningVerifier.VerifyPublishedPort(spec, []));                                       // the Docker 29.8 symptom
        Assert.NotNull(HardeningVerifier.VerifyPublishedPort(spec, [p with { HostIp = "0.0.0.0" }]));          // wider than asked
        Assert.NotNull(HardeningVerifier.VerifyPublishedPort(spec, [p with { HostIp = "" }]));
        Assert.NotNull(HardeningVerifier.VerifyPublishedPort(spec, [p, p with { HostIp = "::" }]));             // a second family
        Assert.NotNull(HardeningVerifier.VerifyPublishedPort(spec, [p with { HostPort = p.HostPort + 1 }]));
        Assert.NotNull(HardeningVerifier.VerifyPublishedPort(spec, [p with { Protocol = "udp" }]));
    }

    [Fact]
    public void AllInterfacesToleratesOnlyTheIpv6WildcardTwin()
    {
        var spec = ContainerSpecBuilder.Build(ServerTestData.Vanilla() with { Exposure = PortExposure.AllInterfaces }, Paths, 1000, 1000, Installation);
        var p = spec.Port;

        Assert.Null(HardeningVerifier.VerifyPublishedPort(spec, [p]));                               // Docker 29.8: IPv4 only
        Assert.Null(HardeningVerifier.VerifyPublishedPort(spec, [p, p with { HostIp = "::" }]));      // a daemon that adds [::]
        Assert.NotNull(HardeningVerifier.VerifyPublishedPort(spec, [p with { HostIp = "::" }]));      // IPv6 instead of IPv4
        Assert.NotNull(HardeningVerifier.VerifyPublishedPort(spec, [p, p with { HostIp = "::", HostPort = p.HostPort + 1 }]));
        Assert.NotNull(HardeningVerifier.VerifyPublishedPort(spec, [p, p with { HostIp = "10.0.0.1" }]));
        var local = ContainerSpecBuilder.Build(ServerTestData.Vanilla(), Paths, 1000, 1000, Installation);
        Assert.NotNull(HardeningVerifier.VerifyPublishedPort(local, [local.Port, local.Port with { HostIp = "::" }])); // loopback stays strict
    }

}

public sealed class NetworkExposureRegistryTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly ServerRegistry _registry;

    public NetworkExposureRegistryTests()
    {
        var app = AppPaths.Resolve(k => k == "HOME" ? _temp.Path : null);
        Directory.CreateDirectory(app.DataDir);
        _registry = new ServerRegistry(app);
    }

    public void Dispose() => _temp.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SpecificAddressRoundTripsThroughServerJson()
    {
        var definition = ServerTestData.Vanilla() with { Exposure = PortExposure.SpecificAddress, HostAddress = "fd00::1" };
        await _registry.CreateAsync(ServerRecord.New(definition), Ct);

        var loaded = _registry.Load(definition.Id)!.Record!;
        Assert.Equal(5, loaded.SchemaVersion);
        Assert.Equal(PortExposure.SpecificAddress, loaded.Definition.Exposure);
        Assert.Equal("fd00::1", loaded.Definition.HostAddress);
    }

    [Fact]
    public async Task RecordsWithoutTheFieldStillLoad()
    {
        var definition = ServerTestData.Vanilla();
        var paths = await _registry.CreateAsync(ServerRecord.New(definition), Ct);
        var json = await File.ReadAllTextAsync(paths.RecordFile, Ct);
        Assert.Contains("\"HostAddress\": null", json);
        await File.WriteAllTextAsync(paths.RecordFile, json.Replace("\"HostAddress\": null,", "").Replace("\"SchemaVersion\": 5", "\"SchemaVersion\": 4"), Ct);

        var loaded = _registry.Load(definition.Id)!;
        Assert.NotNull(loaded.Record);
        Assert.Null(loaded.Record!.Definition.HostAddress);
    }

    [Fact]
    public async Task ANewerSchemaIsReportedBeforeAnythingIsParsed()
    {
        var definition = ServerTestData.Vanilla();
        var paths = await _registry.CreateAsync(ServerRecord.New(definition), Ct);
        var json = await File.ReadAllTextAsync(paths.RecordFile, Ct);
        // A future mode this version cannot parse: the schema number must speak first.
        await File.WriteAllTextAsync(paths.RecordFile, json.Replace("\"SchemaVersion\": 5", "\"SchemaVersion\": 9").Replace("\"Localhost\"", "\"Teleport\""), Ct);

        var loaded = _registry.Load(definition.Id)!;
        Assert.Null(loaded.Record);
        Assert.Contains("schema 9", loaded.Error);
    }

    [Fact]
    public async Task AnInvalidPendingSettingsChangeMarksTheRecordDamaged()
    {
        var definition = ServerTestData.Vanilla();
        await _registry.CreateAsync(ServerRecord.New(definition), Ct);
        var record = _registry.Load(definition.Id)!.Record!;
        var broken = definition with { Exposure = PortExposure.SpecificAddress, HostAddress = "0.0.0.0" };
        await _registry.SaveAsync(record with { PendingChange = new PendingSettingsChange(definition, broken, false, SettingsChangeStep.Prepared) }, Ct);

        var loaded = _registry.Load(definition.Id)!;
        Assert.Null(loaded.Record);
        Assert.Contains("pending settings change", loaded.Error);
    }
}

[Collection(LocalizerCollection.Name)]
public sealed class NetworkExposureOperationsTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly ServerRegistry _registry;
    private readonly FakeServerRuntime _runtime = new();
    private readonly FakeHostAddresses _hosts = FakeHostAddresses.Typical();
    private readonly ServerCreationTests.FixedPorts _ports = new();
    private readonly ServerOperationsService _operations;

    public NetworkExposureOperationsTests()
    {
        Localizer.Instance.SetLanguage(AppLanguage.English);
        var app = AppPaths.Resolve(k => k == "HOME" ? _temp.Path : null);
        Directory.CreateDirectory(app.DataDir);
        _registry = new ServerRegistry(app);
        _operations = new ServerOperationsService(_registry, _runtime, new ServerCreationTests.SafeDocker(), NullLogger<ServerOperationsService>.Instance,
            hostAddresses: _hosts, ports: _ports);
    }

    public void Dispose() => _temp.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<Guid> CreateAsync(ServerDefinition definition, string state = "exited")
    {
        var record = ServerRecord.New(definition) with { Phase = ProvisioningPhase.Ready, ContainerId = "c1", NetworkId = "n1" };
        await _registry.CreateAsync(record, Ct);
        _runtime.Containers.Add(FakeServerRuntime.State(definition.Id, state));
        return definition.Id;
    }

    private static ServerDefinition OnVpn() =>
        ServerTestData.Vanilla() with { Exposure = PortExposure.SpecificAddress, HostAddress = "10.8.0.1", RestartPolicy = RestartPolicyKind.No };

    [Fact]
    public async Task StartIsRefusedWhileTheAddressIsAbsentAndNothingIsStarted()
    {
        var id = await CreateAsync(OnVpn());
        _hosts.Interfaces.RemoveAll(i => i.Name == "wg0"); // VPN down

        var ex = await Assert.ThrowsAsync<ServerOperationException>(() => _operations.StartAsync(id, Ct));

        Assert.Equal(ServerOperationError.AddressNotPresent, ex.Error);
        Assert.Equal("10.8.0.1", ex.Detail);
        Assert.DoesNotContain("start", _runtime.Calls);
    }

    [Fact]
    public async Task StartProceedsWhenTheAddressIsPresentAndEnumeratesAfresh()
    {
        var id = await CreateAsync(OnVpn());

        await _operations.StartAsync(id, Ct);
        await _operations.StartAsync(id, Ct);

        Assert.Equal(["start", "start"], _runtime.Calls);
        Assert.Equal(2, _hosts.Reads); // once per start, never cached
    }

    [Fact]
    public async Task AnEnumerationFailureRefusesTheStartWithItsOwnError()
    {
        var id = await CreateAsync(OnVpn());
        _hosts.Fail = true;

        var ex = await Assert.ThrowsAsync<ServerOperationException>(() => _operations.StartAsync(id, Ct));

        Assert.Equal(ServerOperationError.AddressCheckFailed, ex.Error);
        Assert.Empty(_runtime.Calls);
    }

    [Fact]
    public async Task OtherModesDoNotConsultTheInterfaces()
    {
        var id = await CreateAsync(ServerTestData.Vanilla() with { Exposure = PortExposure.AllInterfaces });
        _hosts.Fail = true;

        await _operations.StartAsync(id, Ct);

        Assert.Equal(["start"], _runtime.Calls);
        Assert.Equal(0, _hosts.Reads);
    }

    [Fact]
    public async Task WithoutAnEnumeratorASpecificAddressServerFailsClosed()
    {
        var operations = new ServerOperationsService(_registry, _runtime, new ServerCreationTests.SafeDocker(), NullLogger<ServerOperationsService>.Instance);
        var id = await CreateAsync(OnVpn());

        var ex = await Assert.ThrowsAsync<ServerOperationException>(() => operations.StartAsync(id, Ct));

        Assert.Equal(ServerOperationError.AddressCheckFailed, ex.Error);
        Assert.Empty(_runtime.Calls);
    }

    [Fact]
    public async Task RestartStopsThenRefusesTheStartWhenTheAddressVanished()
    {
        var id = await CreateAsync(OnVpn(), "running");
        _runtime.Stop = (record, _) =>
        {
            _runtime.Containers.Clear();
            _runtime.Containers.Add(FakeServerRuntime.State(record.Definition.Id, "exited"));
            _hosts.Interfaces.RemoveAll(i => i.Name == "wg0"); // the VPN drops while the server stops
            return Task.CompletedTask;
        };

        var ex = await Assert.ThrowsAsync<ServerOperationException>(() => _operations.RestartAsync(id, Ct));

        Assert.Equal(ServerOperationError.AddressNotPresent, ex.Error);
        Assert.Equal(["stop"], _runtime.Calls);
    }

    [Fact]
    public async Task ForceStopDuringTheAddressCheckStillAbortsTheRestart()
    {
        var id = await CreateAsync(OnVpn(), "running");
        _runtime.Stop = (record, _) =>
        {
            _runtime.Containers.Clear();
            _runtime.Containers.Add(FakeServerRuntime.State(record.Definition.Id, "exited"));
            return Task.CompletedTask;
        };
        // The force stop lands while the interfaces are being enumerated — after the stop, after
        // the daemon check, before the start decision. The flag must be read after the enumeration.
        _hosts.OnRead = () => _operations.ForceStopAsync(id, Ct).GetAwaiter().GetResult();

        var ex = await Assert.ThrowsAsync<ServerOperationException>(() => _operations.RestartAsync(id, Ct));

        Assert.Equal(ServerOperationError.RestartAborted, ex.Error);
        Assert.Equal(1, _hosts.Reads);
        Assert.Equal(["stop", "kill"], _runtime.Calls);
    }

    [Fact]
    public async Task SettingsPatchMovesTheBindingAndChecksItFirst()
    {
        var id = await CreateAsync(ServerTestData.Vanilla());
        var current = _registry.Load(id)!.Record!.Definition;
        var patch = new ServerSettingsPatch(current.Game, 25600, PortExposure.SpecificAddress, "10.8.0.1", RestartPolicyKind.No);

        var result = await _operations.ApplySettingsAsync(id, patch, Ct);

        Assert.Equal(SettingsApplyOutcome.SavedForNextStart, result.Outcome);
        var (hostIp, port, scope) = Assert.Single(_ports.Checks);
        Assert.Equal(("10.8.0.1", 25600), (hostIp, port));
        Assert.Equal(id, scope.IgnoreServerId);
        Assert.Equal("c1", scope.IgnoreContainerId);
        Assert.True(scope.ProbeHost); // a different port: the old container's listener cannot be the conflict
        var saved = _registry.Load(id)!.Record!.Definition;
        Assert.Equal((PortExposure.SpecificAddress, "10.8.0.1", 25600), (saved.Exposure, saved.HostAddress, saved.Port));
        Assert.Equal("10.8.0.1", Assert.Single(_runtime.Provisioned).HostAddress);
    }

    [Fact]
    public async Task AnOverlappingReplacementSkipsTheHostProbe()
    {
        var id = await CreateAsync(ServerTestData.Vanilla());
        var current = _registry.Load(id)!.Record!.Definition;
        var patch = new ServerSettingsPatch(current.Game, current.Port, PortExposure.AllInterfaces, null, current.RestartPolicy);

        await _operations.ApplySettingsAsync(id, patch, Ct);

        Assert.False(Assert.Single(_ports.Checks).Scope.ProbeHost);
    }

    [Fact]
    public async Task AConflictingBindingIsRefusedBeforeAnythingChanges()
    {
        var id = await CreateAsync(ServerTestData.Vanilla());
        var current = _registry.Load(id)!.Record!.Definition;
        _ports.Result = PortCheck.UsedByHost;

        var ex = await Assert.ThrowsAsync<ServerOperationException>(() =>
            _operations.ApplySettingsAsync(id, new ServerSettingsPatch(current.Game, current.Port + 1, current.Exposure, null, current.RestartPolicy), Ct));

        Assert.Equal(ServerOperationError.PortInUse, ex.Error);
        Assert.Empty(_runtime.Calls);
        Assert.Null(_registry.Load(id)!.Record!.PendingChange);
    }

    [Fact]
    public async Task GameOnlyChangesDoNotCheckThePort()
    {
        var id = await CreateAsync(ServerTestData.Vanilla());
        var current = _registry.Load(id)!.Record!.Definition;

        await _operations.ApplySettingsAsync(id, current.Game with { MaxPlayers = 3 }, Ct);
        var unchanged = await _operations.ApplySettingsAsync(id, ServerSettingsPatch.From(_registry.Load(id)!.Record!.Definition, current.Game with { MaxPlayers = 3 }), Ct);

        Assert.Empty(_ports.Checks);
        Assert.Equal(SettingsApplyOutcome.NoChange, unchanged.Outcome);
    }

    [Fact]
    public async Task AnInvalidPatchIsRejectedWithValidationKeys()
    {
        var id = await CreateAsync(ServerTestData.Vanilla());
        var current = _registry.Load(id)!.Record!.Definition;

        var ex = await Assert.ThrowsAsync<SettingsValidationException>(() =>
            _operations.ApplySettingsAsync(id, new ServerSettingsPatch(current.Game, current.Port, PortExposure.SpecificAddress, "10.8.0.1", RestartPolicyKind.UnlessStopped), Ct));

        Assert.Contains("Validation_SpecificAddressRestartPolicy", ex.Errors);
        Assert.Empty(_runtime.Calls);
    }

    [Fact]
    public async Task SettingsRestartIsRefusedWhenTheAddressIsAbsent()
    {
        var id = await CreateAsync(OnVpn(), "running");
        _runtime.ProvisionedContainerId = "c2";
        var current = _registry.Load(id)!.Record!.Definition;
        _runtime.Stop = (_, _) =>
        {
            _hosts.Interfaces.RemoveAll(i => i.Name == "wg0");
            return Task.CompletedTask;
        };

        var result = await _operations.ApplySettingsAsync(id, current.Game with { MaxPlayers = 5 }, Ct);

        Assert.Equal(SettingsApplyOutcome.AppliedStartFailed, result.Outcome);
        Assert.Equal(ServerOperationError.AddressNotPresent, result.StartError!.Error);
        Assert.DoesNotContain("start", _runtime.Calls);
        Assert.Equal(5, _registry.Load(id)!.Record!.Definition.Game.MaxPlayers); // applied, just not started
    }
}
