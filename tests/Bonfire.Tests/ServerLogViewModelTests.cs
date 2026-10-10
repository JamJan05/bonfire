using Bonfire.App.Localization;
using Bonfire.App.ViewModels;
using Bonfire.Core;
using Bonfire.Core.Logs;
using Bonfire.Core.Servers;
using Bonfire.Infrastructure.Servers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bonfire.Tests;

[Collection(LocalizerCollection.Name)]
public sealed class ServerLogViewModelTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly ServerRegistry _registry;
    private readonly FakeServerRuntime _runtime = new();
    private readonly Guid _id;
    private readonly ServerLogViewModel _logs;

    public ServerLogViewModelTests()
    {
        Localizer.Instance.SetLanguage(AppLanguage.English);
        var app = AppPaths.Resolve(k => k == "HOME" ? _temp.Path : null);
        Directory.CreateDirectory(app.DataDir);
        _registry = new ServerRegistry(app);
        var record = ServerRecord.New(ServerTestData.Vanilla()) with { Phase = ProvisioningPhase.Ready, ContainerId = "c1", NetworkId = "n1" };
        _registry.CreateAsync(record).GetAwaiter().GetResult();
        _id = record.Definition.Id;
        _runtime.Containers.Add(FakeServerRuntime.State(_id, "running"));
        _logs = new ServerLogViewModel(_id, _registry, _runtime, Localizer.Instance, TimeProvider.System, NullLogger.Instance);
    }

    public void Dispose()
    {
        _logs.Dispose();
        _temp.Dispose();
    }

    private static LogLine L(string ts, string text, LogLevel level = LogLevel.Info) => new(ts, null, text, LogSource.Stdout, level, false);

    [Fact]
    public void PendingQueueIsBoundedAndCountsDroppedLines()
    {
        for (var i = 0; i < ServerLogViewModel.MaxPending + 50; i++)
        {
            _logs.Enqueue(L("t", $"line {i}"));
        }

        for (var i = 0; i < 20; i++)
        {
            _logs.DrainPending();
        }

        Assert.Equal(ServerLogViewModel.MaxLines, _logs.Visible.Count);
        Assert.Contains("50", _logs.DroppedText);
        Assert.Equal($"line {ServerLogViewModel.MaxPending + 49}", _logs.Visible[^1].Text);
    }

    [Fact]
    public void OneDrainDoesBoundedWork()
    {
        for (var i = 0; i < 5000; i++)
        {
            _logs.Enqueue(L("t", $"line {i}"));
        }

        _logs.DrainPending();

        Assert.Equal(ServerLogViewModel.MaxPerDrain, _logs.Visible.Count);
    }

    [Fact]
    public void VisibleLinesExpireTogetherWithTheBuffer()
    {
        _logs.FilterText = "needle";
        _logs.Enqueue(L("t", "needle"));
        for (var i = 0; i < ServerLogViewModel.MaxLines; i++)
        {
            _logs.Enqueue(L("t", "hay"));
        }

        for (var i = 0; i < 10; i++)
        {
            _logs.DrainPending();
        }

        Assert.Empty(_logs.Visible); // the matching line left the retained buffer
        _logs.FilterText = "";
        Assert.Equal(ServerLogViewModel.MaxLines, _logs.Visible.Count);
        Assert.All(_logs.Visible, v => Assert.Equal("hay", v.Text));
    }

    [Fact]
    public void LevelFilterNeverHidesUnparsedLines()
    {
        _logs.Enqueue(L("t", "[x] [Server thread/INFO]: hello"));
        _logs.Enqueue(L("t", "\tat Foo.bar()", LogLevel.Unknown));
        _logs.Enqueue(L("t", "[x] [Server thread/WARN]: careful", LogLevel.Warn));
        _logs.DrainPending();

        _logs.ShowInfo = false;
        _logs.ShowWarn = false;

        Assert.Equal(["\tat Foo.bar()"], _logs.Visible.Select(v => v.Text));
    }

    [Fact]
    public void TextFilterIsCaseInsensitive()
    {
        _logs.Enqueue(L("t", "Player Steve joined"));
        _logs.Enqueue(L("t", "Saving chunks"));
        _logs.DrainPending();

        _logs.FilterText = "steve";

        Assert.Equal(["Player Steve joined"], _logs.Visible.Select(v => v.Text));
    }

    [Fact]
    public void PausedAutoScrollKeepsIngestingAndCountsUnread()
    {
        _logs.AutoScroll = false;
        _logs.Enqueue(L("t", "a"));
        _logs.Enqueue(L("t", "b"));
        _logs.DrainPending();

        Assert.Equal(2, _logs.Unread);
        Assert.Equal(2, _logs.Visible.Count);

        _logs.AutoScroll = true;
        Assert.Equal(0, _logs.Unread);
    }

    [Fact]
    public async Task ReconnectUsesTheCursorAndSkipsOnlyAlreadyShownBoundaryLines()
    {
        var attempt = 0;
        _runtime.Logs = (since, onLine, ct) =>
        {
            attempt++;
            if (attempt == 1)
            {
                onLine(L("T1", "first"));
                onLine(L("T2", "same-ts A"));
                return Task.CompletedTask; // stream ends
            }

            if (attempt == 2)
            {
                Assert.Equal("T2", since);
                onLine(L("T2", "same-ts A")); // repeated by Docker's inclusive "since": skipped
                onLine(L("T2", "same-ts B")); // new line with the same timestamp: kept
                onLine(L("T3", "later"));
                return Task.CompletedTask;
            }

            return Task.Delay(Timeout.Infinite, ct);
        };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var follow = _logs.FollowAsync(cts.Token);
        while (attempt < 3 && !cts.IsCancellationRequested)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        await cts.CancelAsync();
        await follow;
        _logs.DrainPending();

        Assert.Equal(["first", "same-ts A", "same-ts B", "later"], _logs.Visible.Select(v => v.Text));
        Assert.Equal([null, "T2", "T3"], _runtime.LogSinces.Take(3));
    }

    [Fact]
    public async Task RecreatedContainerResetsTheCursor()
    {
        var attempt = 0;
        _runtime.Logs = (since, onLine, ct) =>
        {
            attempt++;
            if (attempt == 1)
            {
                onLine(L("T9", "old container"));
                // Simulate "apply settings": the record now points at a new container.
                var record = _registry.Load(_id)!.Record! with { ContainerId = "c2" };
                _registry.SaveAsync(record).GetAwaiter().GetResult();
                _runtime.Containers[0] = FakeServerRuntime.State(_id, "running", containerId: "c2");
                return Task.CompletedTask;
            }

            return Task.Delay(Timeout.Infinite, ct);
        };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var follow = _logs.FollowAsync(cts.Token);
        while (_runtime.LogSinces.Count < 2 && !cts.IsCancellationRequested)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        await cts.CancelAsync();
        await follow;
        _logs.DrainPending();

        Assert.Equal([null, null], _runtime.LogSinces.Take(2)); // the new container starts from its tail
        Assert.Contains(_logs.Visible, v => v.Text.Contains("recreated", StringComparison.Ordinal));
    }
}
