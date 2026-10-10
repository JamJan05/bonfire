using Bonfire.Core.Backups;
using Bonfire.Core.Servers;
using Bonfire.Infrastructure.Backups;
using Bonfire.Infrastructure.Linux;
using Bonfire.Infrastructure.Servers;
using Microsoft.Extensions.Logging;

namespace Bonfire.App.Services;

/// <summary>
/// Replaces a stopped server's data with a verified backup. Every step is persisted first; the
/// swap is two renames inside the server directory, and recovery decides from what actually exists
/// on disk. The previous data is kept as the newest "data-before-restore-…" rollback directory.
/// The server is never started by a restore or by its recovery.
/// </summary>
public sealed class RestoreEngine
{
    public const string RollbackPrefix = "data-before-restore-";

    private readonly ServerRegistry _registry;
    private readonly IServerRuntime _runtime;
    private readonly TimeProvider _time;
    private readonly ILogger<RestoreEngine> _logger;

    public RestoreEngine(ServerRegistry registry, IServerRuntime runtime, TimeProvider time, ILogger<RestoreEngine> logger)
    {
        _registry = registry;
        _runtime = runtime;
        _time = time;
        _logger = logger;
    }

    /// <summary>Caller guarantees: serialized per server, no pending operation, container not running.</summary>
    public async Task RestoreAsync(ServerRecord record, BackupInfo backup, IProgress<BackupProgress>? progress, CancellationToken cancellationToken)
    {
        if (!backup.IsUsable)
        {
            throw new BackupException(BackupError.Corrupt, backup.Error ?? "unusable");
        }

        if (!backup.Record!.Manifest.MatchesVersionsOf(record.Definition))
        {
            throw new BackupException(BackupError.VersionMismatch, $"{backup.Record.Manifest.MinecraftVersion} vs {record.Definition.MinecraftVersion}");
        }

        var paths = _registry.PathsFor(record.Definition.Id);
        BackupStore.EnsureSpace(paths.ServerDir, backup.DataBytes + (64L * 1024 * 1024));
        RemoveLeftovers(paths.ServerDir); // no restore is pending (caller guarantees)

        var operation = Guid.NewGuid();
        var tag = operation.ToString("N")[..8];
        record = record with
        {
            PendingRestore = new PendingRestore(
                operation, backup.BackupId, backup.Record.ArchiveSha256,
                $"restore-staging-{tag}", $"{RollbackPrefix}{_time.GetUtcNow():yyyyMMdd-HHmmss}-{tag}",
                record.Definition.RestartPolicy, RestoreStep.Prepared,
                backup.Record.Manifest.HasContent ? $"{ExtrasPrefix}{tag}" : null,
                ManagesContent: true),
        };
        await _registry.SaveAsync(record, CancellationToken.None);

        try
        {
            if (record.ContainerId is not null)
            {
                // Nothing may start the container while its data is being exchanged.
                await IgnoreMissing(() => _runtime.SetRestartPolicyAsync(record, RestartPolicyKind.No, cancellationToken));
            }

            record = await StepAsync(record, RestoreStep.PolicyDisabled);
            await BackupStore.ExtractVerifiedAsync(backup, Path.Combine(paths.ServerDir, record.PendingRestore!.StagingDirName), progress, cancellationToken,
                record.PendingRestore.ExtrasDirName is { } extras ? Path.Combine(paths.ServerDir, extras) : null);
            if (record.PendingRestore.ExtrasDirName is { } extrasDir
                && Path.Combine(paths.ServerDir, extrasDir, BackupManifest.LockPath) is var stagedLock && File.Exists(stagedLock)
                && Bonfire.Infrastructure.Content.ContentLockStore.Deserialize(await File.ReadAllTextAsync(stagedLock, cancellationToken)) is null)
            {
                // Checked before anything live changes: an unusable lock must not strand a half-applied restore.
                throw new BackupException(BackupError.Corrupt, "content-lock.json in the backup is invalid");
            }

            SafeFiles.FsyncDirectory(paths.ServerDir);
            record = await StepAsync(record, RestoreStep.Extracted);
        }
        catch
        {
            await CancelAsync(record); // nothing live was touched yet
            throw;
        }

        // From here on the verified data is complete: the swap is finished even if cancelled.
        await SwapAndCommitAsync(record);
    }

    /// <summary>Finishes or cancels an interrupted restore, judging by what exists on disk. Never starts the server.</summary>
    public async Task RecoverAsync(ServerRecord record)
    {
        var pending = record.PendingRestore!;

        // Before touching directories: no automatic restart, and the container really stopped.
        // If Docker cannot tell us, this throws and the pending restore is kept for a later Repair.
        if (record.ContainerId is not null)
        {
            await IgnoreMissing(() => _runtime.SetRestartPolicyAsync(record, RestartPolicyKind.No, CancellationToken.None));
            if ((await _runtime.ListAsync(CancellationToken.None)).Any(s => s.ContainerId == record.ContainerId && s.IsRunning))
            {
                _logger.LogWarning("Container of {ServerId} was running during restore recovery; stopping it", record.Definition.Id);
                await _runtime.StopAsync(record, CancellationToken.None);
            }
        }

        var paths = _registry.PathsFor(record.Definition.Id);
        var staging = Path.Combine(paths.ServerDir, pending.StagingDirName);
        var rollback = Path.Combine(paths.ServerDir, pending.RollbackDirName);
        var data = paths.DataDir;

        if (Directory.Exists(data) && !Directory.Exists(rollback))
        {
            // The live data was never moved: the restore did not happen. Drop the staging copy.
            _logger.LogWarning("Interrupted restore of {ServerId} before the swap; cancelling", record.Definition.Id);
            await CancelAsync(record);
            return;
        }

        var restored = true;
        if (!Directory.Exists(data) && Directory.Exists(staging) && pending.Step >= RestoreStep.Extracted)
        {
            // Data moved aside, verified staging present: finish the swap.
            Directory.Move(staging, data);
            SafeFiles.FsyncDirectory(paths.ServerDir);
        }
        else if (!Directory.Exists(data) && Directory.Exists(rollback))
        {
            // No complete replacement: put the previous data back.
            Directory.Move(rollback, data);
            SafeFiles.FsyncDirectory(paths.ServerDir);
            restored = false;
        }

        // Never move a recorded step backwards: after Swapped, recovery only rolls forward.
        var step = pending.Step > RestoreStep.Swapped ? pending.Step : RestoreStep.Swapped;
        await CommitAsync(record with { PendingRestore = pending with { Step = step } }, restored);
    }

    private async Task SwapAndCommitAsync(ServerRecord record)
    {
        var pending = record.PendingRestore!;
        var paths = _registry.PathsFor(record.Definition.Id);

        record = await StepAsync(record, RestoreStep.DataMovedAside); // intent first, then the rename
        Directory.Move(paths.DataDir, Path.Combine(paths.ServerDir, pending.RollbackDirName));
        SafeFiles.FsyncDirectory(paths.ServerDir);

        Directory.Move(Path.Combine(paths.ServerDir, pending.StagingDirName), paths.DataDir);
        SafeFiles.FsyncDirectory(paths.ServerDir);
        record = await StepAsync(record, RestoreStep.Swapped);

        await CommitAsync(record, restored: true);
    }

    private async Task CommitAsync(ServerRecord record, bool restored)
    {
        var pending = record.PendingRestore!;
        var paths = _registry.PathsFor(record.Definition.Id);
        if (restored && pending.ManagesContent)
        {
            record = await ApplyContentAsync(record);
        }
        else if (restored)
        {
            // A restore journal from before add-on backups: the lock no longer describes the files.
            Bonfire.Infrastructure.Content.ContentLockStore.ResetAfterRestore(paths);
        }

        if (restored)
        {
            // The history described the files before the restore (renamed away first: durable at once).
            Bonfire.Infrastructure.Content.ContentHistoryStore.Clear(paths);
        }

        if (record.ContainerId is not null)
        {
            await IgnoreMissing(() => _runtime.SetRestartPolicyAsync(record, pending.OriginalPolicy, CancellationToken.None));
        }

        await _registry.SaveAsync(record with { PendingRestore = null }, CancellationToken.None);

        // Keep only the newest rollback set: previous data + previous add-on state (the server is stopped).
        foreach (var old in Directory.EnumerateDirectories(paths.ServerDir, RollbackPrefix + "*")
                     .Where(d => Path.GetFileName(d) != pending.RollbackDirName && Path.GetFileName(d) != pending.ContentAsideDirName))
        {
            SafeFiles.DeleteTree(old, paths.ServerDir);
        }

        RemoveLeftovers(paths.ServerDir); // repeatable: also runs before the next restore
    }

    public const string ExtrasPrefix = "restore-extras-";

    /// <summary>Test hook: runs after each add-on step of a restore (failure injection).</summary>
    internal Action<RestoreStep>? AfterContentStep { get; set; }

    /// <summary>
    /// Replaces the add-on state outside /data (lock and disabled files) with the backup's, in two
    /// recorded steps. Everything previous goes into the rollback set first, so "already applied"
    /// is never confused with "the backup has none": a backup without add-on state (format 1)
    /// leaves no lock and no disabled files rather than a mix of two snapshots.
    /// </summary>
    private async Task<ServerRecord> ApplyContentAsync(ServerRecord record)
    {
        var pending = record.PendingRestore!;
        var paths = _registry.PathsFor(record.Definition.Id);
        var lockPath = Bonfire.Infrastructure.Content.ContentLockStore.PathFor(paths);
        var disabled = Path.Combine(paths.ServerDir, Bonfire.Infrastructure.Content.ContentFiles.DisabledRoot);
        var aside = Path.Combine(paths.ServerDir, pending.ContentAsideDirName);

        if (pending.Step < RestoreStep.ContentAside)
        {
            SafeFiles.EnsureRealDirectory(aside);
            foreach (var (from, name) in new[] { (disabled, "disabled-content"), (lockPath, "content-lock.json"), (lockPath + ".bak", "content-lock.json.bak") })
            {
                var to = Path.Combine(aside, name);
                if (SafeFiles.EntryExists(from) && !SafeFiles.EntryExists(to))
                {
                    SafeFiles.MoveNoReplace(from, to);
                }
            }

            SafeFiles.FsyncDirectory(aside);
            SafeFiles.FsyncDirectory(paths.ServerDir);
            record = await StepAsync(record, RestoreStep.ContentAside);
            AfterContentStep?.Invoke(RestoreStep.ContentAside);
        }

        if (record.PendingRestore!.Step < RestoreStep.ContentApplied)
        {
            // The originals were moved aside before ContentAside was recorded: anything there now was put by this step.
            if (pending.ExtrasDirName is { } extrasName)
            {
                var extras = Path.Combine(paths.ServerDir, extrasName);
                var stagedDisabled = Path.Combine(extras, Bonfire.Core.Backups.BackupManifest.DisabledPath);
                if (Directory.Exists(stagedDisabled) && !SafeFiles.EntryExists(disabled))
                {
                    SafeFiles.MoveNoReplace(stagedDisabled, disabled);
                }

                var stagedLock = Path.Combine(extras, Bonfire.Core.Backups.BackupManifest.LockPath);
                if (File.Exists(stagedLock) && !SafeFiles.EntryExists(lockPath))
                {
                    if (Bonfire.Infrastructure.Content.ContentLockStore.Deserialize(await File.ReadAllTextAsync(stagedLock)) is null)
                    {
                        throw new BackupException(BackupError.Corrupt, "content-lock.json in the backup is invalid");
                    }

                    SafeFiles.MoveNoReplace(stagedLock, lockPath);
                }
            }

            SafeFiles.FsyncDirectory(paths.ServerDir);
            record = await StepAsync(record, RestoreStep.ContentApplied);
            AfterContentStep?.Invoke(RestoreStep.ContentApplied);
        }

        return record;
    }

    /// <summary>Staging of finished or abandoned restores (only called when no restore is pending).</summary>
    private static void RemoveLeftovers(string serverDir)
    {
        foreach (var dir in Directory.EnumerateDirectories(serverDir, ExtrasPrefix + "*")
                     .Concat(Directory.EnumerateDirectories(serverDir, "restore-staging-*")))
        {
            SafeFiles.DeleteTree(dir, serverDir);
        }
    }

    private async Task CancelAsync(ServerRecord record)
    {
        var pending = record.PendingRestore!;
        var paths = _registry.PathsFor(record.Definition.Id);
        var staging = Path.Combine(paths.ServerDir, pending.StagingDirName);
        if (Directory.Exists(staging))
        {
            SafeFiles.DeleteTree(staging, paths.ServerDir);
        }

        if (pending.ExtrasDirName is { } extras && Directory.Exists(Path.Combine(paths.ServerDir, extras)))
        {
            SafeFiles.DeleteTree(Path.Combine(paths.ServerDir, extras), paths.ServerDir);
        }

        if (record.ContainerId is not null)
        {
            await IgnoreMissing(() => _runtime.SetRestartPolicyAsync(record, pending.OriginalPolicy, CancellationToken.None));
        }

        await _registry.SaveAsync(record with { PendingRestore = null }, CancellationToken.None);
    }

    private async Task<ServerRecord> StepAsync(ServerRecord record, RestoreStep step)
    {
        record = record with { PendingRestore = record.PendingRestore! with { Step = step } };
        await _registry.SaveAsync(record, CancellationToken.None);
        return record;
    }

    private static async Task IgnoreMissing(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (ServerOperationException ex) when (ex.Error == ServerOperationError.ContainerMissing)
        {
        }
    }
}
