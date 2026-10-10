using Bonfire.App.Localization;
using Bonfire.App.Services;
using Bonfire.App.ViewModels;
using Bonfire.Core;
using Bonfire.Core.Servers;
using Bonfire.Infrastructure;
using Bonfire.Infrastructure.Servers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bonfire.Tests;

[Collection(LocalizerCollection.Name)]
public sealed class ServerBackupsViewModelTests : IDisposable
{
    private sealed class Answer(bool yes) : IDialogService, IFolderLauncher
    {
        public int Asked;

        public Task<bool> ConfirmAsync(string title, string message, string confirmText, bool dangerous)
        {
            Asked++;
            return Task.FromResult(yes);
        }

        public Task<bool> OpenAsync(string path) => Task.FromResult(true);

        public Task<bool> OpenUriAsync(Uri uri) => Task.FromResult(true);
    }

    private readonly TempDirectory _temp = new();
    private readonly ServerRegistry _registry;
    private readonly FakeServerRuntime _runtime = new();
    private readonly ServerOperationsService _operations;
    private readonly Guid _id;
    private bool _running;

    public ServerBackupsViewModelTests()
    {
        Localizer.Instance.SetLanguage(AppLanguage.English);
        var app = AppPaths.Resolve(k => k == "HOME" ? _temp.Path : null);
        AppDirectories.Ensure(app);
        _registry = new ServerRegistry(app);
        var record = ServerRecord.New(ServerTestData.Vanilla()) with { Phase = ProvisioningPhase.Ready, ContainerId = "c1", NetworkId = "n1" };
        var paths = _registry.CreateAsync(record).GetAwaiter().GetResult();
        File.WriteAllText(Path.Combine(paths.DataDir, "level.dat"), "world");
        _id = record.Definition.Id;
        _operations = new ServerOperationsService(_registry, _runtime, new ServerCreationTests.SafeDocker(), NullLogger<ServerOperationsService>.Instance);
    }

    public void Dispose() => _temp.Dispose();

    private ServerBackupsViewModel Create(Answer answer)
    {
        var vm = TestDetails.Backups(_id, _registry, _runtime, _operations, answer, answer);
        vm.IsServerRunning = () => _running;
        return vm;
    }

    [Fact]
    public async Task BackingUpARunningServerStopsBacksUpAndStartsAgain()
    {
        _running = true;
        _runtime.Containers.Add(FakeServerRuntime.State(_id, "running"));
        _runtime.Stop = (_, _) =>
        {
            _runtime.Containers[0] = FakeServerRuntime.State(_id, "exited"); // graceful: exit code 0
            return Task.CompletedTask;
        };
        var answer = new Answer(true);
        var vm = Create(answer);

        await vm.CreateCommand.ExecuteAsync(null);

        Assert.Equal(1, answer.Asked);
        Assert.Equal(["stop", "start"], _runtime.Calls);
        var backup = Assert.Single(vm.Backups);
        Assert.True(backup.IsNewestGood);
        Assert.False(backup.CanDelete); // the newest good copy is protected
        Assert.Contains("verified", vm.ResultMessage);
    }

    [Theory]
    [InlineData(137)] // killed (Force stop)
    [InlineData(1)]   // crashed while stopping
    public async Task UncleanStopIsNotBackedUpOrRestarted(int exitCode)
    {
        _running = true;
        _runtime.Containers.Add(FakeServerRuntime.State(_id, "running"));
        _runtime.Stop = (_, _) =>
        {
            _runtime.Containers[0] = FakeServerRuntime.State(_id, "exited") with { ExitCode = exitCode };
            return Task.CompletedTask;
        };
        var vm = Create(new Answer(true));

        await vm.CreateCommand.ExecuteAsync(null);

        Assert.Empty(vm.Backups);
        Assert.DoesNotContain("start", _runtime.Calls);
        Assert.Contains("did not stop cleanly", vm.ResultMessage);
    }

    [Fact]
    public async Task CancellingAfterTheStopStillStartsTheServerAgain()
    {
        _running = true;
        _runtime.Containers.Add(FakeServerRuntime.State(_id, "running"));
        ServerBackupsViewModel? vm = null;
        _runtime.Stop = (_, _) =>
        {
            _runtime.Containers[0] = FakeServerRuntime.State(_id, "exited");
            vm!.CancelCommand.Execute(null); // user cancels right after the stop
            return Task.CompletedTask;
        };
        vm = Create(new Answer(true));

        await vm.CreateCommand.ExecuteAsync(null);

        Assert.Equal(["stop", "start"], _runtime.Calls); // never left stopped
        Assert.Empty(vm.Backups);
    }

    [Fact]
    public async Task DecliningTheStopDoesNothing()
    {
        _running = true;
        var vm = Create(new Answer(false));

        await vm.CreateCommand.ExecuteAsync(null);

        Assert.Empty(_runtime.Calls);
        Assert.Empty(vm.Backups);
    }

    [Fact]
    public async Task RestoreRequiresAStoppedServerAndConfirmation()
    {
        var vm = Create(new Answer(true));
        await vm.CreateCommand.ExecuteAsync(null); // stopped server: no stop needed
        await File.WriteAllTextAsync(Path.Combine(_registry.PathsFor(_id).DataDir, "level.dat"), "changed", TestContext.Current.CancellationToken);

        _running = true;
        await vm.RestoreCommand.ExecuteAsync(vm.Backups[0]);
        Assert.Contains("Stop the server", vm.ResultMessage);

        _running = false;
        await vm.RestoreCommand.ExecuteAsync(vm.Backups[0]);
        Assert.Equal("world", await File.ReadAllTextAsync(Path.Combine(_registry.PathsFor(_id).DataDir, "level.dat"), TestContext.Current.CancellationToken));
        Assert.NotNull(vm.RollbackText);
        Assert.DoesNotContain("start", _runtime.Calls); // a restore never starts the server
    }
}
