using Bonfire.App.Localization;
using Bonfire.App.ViewModels;
using Bonfire.Core;
using Bonfire.Core.Docker;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bonfire.Tests;

[Collection(LocalizerCollection.Name)]
public sealed class DockerStatusViewModelTests
{
    private sealed class FakeEngine : IDockerEngine
    {
        public int Calls;
        public Func<CancellationToken, Task<DockerProbeResult>> Probe { get; set; } =
            _ => Task.FromResult(Connected());

        public Task<DockerProbeResult> ProbeAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            return Probe(cancellationToken);
        }
    }

    private static readonly DockerEndpoint Endpoint = new("/var/run/docker.sock");

    private static DockerProbeResult Connected(DockerCapabilities? caps = null) => new(
        DockerConnectionStatus.Connected,
        Endpoint,
        DateTimeOffset.UtcNow,
        new DockerDaemonInfo("29.8.0", new Version(1, 40), new Version(1, 56), new Version(1, 56), "linux", "6.18", "amd64"),
        caps ?? new DockerCapabilities(false, true, "builtin", "2", "cgroupfs"));

    private static DockerStatusViewModel Create(FakeEngine engine)
    {
        Localizer.Instance.SetLanguage(AppLanguage.English);
        return new DockerStatusViewModel(engine, Localizer.Instance, NullLogger<DockerStatusViewModel>.Instance);
    }

    [Fact]
    public async Task ConnectedResultIsShown()
    {
        using var vm = Create(new FakeEngine());

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.True(vm.IsConnected);
        Assert.False(vm.IsProblem);
        Assert.Equal("Docker 29.8.0", vm.ShortText);
        Assert.Equal("Connected to Docker", vm.Title);
        Assert.Contains(vm.Details, r => r.Value == "enabled, profile: builtin");
        Assert.Empty(vm.Warnings);
    }

    [Fact]
    public async Task PermissionProblemShowsSocketAndHint()
    {
        var engine = new FakeEngine
        {
            Probe = _ => Task.FromResult(new DockerProbeResult(DockerConnectionStatus.PermissionDenied, Endpoint, DateTimeOffset.UtcNow)),
        };
        using var vm = Create(engine);

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.True(vm.IsProblem);
        Assert.Equal("No access to Docker", vm.Title);
        Assert.Contains("/var/run/docker.sock", vm.Message);
        Assert.Contains("log out", vm.Message);
    }

    [Fact]
    public async Task TextsFollowLanguageChange()
    {
        using var vm = Create(new FakeEngine());
        await vm.RefreshCommand.ExecuteAsync(null);
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        try
        {
            Localizer.Instance.SetLanguage(AppLanguage.Polish);

            Assert.Equal("Połączono z Dockerem", vm.Title);
            Assert.Contains(nameof(vm.Title), raised);
            Assert.Contains(nameof(vm.Details), raised);
        }
        finally
        {
            Localizer.Instance.SetLanguage(AppLanguage.English);
        }
    }

    [Fact]
    public async Task UnknownCapabilitiesAreAWarningNotMissingSeccomp()
    {
        var engine = new FakeEngine
        {
            Probe = _ => Task.FromResult(Connected() with { Capabilities = null }),
        };
        using var vm = Create(engine);

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.True(vm.IsConnected);
        Assert.Single(vm.Warnings);
        Assert.Contains(vm.Details, r => r.Label == "Seccomp" && r.Value == "unknown");
    }

    [Fact]
    public async Task RootlessAndNoSeccompProduceWarnings()
    {
        var engine = new FakeEngine
        {
            Probe = _ => Task.FromResult(Connected(new DockerCapabilities(true, false, null, "2", "systemd"))),
        };
        using var vm = Create(engine);

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.Warnings.Count);
        Assert.True(vm.HasWarnings);
    }

    [Fact]
    public async Task OnlyOneProbeRunsAtATime()
    {
        var gate = new TaskCompletionSource<DockerProbeResult>();
        var engine = new FakeEngine { Probe = _ => gate.Task };
        using var vm = Create(engine);

        var first = vm.RefreshCommand.ExecuteAsync(null);
        Assert.True(vm.IsChecking);
        Assert.False(vm.RefreshCommand.CanExecute(null));
        vm.RefreshCommand.Execute(null); // ignored while running

        gate.SetResult(Connected());
        await first;

        Assert.Equal(1, engine.Calls);
        Assert.False(vm.IsChecking);
    }

    [Fact]
    public async Task DisposeCancelsRunningProbe()
    {
        var engine = new FakeEngine
        {
            Probe = async ct =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                throw new InvalidOperationException("unreachable");
            },
        };
        var vm = Create(engine);

        var running = vm.RefreshCommand.ExecuteAsync(null);
        vm.Dispose();
        await running;

        Assert.Null(vm.Result);
        Assert.False(vm.IsChecking);
    }

    [Fact]
    public async Task UnexpectedExceptionBecomesFailedResult()
    {
        var engine = new FakeEngine { Probe = _ => throw new InvalidOperationException("bug") };
        using var vm = Create(engine);

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(DockerConnectionStatus.Failed, vm.Result!.Status);
        Assert.True(vm.IsProblem);
    }
}
