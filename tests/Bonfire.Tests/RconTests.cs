using Bonfire.App.Localization;
using Bonfire.App.Services;
using Bonfire.App.ViewModels;
using Bonfire.Core;
using Bonfire.Core.Rcon;
using Bonfire.Core.Servers;
using Bonfire.Infrastructure.Servers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Bonfire.Tests;

public sealed class RconParsingTests
{
    [Theory]
    [InlineData("list", null)]
    [InlineData("say hello world", null)]
    [InlineData("", "Rcon_Empty")]
    [InlineData("   ", "Rcon_Empty")]
    [InlineData("say a\nstop", "Rcon_InvalidCharacters")]
    [InlineData("say \u001b[31mred", "Rcon_InvalidCharacters")]
    public void Validation(string command, string? error) => Assert.Equal(error, RconCommands.Validate(command));

    [Fact]
    public void TooLongCommandIsRefused() => Assert.Equal("Rcon_TooLong", RconCommands.Validate(new string('a', 1001)));

    [Theory]
    [InlineData("stop", true)]
    [InlineData(" /STOP ", true)]
    [InlineData("stopwatch", false)]
    [InlineData("say stop", false)]
    public void StopIsRecognisedExactly(string command, bool isStop) => Assert.Equal(isStop, RconCommands.IsStopCommand(command));

    [Fact]
    public void VanillaEmpty()
    {
        var list = RconCommands.ParseList("There are 0 of a max of 10 players online: ");

        Assert.Equal(0, list.Online);
        Assert.Equal(10, list.Max);
        Assert.Empty(list.Names!);
    }

    [Fact]
    public void VanillaWithNames()
    {
        var list = RconCommands.ParseList("There are 2 of a max of 20 players online: Alex, Steve_99");

        Assert.Equal(["Alex", "Steve_99"], list.Names);
    }

    [Fact]
    public void PaperGroupedNames()
    {
        var list = RconCommands.ParseList("There are 2 out of maximum 20 players online.\ndefault: Alex, Steve");

        Assert.Equal(2, list.Online);
        Assert.Equal(["Alex", "Steve"], list.Names);
    }

    [Fact]
    public void CountKnownButNamesUnreliable()
    {
        var list = RconCommands.ParseList("There are 3 of a max of 20 players online: Alex, [AFK] Steve");

        Assert.Equal(3, list.Online);
        Assert.Null(list.Names);
    }

    [Fact]
    public void ShortForm() => Assert.Equal(5, RconCommands.ParseList("5/20 online").Online);

    [Fact]
    public void UnexpectedOutputIsUnknown() => Assert.Equal(PlayerList.Unknown, RconCommands.ParseList("Unknown command"));

    [Fact]
    public void CutOffOutputDropsAPartialSecret()
    {
        const string secret = "hunter2-very-secret";

        Assert.Equal("password is", RconCommands.CleanOutput("password is hunter2-ver", [secret], cutOff: true));
        Assert.Equal("password is hunter2-ver", RconCommands.CleanOutput("password is hunter2-ver", [secret], cutOff: false));
    }

    [Fact]
    public void OutputIsSanitizedAndRedacted() =>
        Assert.Equal("gone pw=********\nline2", RconCommands.CleanOutput("§agone pw=hunter2\r\nline2\u0007\n", ["hunter2"]));
}

[Collection(LocalizerCollection.Name)]
public sealed class RconOperationsTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly ServerRegistry _registry;
    private readonly FakeServerRuntime _runtime = new();
    private readonly ServerCreationTests.SafeDocker _docker = new();
    private readonly ServerOperationsService _operations;
    private readonly Guid _id;

    public RconOperationsTests()
    {
        Localizer.Instance.SetLanguage(AppLanguage.English);
        var app = AppPaths.Resolve(k => k == "HOME" ? _temp.Path : null);
        Directory.CreateDirectory(app.DataDir);
        _registry = new ServerRegistry(app);
        var record = ServerRecord.New(ServerTestData.Vanilla()) with { Phase = ProvisioningPhase.Ready, ContainerId = "c1", NetworkId = "n1" };
        _registry.CreateAsync(record).GetAwaiter().GetResult();
        _id = record.Definition.Id;
        _operations = new ServerOperationsService(_registry, _runtime, _docker, NullLogger<ServerOperationsService>.Instance);
    }

    public void Dispose() => _temp.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("stop")]
    [InlineData("/Stop")]
    public async Task StopFromTheConsoleUsesTheGracefulStop(string command)
    {
        var result = await _operations.SendCommandAsync(_id, command, Ct);

        Assert.Null(result);
        Assert.Equal(["stop"], _runtime.Calls);
        Assert.Empty(_runtime.RconCommands);
    }

    [Fact]
    public async Task OtherCommandsGoToRcon()
    {
        await _operations.SendCommandAsync(_id, "say hi", Ct);

        Assert.Equal(["say hi"], _runtime.RconCommands);
    }

    [Fact]
    public async Task NoCommandsWithoutDocker()
    {
        _docker.Current = null;

        var error = await Assert.ThrowsAsync<ServerOperationException>(() => _operations.SendCommandAsync(_id, "list", Ct));

        Assert.Equal(ServerOperationError.DockerUnsupported, error.Error);
    }

    [Fact]
    public async Task PlayerQueryIsBusyDuringALifecycleOperation()
    {
        var stopping = new TaskCompletionSource();
        _runtime.Stop = (_, _) => stopping.Task;
        var stop = _operations.StopAsync(_id, Ct);

        var error = await Assert.ThrowsAsync<ServerOperationException>(() => _operations.QueryPlayersAsync(_id, Ct));

        Assert.Equal(ServerOperationError.Busy, error.Error);
        stopping.SetResult();
        await stop;
    }

    [Fact]
    public async Task ConsoleHistoryAndOutcomes()
    {
        var console = new ServerConsoleViewModel(_id, _operations, Localizer.Instance, NullLogger.Instance);
        _runtime.Rcon = (cmd, _) => Task.FromResult(cmd == "slow"
            ? new RconResult(RconOutcome.Unknown, "", null, false)
            : new RconResult(RconOutcome.Completed, $"ok {cmd}", 0, false));

        console.Input = "say one";
        await console.SendCommand.ExecuteAsync(null);
        console.Input = "slow";
        await console.SendCommand.ExecuteAsync(null);

        Assert.Equal("ok say one", console.Entries[0].Output);
        Assert.True(console.Entries[1].IsProblem);
        Assert.Contains("may or may not have been executed", console.Entries[1].Output);
        Assert.Single(_runtime.RconCommands, "slow"); // never retried

        console.HistoryPrevious();
        Assert.Equal("slow", console.Input);
        console.HistoryPrevious();
        Assert.Equal("say one", console.Input);
        console.HistoryNext();
        console.HistoryNext();
        Assert.Equal("", console.Input);
    }

    [Fact]
    public async Task ConsoleStopGoesThroughTheServerCard()
    {
        var console = new ServerConsoleViewModel(_id, _operations, Localizer.Instance, NullLogger.Instance);
        var routed = 0;
        console.RequestStop = () =>
        {
            routed++;
            return Task.FromResult(true);
        };

        console.Input = "stop";
        await console.SendCommand.ExecuteAsync(null);

        Assert.Equal(1, routed);
        Assert.Empty(_runtime.RconCommands);
        Assert.Empty(_runtime.Calls);
    }

    [Fact]
    public async Task UnexpectedPlayerRefreshFailureKeepsPolling()
    {
        var time = new FakeTimeProvider();
        var players = new ServerPlayersViewModel(_id, _operations, Localizer.Instance, time);
        var fail = true;
        _runtime.Rcon = (_, _) => fail
            ? throw new HttpRequestException("connection reset")
            : Task.FromResult(new RconResult(RconOutcome.Completed, "There are 0 of a max of 10 players online: ", 0, false));

        players.SetActive(true);
        await Task.Delay(50, Ct);
        Assert.NotNull(players.ErrorText);

        fail = false;
        time.Advance(ServerPlayersViewModel.RefreshInterval);
        await Task.Delay(50, Ct);
        Assert.Null(players.ErrorText);
        Assert.Contains("0 of 10", players.CountText);
        players.Dispose();
    }

    [Fact]
    public async Task InvalidCommandIsRefusedBeforeSending()
    {
        var console = new ServerConsoleViewModel(_id, _operations, Localizer.Instance, NullLogger.Instance);

        console.Input = new string('x', 1001);
        await console.SendCommand.ExecuteAsync(null);

        Assert.Empty(_runtime.RconCommands);
        Assert.True(console.Entries[0].IsProblem);
    }

    [Fact]
    public async Task PlayersPollOnlyWhileActiveAndKeepTheLastListOnBusy()
    {
        var time = new FakeTimeProvider();
        var players = new ServerPlayersViewModel(_id, _operations, Localizer.Instance, time);
        _runtime.Rcon = (_, _) => Task.FromResult(new RconResult(RconOutcome.Completed, "There are 1 of a max of 10 players online: Alex", 0, false));

        players.SetActive(true);
        await Task.Delay(50, Ct);
        Assert.Equal(["Alex"], players.Names);
        var calls = _runtime.RconCommands.Count;

        time.Advance(ServerPlayersViewModel.RefreshInterval);
        await Task.Delay(50, Ct);
        Assert.True(_runtime.RconCommands.Count > calls);

        _runtime.Rcon = (_, _) => throw new ServerOperationException(ServerOperationError.Busy, "x");
        await players.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(["Alex"], players.Names); // last known list kept
        Assert.NotNull(players.ErrorText);

        players.SetActive(false);
        calls = _runtime.RconCommands.Count;
        time.Advance(ServerPlayersViewModel.RefreshInterval * 3);
        await Task.Delay(50, Ct);
        Assert.Equal(calls, _runtime.RconCommands.Count);
        players.Dispose();
    }
}
