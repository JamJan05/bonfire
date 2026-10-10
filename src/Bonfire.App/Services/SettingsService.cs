using Bonfire.Core;
using Bonfire.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Bonfire.App.Services;

/// <summary>
/// Holds the current settings and persists changes. Saves form a serial queue that runs off
/// the UI thread; each save writes the newest value at the time it runs, so the last queued
/// save always leaves the latest settings on disk.
/// </summary>
public sealed class SettingsService
{
    private readonly SettingsStore _store;
    private readonly ILogger<SettingsService> _logger;
    private readonly Lock _queueLock = new();
    private Task<bool> _tail = Task.FromResult(true);

    public SettingsService(SettingsStore store, SettingsLoadResult initial, ILogger<SettingsService> logger)
    {
        _store = store;
        _logger = logger;
        Current = initial.Settings;
        LoadStatus = initial.Status;
    }

    public AppSettings Current { get; private set; }

    public SettingsLoadStatus LoadStatus { get; private set; }

    /// <summary>Whether changes are persisted, or only kept for this session.</summary>
    public bool CanSave => LoadStatus is SettingsLoadStatus.Missing or SettingsLoadStatus.Loaded;

    /// <summary>Applies the change in memory and saves it when allowed. Returns false if saving failed.</summary>
    public Task<bool> UpdateAsync(Func<AppSettings, AppSettings> change)
    {
        Current = change(Current);
        return CanSave ? EnqueueSave() : Task.FromResult(true);
    }

    /// <summary>Overwrites a damaged or unreadable settings file after explicit user confirmation.</summary>
    public async Task<bool> ReplaceFileAsync()
    {
        var previous = LoadStatus;
        // Switch state before queueing so changes made while replacing are saved after it.
        LoadStatus = SettingsLoadStatus.Loaded;
        if (await EnqueueSave())
        {
            return true;
        }

        LoadStatus = previous;
        return false;
    }

    /// <summary>Completes when every save queued so far has finished. Never throws.</summary>
    public Task FlushAsync()
    {
        lock (_queueLock)
        {
            return _tail;
        }
    }

    private Task<bool> EnqueueSave()
    {
        lock (_queueLock)
        {
            _tail = _tail.ContinueWith(
                    _ => SaveCurrentAsync(),
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.Default)
                .Unwrap();
            return _tail;
        }
    }

    private async Task<bool> SaveCurrentAsync()
    {
        try
        {
            await _store.SaveAsync(Current);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Saving settings to {Path} failed", _store.FilePath);
            return false;
        }
    }
}
