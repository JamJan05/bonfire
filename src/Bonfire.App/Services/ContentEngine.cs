using System.Text.RegularExpressions;
using Bonfire.Core.Content;
using Bonfire.Core.Servers;
using Bonfire.Infrastructure.Content;
using Bonfire.Infrastructure.Linux;
using Bonfire.Infrastructure.Servers;
using Microsoft.Extensions.Logging;

namespace Bonfire.App.Services;

/// <summary>The journal and the files on disk disagree; the journal is kept and the server stays blocked.</summary>
public sealed class ContentRecoveryException(string detail) : IOException(detail);

/// <summary>
/// Applies an add-on transaction as a list of renames inside the server directory. The intent is
/// persisted (server.json) before anything changes, including the restart policy; until the moves
/// are durable and marked Committed, recovery moves files back. After that it rolls forward.
/// The server is never started.
/// </summary>
public sealed partial class ContentEngine
{
    public static readonly TimeSpan DockerTimeout = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan StopTimeout = TimeSpan.FromMinutes(5);

    private readonly ServerRegistry _registry;
    private readonly IServerRuntime _runtime;
    private readonly ILogger<ContentEngine> _logger;

    public ContentEngine(ServerRegistry registry, IServerRuntime runtime, ILogger<ContentEngine> logger)
    {
        _registry = registry;
        _runtime = runtime;
        _logger = logger;
    }

    /// <summary>Test hook: runs after each completed move (failure injection).</summary>
    internal Action<int>? AfterMove { get; set; }

    /// <summary>
    /// Caller guarantees: serialized per server, no pending operation, staged files verified and
    /// synced. Cancellation is honoured only until the first move.
    /// </summary>
    public async Task CommitAsync(
        ServerRecord record, ContentFiles files, Guid txId, IReadOnlyList<ContentMove> moves, ContentLock lockAfter,
        ContentChangeKind kind, IReadOnlyList<string> summary, CancellationToken cancellationToken, Guid? undoOf = null)
    {
        var paths = _registry.PathsFor(record.Definition.Id);
        var pending = new PendingContent(txId, files.Directory, moves, lockAfter, record.Definition.RestartPolicy, ContentTxStep.Prepared,
            ContentLockStore.Load(paths), kind, summary.Take(PendingContent.MaxSummary).Select(t => t.Length > 200 ? t[..200] : t).ToList(),
            undoOf, undoOf is null ? ContentHistoryStore.NextSequence(paths) : 0);
        if (!pending.IsWellFormed() || lockAfter.Entries.Any(e => !ContentRules.IsSafeJarName(e.FileName)))
        {
            throw new ArgumentException("Malformed add-on transaction");
        }

        files.EnsureLayout(txId, undoOf);
        CheckSequence(files, txId, moves, undoOf);

        record = record with { PendingContent = pending };
        await _registry.SaveAsync(record, CancellationToken.None); // intent before any change

        try
        {
            await DisableRestartAndRequireStoppedAsync(record, stopIfRunning: false);
            cancellationToken.ThrowIfCancellationRequested(); // last point at which cancelling is free

            for (var i = 0; i < moves.Count; i++)
            {
                var m = moves[i];
                SafeFiles.MoveNoReplace(files.PathFor(m.From, txId, m.FileName, undoOf), files.PathFor(m.To, txId, m.FileName, undoOf));
                SafeFiles.FsyncDirectory(files.DirFor(m.To, txId, undoOf));
                SafeFiles.FsyncDirectory(files.DirFor(m.From, txId, undoOf));
                AfterMove?.Invoke(i);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Add-on transaction {TxId} failed; rolling back", txId);
            await RollBackAsync(record); // throws ContentRecoveryException if files do not match either state
            throw;
        }

        // The state this change leaves (its lock and the files as moved) is fixed now, durably with the
        // Committed marker — recovery must never adopt later changes as the baseline for undo.
        var stateAfter = ContentHistoryStore.StateHash(record.Definition, lockAfter, paths, files.Directory);
        record = record with { PendingContent = pending with { Step = ContentTxStep.Committed, StateAfter = stateAfter } };
        await _registry.SaveAsync(record, CancellationToken.None);
        await FinishAsync(record);
    }

    /// <summary>Finishes or rolls back an interrupted transaction (Repair). Never starts the server.</summary>
    public async Task RecoverAsync(ServerRecord record)
    {
        var pending = record.PendingContent!;
        var files = new ContentFiles(_registry.PathsFor(record.Definition.Id), pending.Directory);
        if (pending.Step == ContentTxStep.Committed)
        {
            await FinishAsync(record);
            return;
        }

        if (pending.Step == ContentTxStep.RolledBack)
        {
            await CleanUpAsync(record, files);
            return;
        }

        // Files may only be moved back while nothing can run (or restart) the server — and only
        // after checking again that no directory on the way was replaced by a link meanwhile.
        await DisableRestartAndRequireStoppedAsync(record, stopIfRunning: true);
        try
        {
            files.EnsureLayout(pending.TxId, pending.UndoOf);
        }
        catch (IOException ex)
        {
            throw new ContentRecoveryException(ex.Message);
        }

        await RollBackAsync(record);
    }

    /// <summary>Deletes leftover staging directories of transactions that never got a journal.</summary>
    public void CleanStaleStaging(ServerPaths paths, Guid? keep = null)
    {
        foreach (var dir in Directory.EnumerateDirectories(paths.ServerDir, PendingContent.TxDirPrefix + "*"))
        {
            var name = Path.GetFileName(dir);
            if (TxDirName().IsMatch(name) && name != (keep is { } k ? PendingContent.TxDirPrefix + k.ToString("N") : null))
            {
                SafeFiles.DeleteTree(dir, paths.ServerDir);
            }
        }
    }

    [GeneratedRegex(@"^content-tx-[0-9a-f]{32}\z")]
    private static partial Regex TxDirName();

    /// <summary>
    /// Every source must hold exactly the expected file and every destination must be free, in
    /// sequence. A location is used at most once per transaction (no replacements in one step).
    /// </summary>
    private static void CheckSequence(ContentFiles files, Guid txId, IReadOnlyList<ContentMove> moves, Guid? archive)
    {
        var used = new HashSet<(ContentLocation, string)>();
        foreach (var m in moves)
        {
            if (!used.Add((m.From, m.FileName)) || !used.Add((m.To, m.FileName)))
            {
                throw new ArgumentException("A file location is used twice in one transaction");
            }

            if (files.HashAt(m.From, txId, m.FileName, archive) != m.Sha512)
            {
                throw new ContentException(ContentError.PlanStale, m.FileName);
            }

            if (SafeFiles.EntryExists(files.PathFor(m.To, txId, m.FileName, archive)))
            {
                throw new ContentException(ContentError.NameCollision, m.FileName);
            }
        }
    }

    private async Task RollBackAsync(ServerRecord record)
    {
        var pending = record.PendingContent!;
        var files = new ContentFiles(_registry.PathsFor(record.Definition.Id), pending.Directory);
        foreach (var m in pending.Moves.Reverse())
        {
            string? fromSha, toSha;
            try
            {
                fromSha = files.HashAt(m.From, pending.TxId, m.FileName, pending.UndoOf);
                toSha = files.HashAt(m.To, pending.TxId, m.FileName, pending.UndoOf);
            }
            catch (IOException ex)
            {
                throw new ContentRecoveryException($"{m.FileName}: {ex.Message}");
            }

            if (toSha == m.Sha512 && fromSha is null)
            {
                SafeFiles.MoveNoReplace(files.PathFor(m.To, pending.TxId, m.FileName, pending.UndoOf), files.PathFor(m.From, pending.TxId, m.FileName, pending.UndoOf));
                SafeFiles.FsyncDirectory(files.DirFor(m.From, pending.TxId, pending.UndoOf));
                SafeFiles.FsyncDirectory(files.DirFor(m.To, pending.TxId, pending.UndoOf)); // the removal must be durable too
            }
            else if (!(fromSha == m.Sha512 && toSha is null))
            {
                // Neither "moved" nor "not moved": something else changed the files. Keep the evidence.
                throw new ContentRecoveryException($"{m.FileName} does not match the transaction");
            }
        }

        // Durable before the staging evidence goes: a crash during cleanup must not look like a mismatch.
        record = record with { PendingContent = pending with { Step = ContentTxStep.RolledBack } };
        await _registry.SaveAsync(record, CancellationToken.None);
        await CleanUpAsync(record, files);
    }

    /// <summary>Test hook: runs before the history entry is published (failure injection).</summary>
    internal Action? BeforePublish { get; set; }

    private async Task FinishAsync(ServerRecord record)
    {
        var pending = record.PendingContent!;
        var paths = _registry.PathsFor(record.Definition.Id);
        var files = new ContentFiles(paths, pending.Directory);
        await ContentLockStore.SaveAsync(paths, pending.LockAfter); // idempotent

        if (pending.UndoOf is { } undone)
        {
            // The undone entry's preimages were moved out; the entry itself goes. If this fails, the
            // entry no longer matches the state, so it is not offered, and retention removes it later.
            TryMaintenance(() => ContentHistoryStore.Delete(paths, undone), "remove an undone history entry");
        }
        else
        {
            BeforePublish?.Invoke();
            if (pending.StateAfter is not null)
            {
                await ContentHistoryStore.PublishAsync(paths, files, pending, DateTimeOffset.UtcNow); // resumable; part of the commit
                TryMaintenance(() => ContentHistoryStore.Prune(paths, pending.TxId), "prune add-on history");
            }
            else
            {
                _logger.LogWarning("Committed add-on change {TxId} has no recorded state; it is not added to the history", pending.TxId);
            }
        }

        await CleanUpAsync(record, files);
    }

    /// <summary>Retention and cleanup must never block completing a committed change.</summary>
    private void TryMaintenance(Action action, string what)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not {What}; will retry later", what);
        }
    }

    private async Task CleanUpAsync(ServerRecord record, ContentFiles files)
    {
        var pending = record.PendingContent!;
        SafeFiles.DeleteTree(files.TxDir(pending.TxId), files.ServerDir); // a missing directory is fine
        if (record.ContainerId is not null)
        {
            using var timeout = new CancellationTokenSource(DockerTimeout);
            await IgnoreMissing(() => _runtime.SetRestartPolicyAsync(record, pending.OriginalPolicy, timeout.Token));
        }

        await _registry.SaveAsync(record with { PendingContent = null }, CancellationToken.None);
    }

    private async Task DisableRestartAndRequireStoppedAsync(ServerRecord record, bool stopIfRunning)
    {
        if (record.ContainerId is null)
        {
            return;
        }

        using (var timeout = new CancellationTokenSource(DockerTimeout))
        {
            await IgnoreMissing(() => _runtime.SetRestartPolicyAsync(record, RestartPolicyKind.No, timeout.Token));
        }

        ServerContainerState? state;
        using (var timeout = new CancellationTokenSource(DockerTimeout))
        {
            state = (await _runtime.ListAsync(timeout.Token)).FirstOrDefault(s => s.ContainerId == record.ContainerId);
        }

        if (state is not null && !IsStopped(state))
        {
            if (!stopIfRunning)
            {
                throw new ServerOperationException(ServerOperationError.StillRunning, record.Definition.Id.ToString("N"));
            }

            _logger.LogWarning("Container of {ServerId} was running during add-on recovery; stopping it", record.Definition.Id);
            using var timeout = new CancellationTokenSource(StopTimeout);
            await _runtime.StopAsync(record, timeout.Token);
            using var check = new CancellationTokenSource(DockerTimeout);
            if ((await _runtime.ListAsync(check.Token)).FirstOrDefault(s => s.ContainerId == record.ContainerId) is { } after && !IsStopped(after))
            {
                throw new ServerOperationException(ServerOperationError.StillRunning, record.Definition.Id.ToString("N"));
            }
        }
    }

    /// <summary>Paused or restarting count as running: only these states mean nothing reads the files.</summary>
    public static bool IsStopped(ServerContainerState state) => state.State is "exited" or "created" or "dead";

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
