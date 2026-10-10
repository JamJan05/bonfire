using Bonfire.Core.Backups;
using Bonfire.Core.Servers;
using Bonfire.Infrastructure.Backups;
using Bonfire.Infrastructure.Servers;

namespace Bonfire.App.Services;

/// <summary>
/// Backups and restores, serialized with every other operation on the server. Both need a stopped
/// server (MVP); the UI offers to stop it first.
/// </summary>
public sealed class BackupService
{
    /// <summary>Restore points made before mod changes are kept separately.</summary>
    public const int RestorePointsToKeep = 3;

    private readonly ServerRegistry _registry;
    private readonly IServerRuntime _runtime;
    private readonly ServerOperationsService _operations;
    private readonly BackupStore _store;
    private readonly RestoreEngine _restore;
    private readonly SettingsService _settings;
    private readonly TimeProvider _time;

    public BackupService(
        ServerRegistry registry,
        IServerRuntime runtime,
        ServerOperationsService operations,
        BackupStore store,
        RestoreEngine restore,
        SettingsService settings,
        TimeProvider time)
    {
        _registry = registry;
        _runtime = runtime;
        _operations = operations;
        _store = store;
        _restore = restore;
        _settings = settings;
        _time = time;
    }

    public IReadOnlyList<BackupInfo> List(Guid serverId) => _store.List(serverId);

    public Task<BackupInfo> CreateAsync(Guid serverId, BackupKind kind, IProgress<BackupProgress>? progress, CancellationToken cancellationToken) =>
        _operations.RunExclusiveAsync(serverId, async () =>
        {
            var record = await RequireStoppedAsync(serverId, cancellationToken);
            BackupInfo created;
            try
            {
                created = await Task.Run(
                    () => _store.CreateAsync(record.Definition, _registry.PathsFor(serverId), kind, _time, progress, cancellationToken),
                    cancellationToken);
            }
            catch (Infrastructure.Linux.UnsupportedEntryException ex)
            {
                throw new BackupException(BackupError.UnsupportedEntry, ex.RelativePath, ex);
            }

            _store.ApplyRetention(serverId, kind, kind == BackupKind.Manual ? _settings.Current.BackupsToKeep : RestorePointsToKeep);
            return created;
        });

    public Task RestoreAsync(Guid serverId, Guid backupId, IProgress<BackupProgress>? progress, CancellationToken cancellationToken) =>
        _operations.RunExclusiveAsync(serverId, async () =>
        {
            var record = await RequireStoppedAsync(serverId, cancellationToken);
            var backup = _store.Find(serverId, backupId) ?? throw new BackupException(BackupError.Corrupt, "backup not found");
            await Task.Run(() => _restore.RestoreAsync(record, backup, progress, cancellationToken), CancellationToken.None);
            return true;
        });

    /// <summary>Deletes a backup; the newest usable one is protected so a good copy always remains.</summary>
    public Task DeleteAsync(Guid serverId, Guid backupId) =>
        _operations.RunExclusiveAsync(serverId, () =>
        {
            var all = _store.List(serverId);
            var backup = all.FirstOrDefault(b => b.BackupId == backupId && b.BackupId != Guid.Empty)
                ?? throw new BackupException(BackupError.Corrupt, "backup not found");
            if (backup.IsUsable && all.First(b => b.IsUsable).BackupId == backupId)
            {
                throw new BackupException(BackupError.LastGoodBackup, backupId.ToString("N"));
            }

            _store.Delete(backup);
            return Task.FromResult(true);
        });

    private async Task<ServerRecord> RequireStoppedAsync(Guid serverId, CancellationToken cancellationToken)
    {
        _operations.RequireConnected(); // without Docker we cannot know the server is stopped
        var record = _registry.Load(serverId)?.Record
            ?? throw new ServerOperationException(ServerOperationError.RecordUnavailable, serverId.ToString("N"));
        if (record.HasPendingOperation)
        {
            throw new BackupException(BackupError.RestorePending, serverId.ToString("N"));
        }

        if (record.ContainerId is not null
            && (await _runtime.ListAsync(cancellationToken)).Any(s => s.ContainerId == record.ContainerId && s.IsRunning))
        {
            throw new BackupException(BackupError.ServerRunning, serverId.ToString("N"));
        }

        return record;
    }
}
