using Bonfire.App.Localization;
using Bonfire.App.Services;
using Bonfire.App.ViewModels;
using Bonfire.Core;
using Bonfire.Core.Servers;
using Bonfire.Infrastructure.Servers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bonfire.Tests;

public sealed class JavaPropertiesTests
{
    [Fact]
    public void ParsesTheServerPropertiesFormat()
    {
        var props = JavaProperties.Parse(
            "# comment\n! also comment\nmotd=Hello \\u00A7aWorld\nmax-players = 20\nlevel-name:world\npvp true\n" +
            "long=first \\\n    second\nkey\\=with\\:sep=v\nempty=\ndup=1\ndup=2\nbad=\\uZZZZ\n#trailing comment \\\nnext=ok\r\n");

        Assert.Equal("Hello §aWorld", props["motd"]);
        Assert.Equal("20", props["max-players"]);
        Assert.Equal("world", props["level-name"]);
        Assert.Equal("true", props["pvp"]);
        Assert.Equal("first second", props["long"]);
        Assert.Equal("v", props["key=with:sep"]);
        Assert.Equal("", props["empty"]);
        Assert.Equal("2", props["dup"]);
        Assert.Equal("uZZZZ", props["bad"]);
        Assert.Equal("ok", props["next"]); // a comment's trailing backslash does not continue
        Assert.False(props.ContainsKey("# comment"));
    }
}

public sealed class SettingsModelTests
{
    private static readonly ServerPaths Paths = new("/srv/s");

    [Theory]
    [InlineData("%env:RCON_PASSWORD%")]
    [InlineData("%VERSION%")]
    [InlineData("cost $5")]
    [InlineData("back\\slash")]
    [InlineData("two\nlines")]
    public void UnsafeMotdIsRejected(string motd) =>
        Assert.Contains("Validation_MotdInvalid", ServerDefinitionValidator.Validate(
            ServerTestData.Vanilla() with { Game = ServerTestData.Vanilla().Game with { Motd = motd } }));

    [Fact]
    public void FormattingCodesInMotdAreAllowed() =>
        Assert.Empty(ServerDefinitionValidator.Validate(
            ServerTestData.Vanilla() with { Game = ServerTestData.Vanilla().Game with { Motd = "§6Bonfire §7— witaj!" } }));

    [Theory]
    [InlineData(1, null)]
    [InlineData(33, null)]
    [InlineData(null, -1)]
    [InlineData(null, 1001)]
    public void RangesAreChecked(int? view, int? spawn) =>
        Assert.NotEmpty(ServerDefinitionValidator.Validate(
            ServerTestData.Vanilla() with { Game = ServerTestData.Vanilla().Game with { ViewDistance = view, SpawnProtection = spawn } }));

    [Fact]
    public void UnmanagedFieldsSetNoVariables()
    {
        var env = ContainerSpecBuilder.Build(ServerTestData.Vanilla(), Paths, 1000, 1000, Guid.NewGuid()).Environment;

        foreach (var name in new[] { "MOTD", "MODE", "PVP", "VIEW_DISTANCE", "SPAWN_PROTECTION", "ALLOW_FLIGHT", "ENABLE_COMMAND_BLOCK", "ENFORCE_WHITELIST" })
        {
            Assert.False(env.ContainsKey(name), name);
        }

        Assert.Equal("SKIP", env["EXISTING_WHITELIST_FILE"]); // WHITELIST only seeds a missing whitelist.json
    }

    [Fact]
    public void ManagedFieldsBecomeVariables()
    {
        var game = ServerTestData.Vanilla().Game with
        {
            Motd = "Hi", GameMode = GameMode.Creative, Pvp = false, ViewDistance = 12, SpawnProtection = 0,
            AllowFlight = true, EnableCommandBlock = true, EnforceWhitelist = true,
        };
        var env = ContainerSpecBuilder.Build(ServerTestData.Vanilla() with { Game = game }, Paths, 1000, 1000, Guid.NewGuid()).Environment;

        Assert.Equal("Hi", env["MOTD"]);
        Assert.Equal("creative", env["MODE"]);
        Assert.Equal("FALSE", env["PVP"]);
        Assert.Equal("12", env["VIEW_DISTANCE"]);
        Assert.Equal("0", env["SPAWN_PROTECTION"]);
        Assert.Equal("TRUE", env["ALLOW_FLIGHT"]);
        Assert.Equal("TRUE", env["ENABLE_COMMAND_BLOCK"]);
        Assert.Equal("TRUE", env["ENFORCE_WHITELIST"]);
    }

    [Fact]
    public void ContainerLabelVersionIsIndependentOfTheRecordFormat()
    {
        var labels = ContainerSpecBuilder.Build(ServerTestData.Vanilla(), Paths, 1000, 1000, Guid.NewGuid()).Labels;

        Assert.Equal(ServerLabels.SpecVersion, labels[ServerLabels.Schema]);
        Assert.Equal(5, ServerRecord.CurrentSchemaVersion);
    }
}

[Collection(LocalizerCollection.Name)]
public sealed class SettingsApplyTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly ServerRegistry _registry;
    private readonly FakeServerRuntime _runtime = new();
    private readonly ServerCreationTests.SafeDocker _docker = new();
    private readonly ServerOperationsService _operations;
    private readonly Guid _id;

    public SettingsApplyTests()
    {
        Localizer.Instance.SetLanguage(AppLanguage.English);
        var app = AppPaths.Resolve(k => k == "HOME" ? _temp.Path : null);
        Directory.CreateDirectory(app.DataDir);
        _registry = new ServerRegistry(app);
        var record = ServerRecord.New(ServerTestData.Vanilla() with { RestartPolicy = RestartPolicyKind.UnlessStopped })
            with { Phase = ProvisioningPhase.Ready, ContainerId = "c1", NetworkId = "n1" };
        _registry.CreateAsync(record).GetAwaiter().GetResult();
        _id = record.Definition.Id;
        _runtime.ProvisionedContainerId = "c2";
        _operations = new ServerOperationsService(_registry, _runtime, _docker, NullLogger<ServerOperationsService>.Instance);
    }

    public void Dispose() => _temp.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private GameSettings NewSettings() => _registry.Load(_id)!.Record!.Definition.Game with { Motd = "New MOTD", MaxPlayers = 42 };

    [Fact]
    public async Task RunningServerIsStoppedRecreatedWithoutRestartPolicyThenRestored()
    {
        _runtime.Containers.Add(FakeServerRuntime.State(_id, "running"));

        var result = await _operations.ApplySettingsAsync(_id, NewSettings(), Ct);

        Assert.Equal(SettingsApplyOutcome.AppliedAndRestarted, result.Outcome);
        Assert.Equal(["policy:No", "stop", "remove", "provision", "policy:UnlessStopped", "start"], _runtime.Calls);
        Assert.Equal(RestartPolicyKind.No, Assert.Single(_runtime.Provisioned).RestartPolicy); // replacement cannot auto-start
        var record = _registry.Load(_id)!.Record!;
        Assert.Null(record.PendingChange);
        Assert.Equal("New MOTD", record.Definition.Game.Motd);
        Assert.Equal(RestartPolicyKind.UnlessStopped, record.Definition.RestartPolicy);
        Assert.Equal("c2", record.ContainerId);
    }

    [Fact]
    public async Task StoppedServerIsSavedForTheNextStart()
    {
        _runtime.Containers.Add(FakeServerRuntime.State(_id, "exited"));

        var result = await _operations.ApplySettingsAsync(_id, NewSettings(), Ct);

        Assert.Equal(SettingsApplyOutcome.SavedForNextStart, result.Outcome);
        Assert.DoesNotContain("stop", _runtime.Calls);
        Assert.DoesNotContain("start", _runtime.Calls);
    }

    [Fact]
    public async Task ForceStopDuringTheChangeAbortsItAndKeepsTheOldContainer()
    {
        _runtime.Containers.Add(FakeServerRuntime.State(_id, "running"));
        var stopping = new TaskCompletionSource();
        _runtime.Stop = (_, _) => stopping.Task;

        var apply = _operations.ApplySettingsAsync(_id, NewSettings(), Ct);
        await Task.Delay(50, Ct);
        await _operations.ForceStopAsync(_id, Ct);
        stopping.SetResult();

        var error = await Assert.ThrowsAsync<ServerOperationException>(() => apply);
        Assert.Equal(ServerOperationError.SettingsAborted, error.Error);
        Assert.Equal(["policy:No", "stop", "kill", "policy:UnlessStopped"], _runtime.Calls); // policy restored, nothing removed
        var record = _registry.Load(_id)!.Record!;
        Assert.Null(record.PendingChange);
        Assert.Null(record.Definition.Game.Motd);
        Assert.Equal("c1", record.ContainerId);
    }

    [Theory]
    [InlineData(SettingsChangeStep.Prepared)]
    [InlineData(SettingsChangeStep.PolicyDisabled)]
    [InlineData(SettingsChangeStep.Stopped)]
    [InlineData(SettingsChangeStep.OldRemoved)]
    [InlineData(SettingsChangeStep.ReplacementCreated)]
    public async Task InterruptedChangeIsCompletedByRepairButNeverStarted(SettingsChangeStep step)
    {
        var record = _registry.Load(_id)!.Record!;
        var newDefinition = record.Definition with { Game = NewSettings() };
        var crashed = record with
        {
            PendingChange = new PendingSettingsChange(record.Definition, newDefinition, WasRunning: true, step),
            ContainerId = step >= SettingsChangeStep.OldRemoved ? null : "c1",
        };
        await _registry.SaveAsync(crashed, Ct);
        if (step < SettingsChangeStep.OldRemoved)
        {
            _runtime.Containers.Add(FakeServerRuntime.State(_id, "running")); // e.g. the old one still running
        }

        Assert.Equal(ServerStatus.Incomplete, ServerCatalog.Build(_registry.LoadAll(), _runtime.Containers).Single().Status);
        await Assert.ThrowsAsync<ServerOperationException>(() => _operations.StartAsync(_id, Ct)); // blocked while pending

        await _operations.RepairAsync(_id, Ct);

        var done = _registry.Load(_id)!.Record!;
        Assert.Null(done.PendingChange);
        Assert.Equal("New MOTD", done.Definition.Game.Motd);
        Assert.DoesNotContain("start", _runtime.Calls);
        Assert.Equal("policy:UnlessStopped", _runtime.Calls[^1]);
    }

    [Fact]
    public async Task ForceStopThatFinishedBeforeTheStateCheckStillAborts()
    {
        _runtime.Containers.Add(FakeServerRuntime.State(_id, "running"));
        _runtime.PolicyBehavior = async (_, policy) =>
        {
            if (policy == RestartPolicyKind.No)
            {
                // The user forces the stop right after the policy step; by the time Bonfire looks, it is down.
                await _operations.ForceStopAsync(_id, Ct);
                _runtime.Containers[0] = FakeServerRuntime.State(_id, "exited");
            }
        };

        var error = await Assert.ThrowsAsync<ServerOperationException>(() => _operations.ApplySettingsAsync(_id, NewSettings(), Ct));

        Assert.Equal(ServerOperationError.SettingsAborted, error.Error);
        Assert.DoesNotContain("remove", _runtime.Calls);
        Assert.Null(_registry.Load(_id)!.Record!.PendingChange);
    }

    [Fact]
    public async Task ForceStopPastTheRemovalBoundaryAppliesButDoesNotRestart()
    {
        _runtime.Containers.Add(FakeServerRuntime.State(_id, "running"));
        _runtime.RemoveBehavior = () => _operations.ForceStopAsync(_id, Ct); // old container is already being removed

        var result = await _operations.ApplySettingsAsync(_id, NewSettings(), Ct);

        Assert.Equal(SettingsApplyOutcome.SavedForNextStart, result.Outcome);
        Assert.DoesNotContain("start", _runtime.Calls);
        Assert.Equal("New MOTD", _registry.Load(_id)!.Record!.Definition.Game.Motd);
        Assert.Null(_registry.Load(_id)!.Record!.PendingChange);
    }

    [Fact]
    public async Task ApplyingToALegacyRecordUpgradesItsFormat()
    {
        var path = _registry.PathsFor(_id).RecordFile;
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        json["SchemaVersion"] = 1;
        File.WriteAllText(path, json.ToJsonString());
        _runtime.Containers.Add(FakeServerRuntime.State(_id, "running"));
        var stopping = new TaskCompletionSource();
        _runtime.Stop = (_, _) => stopping.Task;

        var apply = _operations.ApplySettingsAsync(_id, NewSettings(), Ct);
        await Task.Delay(50, Ct);

        // While the change is pending, an older Bonfire (format 1) must refuse this record.
        var pending = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        Assert.Equal(ServerRecord.CurrentSchemaVersion, (int)pending["SchemaVersion"]!);
        Assert.NotNull(pending["PendingChange"]);
        stopping.SetResult();
        await apply;
    }

    [Fact]
    public async Task UndefinedGameModeInTheFileFallsBackSafely()
    {
        await File.WriteAllTextAsync(Path.Combine(_registry.PathsFor(_id).DataDir, "server.properties"), "gamemode=4\n", Ct);
        var editor = TestDetails.Settings(_id, _registry, _operations, new AcceptAll());

        await editor.LoadAsync();

        Assert.True(editor.IsLoaded);
        Assert.Equal(GameMode.Survival, editor.SelectedGameMode!.Value);
    }

    [Fact]
    public async Task InvalidSettingsAreRejectedBeforeAnyChange()
    {
        var bad = NewSettings() with { Motd = "%env:RCON_PASSWORD%" };

        var error = await Assert.ThrowsAsync<SettingsValidationException>(() => _operations.ApplySettingsAsync(_id, bad, Ct));

        Assert.Contains("Validation_MotdInvalid", error.Errors);
        Assert.Empty(_runtime.Calls);
        Assert.Null(_registry.Load(_id)!.Record!.PendingChange);
    }

    [Fact]
    public async Task UnchangedSettingsDoNothing()
    {
        var result = await _operations.ApplySettingsAsync(_id, _registry.Load(_id)!.Record!.Definition.Game, Ct);

        Assert.Equal(SettingsApplyOutcome.NoChange, result.Outcome);
        Assert.Empty(_runtime.Calls);
    }

    [Fact]
    public async Task EditorClaimsOnlyChangedFieldsAndPrefillsFromTheFile()
    {
        var paths = _registry.PathsFor(_id);
        await File.WriteAllTextAsync(Path.Combine(paths.DataDir, "server.properties"),
            "motd=Handmade MOTD\nview-distance=7\npvp=false\nspawn-protection=4\n", Ct);
        var editor = TestDetails.Settings(_id, _registry, _operations, new AcceptAll());

        await editor.LoadAsync();
        Assert.Equal("Handmade MOTD", editor.Motd);
        Assert.Equal(7, editor.ViewDistance);
        Assert.False(editor.Pvp);

        editor.Pvp = true; // change exactly one unmanaged field
        var settings = editor.BuildSettings()!;

        Assert.True(settings.Pvp);
        Assert.Null(settings.Motd);          // shown from the file, not claimed
        Assert.Null(settings.ViewDistance);
        Assert.Null(settings.SpawnProtection);
        Assert.Null(settings.GameMode);
    }

    [Fact]
    public async Task AlreadyManagedFieldsKeepTheirCurrentValue()
    {
        var record = _registry.Load(_id)!.Record!;
        await _registry.SaveAsync(record with { Definition = record.Definition with { Game = record.Definition.Game with { ViewDistance = 12 } } }, Ct);
        var editor = TestDetails.Settings(_id, _registry, _operations, new AcceptAll());
        await editor.LoadAsync();

        editor.ViewDistance = 20;

        Assert.Equal(20, editor.BuildSettings()!.ViewDistance);
    }

    private sealed class AcceptAll : IDialogService
    {
        public Task<bool> ConfirmAsync(string title, string message, string confirmText, bool dangerous) => Task.FromResult(true);
    }
}

public sealed class SettingsPersistenceTests
{
    [Fact]
    public async Task VersionOneRecordsStillLoad()
    {
        using var temp = new TempDirectory();
        var app = AppPaths.Resolve(k => k == "HOME" ? temp.Path : null);
        Directory.CreateDirectory(app.DataDir);
        var registry = new ServerRegistry(app);
        var record = ServerRecord.New(ServerTestData.Vanilla());
        var paths = await registry.CreateAsync(record, TestContext.Current.CancellationToken);

        // Rewrite as a v1 record: no PendingChange and none of the newer optional game fields.
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(paths.RecordFile))!.AsObject();
        json["SchemaVersion"] = 1;
        json.Remove("PendingChange");
        var game = json["Definition"]!["Game"]!.AsObject();
        foreach (var key in new[] { "Motd", "GameMode", "Pvp", "ViewDistance", "SpawnProtection", "AllowFlight", "EnableCommandBlock", "EnforceWhitelist" })
        {
            game.Remove(key);
        }

        File.WriteAllText(paths.RecordFile, json.ToJsonString());

        var loaded = Assert.Single(registry.LoadAll());
        Assert.Null(loaded.Error);
        Assert.Null(loaded.Record!.PendingChange);
        Assert.Null(loaded.Record.Definition.Game.ViewDistance);
    }
}

public sealed class ServerPropertiesFileTests
{
    [Fact]
    public void SymlinkedOrOversizedFilesAreNotRead()
    {
        using var temp = new TempDirectory();
        var paths = new ServerPaths(temp.Path);
        Directory.CreateDirectory(paths.DataDir);
        var target = Path.Combine(temp.Path, "secret.txt");
        File.WriteAllText(target, "motd=leak");
        File.CreateSymbolicLink(Path.Combine(paths.DataDir, "server.properties"), target);

        Assert.Null(ServerPropertiesFile.Read(paths));

        File.Delete(Path.Combine(paths.DataDir, "server.properties"));
        File.WriteAllText(Path.Combine(paths.DataDir, "server.properties"), new string('a', (int)ServerPropertiesFile.MaxBytes + 1));
        Assert.Null(ServerPropertiesFile.Read(paths));

        File.WriteAllText(Path.Combine(paths.DataDir, "server.properties"), "motd=ok\n");
        Assert.Equal("ok", ServerPropertiesFile.Read(paths)!["motd"]);
    }

    [Fact]
    public async Task FifoPlantedAsServerPropertiesDoesNotBlock()
    {
        using var temp = new TempDirectory();
        var paths = new ServerPaths(temp.Path);
        Directory.CreateDirectory(paths.DataDir);
        using (var mkfifo = System.Diagnostics.Process.Start("mkfifo", Path.Combine(paths.DataDir, "server.properties"))!)
        {
            await mkfifo.WaitForExitAsync(TestContext.Current.CancellationToken);
        }

        var read = Task.Run(() => ServerPropertiesFile.Read(paths), TestContext.Current.CancellationToken);

        Assert.Same(read, await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)));
        Assert.Null(await read);
    }
}
