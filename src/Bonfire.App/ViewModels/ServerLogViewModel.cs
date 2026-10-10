using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using Bonfire.App.Localization;
using Bonfire.Core.Logs;
using Bonfire.Core.Servers;
using Bonfire.Infrastructure.Servers;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using LogLevel = Bonfire.Core.Logs.LogLevel;

namespace Bonfire.App.ViewModels;

/// <param name="Line">The retained line this view shows (used to evict it together with the buffer).</param>
public sealed record LogLineView(LogLine Line, string Text, bool IsWarn, bool IsError, bool IsStderr);

/// <summary>
/// Live log of one server. A background loop follows the container's log and re-attaches when
/// the container runs again; lines are queued (bounded) and moved to the UI in batches.
/// Reconnection is best-effort: a few lines at the boundary may be repeated or missed.
/// </summary>
public sealed partial class ServerLogViewModel : ViewModelBase, IDisposable
{
    public const int MaxLines = 5000;
    public const int MaxPending = 20000;

    /// <summary>At most this many lines are moved to the UI per batch, so a flood cannot freeze it.</summary>
    public const int MaxPerDrain = 2000;
    public const int InitialTail = 500;
    public static readonly TimeSpan BatchInterval = TimeSpan.FromMilliseconds(100);
    public static readonly TimeSpan IdlePoll = TimeSpan.FromSeconds(3);

    private readonly Guid _serverId;
    private readonly ServerRegistry _registry;
    private readonly IServerRuntime _runtime;
    private readonly Localizer _localizer;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentQueue<LogLine> _pending = new();
    private readonly Queue<LogLine> _all = new();
    private int _pendingCount;
    private long _dropped;
    private string? _cursorContainerId;
    private string? _cursor;
    private readonly List<string> _boundaryLines = []; // texts of the lines carrying the cursor timestamp
    private volatile string? _statusKey;
    private int _statusDirty;
    private bool _disposed;

    public ServerLogViewModel(Guid serverId, ServerRegistry registry, IServerRuntime runtime, Localizer localizer, TimeProvider time, ILogger logger)
    {
        _serverId = serverId;
        _registry = registry;
        _runtime = runtime;
        _localizer = localizer;
        _time = time;
        _logger = logger;
        localizer.LanguageChanged += OnLanguageChanged;
    }

    public ObservableCollection<LogLineView> Visible { get; } = [];

    /// <summary>Raised after new visible lines arrived while auto-scroll is on.</summary>
    public event EventHandler? ScrollToEndRequested;

    [ObservableProperty]
    private string _filterText = "";

    [ObservableProperty]
    private bool _showInfo = true;

    [ObservableProperty]
    private bool _showWarn = true;

    [ObservableProperty]
    private bool _showError = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UnreadText))]
    private bool _autoScroll = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UnreadText))]
    private int _unread;

    public string? UnreadText => !AutoScroll && Unread > 0 ? _localizer.Format("Logs_Unread", Unread) : null;

    public string? DroppedText => Interlocked.Read(ref _dropped) is > 0 and var d ? _localizer.Format("Logs_Dropped", d) : null;

    public string? StatusText => _statusKey is null ? null : _localizer[_statusKey];

    partial void OnFilterTextChanged(string value) => Rebuild();

    partial void OnShowInfoChanged(bool value) => Rebuild();

    partial void OnShowWarnChanged(bool value) => Rebuild();

    partial void OnShowErrorChanged(bool value) => Rebuild();

    partial void OnAutoScrollChanged(bool value)
    {
        if (value)
        {
            Unread = 0;
            ScrollToEndRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    [RelayCommand]
    private void Clear()
    {
        _all.Clear();
        Visible.Clear();
        Unread = 0;
    }

    /// <summary>Starts following the log and the UI batching loop. Call on the UI thread.</summary>
    public void Start()
    {
        if (_disposed)
        {
            return; // a page closed before it started never starts its loops
        }

        _ = Task.Run(() => FollowAsync(_lifetime.Token));
        _ = BatchLoopAsync(_lifetime.Token);
    }

    private async Task BatchLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(BatchInterval, _time, cancellationToken);
                DrainPending();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Moves queued lines to the visible list (UI thread).</summary>
    public void DrainPending()
    {
        var added = 0;
        for (var i = 0; i < MaxPerDrain && _pending.TryDequeue(out var line); i++)
        {
            Interlocked.Decrement(ref _pendingCount);
            _all.Enqueue(line);
            if (Matches(line))
            {
                Visible.Add(ToView(line));
                added++;
            }

            // Bounds are kept on every line; the visible list only ever shows retained lines.
            if (_all.Count > MaxLines)
            {
                var evicted = _all.Dequeue();
                if (Visible.Count > 0 && ReferenceEquals(Visible[0].Line, evicted))
                {
                    Visible.RemoveAt(0);
                }
            }
        }

        if (added > 0)
        {
            if (AutoScroll)
            {
                ScrollToEndRequested?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                Unread += added;
            }
        }

        OnPropertyChanged(nameof(DroppedText));
        if (Interlocked.Exchange(ref _statusDirty, 0) == 1)
        {
            OnPropertyChanged(nameof(StatusText));
        }
    }

    /// <summary>Called from the reader thread; bounded — the oldest pending lines are dropped first.</summary>
    internal void Enqueue(LogLine line)
    {
        _pending.Enqueue(line);
        if (Interlocked.Increment(ref _pendingCount) > MaxPending && _pending.TryDequeue(out _))
        {
            Interlocked.Decrement(ref _pendingCount);
            Interlocked.Increment(ref _dropped);
        }
    }

    internal async Task FollowAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var record = _registry.Load(_serverId)?.Record;
                if (record?.ContainerId is not { } containerId)
                {
                    SetStatus("Logs_NoContainer");
                    await Task.Delay(IdlePoll, _time, cancellationToken);
                    continue;
                }

                if (containerId != _cursorContainerId)
                {
                    // A recreated container has its own log; start over.
                    if (_cursorContainerId is not null)
                    {
                        Enqueue(new LogLine(null, null, _localizer["Logs_NewContainer"], LogSource.Stdout, LogLevel.Unknown, false));
                    }

                    _cursorContainerId = containerId;
                    _cursor = null;
                    _boundaryLines.Clear();
                }

                var state = (await _runtime.ListAsync(cancellationToken)).FirstOrDefault(s => s.ContainerId == containerId);
                if (state is null)
                {
                    SetStatus("Logs_NoContainer");
                    await Task.Delay(IdlePoll, _time, cancellationToken);
                    continue;
                }

                if (!state.IsRunning && _cursor is not null)
                {
                    // Already showed this stopped container's log; wait until it runs again.
                    SetStatus("Logs_NotRunning");
                    await Task.Delay(IdlePoll, _time, cancellationToken);
                    continue;
                }

                SetStatus(state.IsRunning ? null : "Logs_NotRunning");
                var secrets = _registry.ReadRconSecret(_serverId) is { } secret ? new[] { secret } : [];
                var boundary = new List<string>(_boundaryLines);
                var since = _cursor;
                await _runtime.StreamLogsAsync(record, since, InitialTail, secrets, line =>
                {
                    // Skip lines already shown at the reconnect boundary (Docker's "since" may include them).
                    if (since is not null && line.RawTimestamp == since && boundary.Remove(line.Text))
                    {
                        return;
                    }

                    Track(line);
                    Enqueue(line);
                }, cancellationToken);

                // The stream ended: the container stopped, restarted or the daemon went away.
                SetStatus("Logs_StreamEnded");
                await Task.Delay(TimeSpan.FromSeconds(1), _time, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Following server log failed: {Message}", ex.Message);
                SetStatus("Logs_ErrorState");
                try
                {
                    await Task.Delay(IdlePoll, _time, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private void Track(LogLine line)
    {
        if (line.RawTimestamp is null)
        {
            return;
        }

        if (line.RawTimestamp != _cursor)
        {
            _cursor = line.RawTimestamp;
            _boundaryLines.Clear();
        }

        if (_boundaryLines.Count < 1000)
        {
            _boundaryLines.Add(line.Text);
        }
    }

    /// <summary>Called from the reader thread; the UI is notified on the next batch (UI thread).</summary>
    private void SetStatus(string? key)
    {
        if (_statusKey != key)
        {
            _statusKey = key;
            Interlocked.Exchange(ref _statusDirty, 1);
        }
    }

    private bool Matches(LogLine line)
    {
        var levelOk = line.Level switch
        {
            LogLevel.Info => ShowInfo,
            LogLevel.Warn => ShowWarn,
            LogLevel.Error => ShowError,
            _ => true, // unparsed lines (stack traces, startup output) are never hidden by level
        };
        return levelOk && (FilterText.Length == 0 || line.Text.Contains(FilterText, StringComparison.OrdinalIgnoreCase));
    }

    private static LogLineView ToView(LogLine line) =>
        new(line, line.Truncated ? line.Text + " …" : line.Text, line.Level == LogLevel.Warn, line.Level == LogLevel.Error, line.Source == LogSource.Stderr);

    private void Rebuild()
    {
        Visible.Clear();
        foreach (var line in _all.Where(Matches))
        {
            Visible.Add(ToView(line));
        }

        Unread = 0;
        ScrollToEndRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(UnreadText));
        OnPropertyChanged(nameof(DroppedText));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _localizer.LanguageChanged -= OnLanguageChanged;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
