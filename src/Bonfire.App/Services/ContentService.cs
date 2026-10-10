using Bonfire.Core.Content;
using Bonfire.Core.Servers;
using Bonfire.Infrastructure.Content;
using Bonfire.Infrastructure.Linux;
using Bonfire.Infrastructure.Servers;
using Microsoft.Extensions.Logging;

namespace Bonfire.App.Services;

public enum ContentError
{
    /// <summary>The server type has no add-ons (Vanilla).</summary>
    NotSupported,

    /// <summary>Files or settings changed since the plan was made; plan again.</summary>
    PlanStale,

    /// <summary>The plan has unresolved problems.</summary>
    PlanBlocked,

    NotFound,
    NameCollision,

    /// <summary>Other active add-ons require this one.</summary>
    HasDependents,

    /// <summary>Required add-ons are not active.</summary>
    MissingDependencies,

    Incompatible,

    /// <summary>A local file is not a mod/plugin for this server type.</summary>
    WrongKind,

    AlreadyInstalled,

    /// <summary>Pinned add-ons are never updated.</summary>
    Pinned,

    /// <summary>Only add-ons with a known identity can be pinned or updated.</summary>
    Unidentified,

    /// <summary>The add-ons changed since that change (files, pins or server versions): it cannot be undone safely.</summary>
    UndoUnavailable,
}

public enum UndoBlocker
{
    /// <summary>Files, disabled files, the lock (incl. pins) or the server's versions changed since.</summary>
    StateChanged,

    /// <summary>An interrupted operation must be repaired first.</summary>
    Pending,
}

/// <summary>History newest first; <see cref="NewestBlocker"/> is null when the newest entry can be undone.</summary>
public sealed record ContentHistoryView(IReadOnlyList<HistoryEntry> Entries, UndoBlocker? NewestBlocker)
{
    public bool CanUndoNewest => Entries.Count > 0 && NewestBlocker is null;
}

public sealed class ContentException(ContentError error, params string[] subjects)
    : Exception($"{error}: {string.Join(", ", subjects)}")
{
    public ContentError Error { get; } = error;

    public IReadOnlyList<string> Subjects { get; } = subjects;
}

public enum LocalWarning
{
    /// <summary>Not found on Modrinth: compatibility and dependencies are unverified.</summary>
    Unverified,

    /// <summary>Modrinth could not be asked: compatibility and dependencies are unverified.</summary>
    NotChecked,

    /// <summary>Identified, but some required add-ons are not active.</summary>
    MissingDependencies,
}

/// <summary>A local JAR copied into staging and inspected, waiting for the user's confirmation.</summary>
public sealed record LocalCandidate(
    Guid TxId,
    string InstallName,
    LocalJarInfo Info,
    ModrinthVersion? Identified,
    IReadOnlyList<LocalWarning> Warnings,
    IReadOnlyList<string> MissingDependencies,
    string Fingerprint);

/// <summary>
/// Add-on operations for one server: inventory, planning, install, remove, disable/enable and
/// local files. Every change runs exclusively for the server, only while it is stopped, and is
/// re-validated against the files on disk right before it is applied.
/// </summary>
public sealed class ContentService
{
    private readonly ServerRegistry _registry;
    private readonly ServerOperationsService _operations;
    private readonly IServerRuntime _runtime;
    private readonly ContentEngine _engine;
    private readonly IModrinth _modrinth;
    private readonly Func<ContentDownloader> _downloader;
    private readonly TimeProvider _time;
    private readonly ILogger<ContentService> _logger;

    public ContentService(
        ServerRegistry registry, ServerOperationsService operations, IServerRuntime runtime, ContentEngine engine,
        IModrinth modrinth, Func<ContentDownloader> downloader, TimeProvider time, ILogger<ContentService> logger)
    {
        _registry = registry;
        _operations = operations;
        _runtime = runtime;
        _engine = engine;
        _modrinth = modrinth;
        _downloader = downloader;
        _time = time;
        _logger = logger;
    }

    public IModrinth Modrinth => _modrinth;

    public ContentProfile? ProfileFor(Guid serverId) =>
        _registry.Load(serverId)?.Record is { } record ? ContentProfile.For(record.Definition) : null;

    public Task<ContentInventory> InventoryAsync(Guid serverId, CancellationToken cancellationToken)
    {
        var record = LoadRecord(serverId);
        RequireProfile(record);
        // Hashing every JAR is real work: never on the UI thread.
        return Task.Run(() => ContentInventoryBuilder.BuildAsync(record.Definition, _registry.PathsFor(serverId), _modrinth, cancellationToken), cancellationToken);
    }

    public async Task<InstallPlan> PlanInstallAsync(Guid serverId, string projectId, string? versionId, CancellationToken cancellationToken)
    {
        var inventory = await InventoryAsync(serverId, cancellationToken);
        return await new ContentResolver(_modrinth, inventory).PlanAsync(projectId, versionId, cancellationToken);
    }

    /// <summary>Checks the active known add-ons for newer compatible versions (network; no change).</summary>
    public async Task<IReadOnlyList<UpdateInfo>> CheckUpdatesAsync(Guid serverId, bool includePrereleases, CancellationToken cancellationToken)
    {
        var inventory = await InventoryAsync(serverId, cancellationToken);
        return await ContentUpdates.CheckAsync(_modrinth, inventory, includePrereleases, cancellationToken);
    }

    /// <summary>
    /// Plans updating the given add-ons to the offered versions. The old files are replaced in the
    /// same transaction; new required dependencies are added; files that stay keep their requirements.
    /// </summary>
    public async Task<InstallPlan> PlanUpdateAsync(Guid serverId, IReadOnlyList<UpdateInfo> updates, CancellationToken cancellationToken)
    {
        var inventory = await InventoryAsync(serverId, cancellationToken);
        var replacing = new List<InstalledItem>();
        var roots = new List<(string Project, string? Version)>();
        foreach (var update in updates)
        {
            if (update.Latest is null)
            {
                continue;
            }

            // The same file must still be there; otherwise the check is outdated.
            var item = inventory.Find(update.Item.FileName, disabled: false);
            if (item is null || item.Sha512 != update.Item.Sha512 || item.ProjectId != update.Latest.ProjectId)
            {
                throw new ContentException(ContentError.PlanStale);
            }

            if (item.State == ItemState.Tracked && item.Entry is { Pinned: true })
            {
                throw new ContentException(ContentError.Pinned, item.Title);
            }

            replacing.Add(item);
            roots.Add((item.ProjectId!, update.Latest.Id));
        }

        if (roots.Count == 0)
        {
            throw new ContentException(ContentError.PlanBlocked);
        }

        return await new ContentResolver(_modrinth, inventory, replacing).PlanAsync(roots, cancellationToken);
    }

    /// <summary>
    /// Pins or unpins an add-on (metadata only: no file moves, so no journal; allowed while running).
    /// A hash-identified file becomes a detected lock entry.
    /// </summary>
    public Task SetPinnedAsync(Guid serverId, string fileName, bool disabled, bool pinned, CancellationToken cancellationToken) =>
        ExclusiveResult(serverId, async (_, paths, inventory, _) =>
        {
            var item = inventory.Find(fileName, disabled) ?? throw new ContentException(ContentError.NotFound, fileName);
            if (!item.IsKnown)
            {
                throw new ContentException(ContentError.Unidentified, item.Title);
            }

            var now = _time.GetUtcNow();
            var entries = inventory.CurrentEntries(now)
                .Select(e => e.FileName == fileName && e.Disabled == disabled ? e with { Pinned = pinned } : e)
                .ToList();
            cancellationToken.ThrowIfCancellationRequested();
            await ContentLockStore.SaveAsync(paths, new ContentLock(ContentLock.CurrentSchemaVersion, entries));
            return true;
        }, requireStopped: false);

    /// <summary>Downloads and installs an approved plan. Fails as PlanStale if anything changed since planning.</summary>
    public Task InstallAsync(Guid serverId, InstallPlan plan, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken) =>
        Exclusive(serverId, async (record, paths, inventory, files) =>
        {
            if (!plan.CanInstall)
            {
                throw new ContentException(ContentError.PlanBlocked);
            }

            if (inventory.Fingerprint != plan.Fingerprint)
            {
                throw new ContentException(ContentError.PlanStale);
            }

            var txId = Guid.NewGuid();
            files.EnsureLayout(txId);
            try
            {
                using var downloader = _downloader();
                long done = 0;
                foreach (var item in plan.Items)
                {
                    var before = done;
                    // Synchronous forwarding keeps reports in order (the caller's IProgress marshals to the UI).
                    var itemProgress = progress is null ? null : new Forward(p => progress.Report(new DownloadProgress(before + p.BytesDone, plan.TotalBytes)));
                    await downloader.DownloadAsync(item.File, files.PathFor(ContentLocation.Staging, txId, item.InstallName), itemProgress, cancellationToken);
                    done += item.File.Size;
                }

                SafeFiles.FsyncDirectory(files.DirFor(ContentLocation.Staging, txId));
            }
            catch
            {
                SafeFiles.DeleteTree(files.TxDir(txId), paths.ServerDir); // no journal yet: nothing else to undo
                throw;
            }

            var now = _time.GetUtcNow();
            var replaced = plan.ReplacedItems.Select(i => i.FileName).ToHashSet(StringComparer.Ordinal);
            var entries = inventory.CurrentEntries(now).Where(e => e.Disabled || !replaced.Contains(e.FileName)).ToList();
            entries.AddRange(plan.Items.Select(i =>
                inventory.EntryFor(i.Version, i.InstallName, i.File.Sha512, i.File.Size, ContentOrigin.Installed, false, i.ProjectTitle, now)));
            // Old versions leave first; the new files never reuse their names (the plan chose distinct names).
            var moves = plan.ReplacedItems.Select(i => new ContentMove(ContentLocation.Live, ContentLocation.Trash, i.FileName, i.Sha512))
                .Concat(plan.Items.Select(i => new ContentMove(ContentLocation.Staging, ContentLocation.Live, i.InstallName, i.File.Sha512)))
                .ToList();
            await _engine.CommitAsync(record, files, txId, moves, new ContentLock(ContentLock.CurrentSchemaVersion, entries),
                plan.ReplacedItems.Count > 0 ? ContentChangeKind.Update : ContentChangeKind.Install,
                plan.Items.Select(i => $"{i.ProjectTitle} {i.Version.VersionNumber}").ToList(), cancellationToken);
        });

    /// <summary>Removes an add-on file (live or disabled). Refused while known active add-ons require it.</summary>
    public Task RemoveAsync(Guid serverId, string fileName, bool disabled, CancellationToken cancellationToken) =>
        Exclusive(serverId, async (record, _, inventory, files) =>
        {
            var item = inventory.Find(fileName, disabled) ?? throw new ContentException(ContentError.NotFound, fileName);
            if (!disabled)
            {
                RequireNoDependents(inventory, item);
            }

            var txId = Guid.NewGuid();
            var from = disabled ? ContentLocation.Disabled : ContentLocation.Live;
            var entries = inventory.CurrentEntries(_time.GetUtcNow()).Where(e => !(e.FileName == fileName && e.Disabled == disabled)).ToList();
            await _engine.CommitAsync(record, files, txId, [new ContentMove(from, ContentLocation.Trash, fileName, item.Sha512)],
                new ContentLock(ContentLock.CurrentSchemaVersion, entries), ContentChangeKind.Remove, [item.Title], cancellationToken);
        });

    /// <summary>
    /// Disabling moves the file out of the container mount, so the server can neither load nor touch
    /// it. Enabling checks dependencies and incompatibilities first.
    /// </summary>
    public Task SetEnabledAsync(Guid serverId, string fileName, bool enable, CancellationToken cancellationToken) =>
        Exclusive(serverId, async (record, _, inventory, files) =>
        {
            var item = inventory.Find(fileName, disabled: enable) ?? throw new ContentException(ContentError.NotFound, fileName);
            if (inventory.Find(fileName, disabled: !enable) is not null)
            {
                throw new ContentException(ContentError.NameCollision, fileName);
            }

            if (enable)
            {
                RequireEnableable(inventory, item);
            }
            else
            {
                RequireNoDependents(inventory, item);
            }

            var move = enable
                ? new ContentMove(ContentLocation.Disabled, ContentLocation.Live, fileName, item.Sha512)
                : new ContentMove(ContentLocation.Live, ContentLocation.Disabled, fileName, item.Sha512);
            var entries = inventory.CurrentEntries(_time.GetUtcNow())
                .Select(e => e.FileName == fileName && e.Disabled == enable ? e with { Disabled = !enable } : e)
                .ToList();
            await _engine.CommitAsync(record, files, Guid.NewGuid(), [move], new ContentLock(ContentLock.CurrentSchemaVersion, entries),
                enable ? ContentChangeKind.Enable : ContentChangeKind.Disable, [item.Title], cancellationToken);
        });

    /// <summary>
    /// Copies a local JAR into private staging, inspects that copy and identifies it by hash. Nothing
    /// is installed until <see cref="InstallLocalAsync"/>; <see cref="DiscardLocal"/> drops the copy.
    /// </summary>
    public Task<LocalCandidate> PrepareLocalAsync(Guid serverId, string sourcePath, CancellationToken cancellationToken) =>
        ExclusiveResult(serverId, async (_, paths, inventory, files) =>
        {
            var txId = Guid.NewGuid();
            files.EnsureLayout(txId);
            try
            {
                var stagingName = $"local-{txId:N}.jar";
                var staged = files.PathFor(ContentLocation.Staging, txId, stagingName);
                var info = await LocalJar.StageAsync(sourcePath, staged, cancellationToken);
                var expected = inventory.Profile.ServerType == ServerType.Fabric ? JarKind.FabricMod : JarKind.BukkitPlugin;
                if (info.Kind != expected)
                {
                    throw new ContentException(ContentError.WrongKind, Path.GetFileName(sourcePath));
                }

                var warnings = new List<LocalWarning>();
                var missing = new List<string>();
                ModrinthVersion? identified = null;
                try
                {
                    identified = (await _modrinth.GetVersionsByHashAsync([info.Sha512], cancellationToken)).GetValueOrDefault(info.Sha512);
                    if (identified is null)
                    {
                        warnings.Add(LocalWarning.Unverified);
                    }
                }
                catch (Bonfire.Core.Metadata.MetadataException)
                {
                    warnings.Add(LocalWarning.NotChecked);
                }

                if (identified is not null)
                {
                    // A known artifact follows the same rules as a download: no bypass for a wrong game version or loader.
                    if (!new ContentResolver(_modrinth, inventory).IsCompatible(identified))
                    {
                        throw new ContentException(ContentError.Incompatible, identified.Name);
                    }

                    if (inventory.Items.FirstOrDefault(i => i.ProjectId == identified.ProjectId) is { } same)
                    {
                        throw new ContentException(ContentError.AlreadyInstalled, same.Title);
                    }

                    var conflicts = Incompatibilities(inventory, identified.ProjectId, identified.Id, identified.Dependencies.Select(d => new LockDependency(d.ProjectId, d.VersionId, d.Type)).ToList());
                    if (conflicts.Count > 0)
                    {
                        throw new ContentException(ContentError.Incompatible, [.. conflicts]);
                    }

                    missing.AddRange(MissingRequired(inventory, identified.Dependencies.Select(d => new LockDependency(d.ProjectId, d.VersionId, d.Type))));
                    if (missing.Count > 0)
                    {
                        warnings.Add(LocalWarning.MissingDependencies);
                    }
                }

                var original = Path.GetFileName(sourcePath);
                var name = ContentRules.IsSafeJarName(original) && !inventory.NameTaken(original) ? original : $"local-{info.Sha512[..16]}.jar";
                if (inventory.NameTaken(name))
                {
                    throw new ContentException(ContentError.NameCollision, name);
                }

                if (name != stagingName)
                {
                    SafeFiles.MoveNoReplace(staged, files.PathFor(ContentLocation.Staging, txId, name));
                }

                SafeFiles.FsyncDirectory(files.DirFor(ContentLocation.Staging, txId));

                return new LocalCandidate(txId, name, info, identified, warnings, missing, inventory.Fingerprint);
            }
            catch
            {
                SafeFiles.DeleteTree(files.TxDir(txId), paths.ServerDir);
                throw;
            }
        });

    public Task InstallLocalAsync(Guid serverId, LocalCandidate candidate, CancellationToken cancellationToken) =>
        Exclusive(serverId, async (record, _, inventory, files) =>
        {
            if (inventory.Fingerprint != candidate.Fingerprint)
            {
                throw new ContentException(ContentError.PlanStale);
            }

            var now = _time.GetUtcNow();
            var entries = inventory.CurrentEntries(now);
            entries.Add(candidate.Identified is { } v
                ? inventory.EntryFor(v, candidate.InstallName, candidate.Info.Sha512, candidate.Info.Size, ContentOrigin.Installed, false, null, now)
                : new LockEntry(ContentProvider.Local, null, null, candidate.Info.Name ?? candidate.InstallName, candidate.Info.Version,
                    candidate.InstallName, candidate.Info.Sha512, candidate.Info.Size, inventory.Profile.GameVersion, inventory.Profile.Loaders[0],
                    [], [], now, false, ContentOrigin.Installed, false));
            await _engine.CommitAsync(record, files, candidate.TxId,
                [new ContentMove(ContentLocation.Staging, ContentLocation.Live, candidate.InstallName, candidate.Info.Sha512)],
                new ContentLock(ContentLock.CurrentSchemaVersion, entries), ContentChangeKind.LocalInstall,
                [candidate.Identified?.Name ?? candidate.Info.Name ?? candidate.InstallName], cancellationToken);
        }, keepStaging: candidate.TxId);

    /// <summary>
    /// The add-on history, newest first, with whether the newest entry can be undone now (and why not).
    /// Older entries become undoable only after the newer ones are undone.
    /// </summary>
    /// <summary><see cref="History"/> off the UI thread (it hashes every add-on file).</summary>
    public Task<ContentHistoryView> HistoryAsync(Guid serverId) => Task.Run(() => History(serverId));

    public ContentHistoryView History(Guid serverId)
    {
        var record = LoadRecord(serverId);
        var profile = RequireProfile(record);
        var paths = _registry.PathsFor(serverId);
        var entries = ContentHistoryStore.List(paths).Where(e => e.Directory == profile.Directory).ToList();
        return new ContentHistoryView(entries, entries.Count == 0 ? null : UndoBlocker(record, paths, profile, entries[0]));
    }

    private static UndoBlocker? UndoBlocker(ServerRecord record, ServerPaths paths, ContentProfile profile, HistoryEntry newest)
    {
        if (record.HasPendingOperation)
        {
            return Services.UndoBlocker.Pending;
        }

        try
        {
            return ContentHistoryStore.StateHash(record.Definition, paths, profile.Directory) == newest.StateAfter
                ? null
                : Services.UndoBlocker.StateChanged;
        }
        catch (IOException)
        {
            return Services.UndoBlocker.StateChanged;
        }
    }

    /// <summary>
    /// Reverts the newest history entry as a normal journaled transaction. Only when the complete
    /// add-on state is exactly what that change left (files, disabled files, lock incl. pins, server
    /// versions). Worlds and configuration are not reverted.
    /// </summary>
    public Task UndoAsync(Guid serverId, Guid txId, CancellationToken cancellationToken) =>
        Exclusive(serverId, async (record, paths, _, files) =>
        {
            var newest = ContentHistoryStore.List(paths).FirstOrDefault(e => e.Directory == files.Directory);
            if (newest is null || newest.TxId != txId)
            {
                throw new ContentException(ContentError.PlanStale); // only the newest change can be undone
            }

            if (ContentHistoryStore.StateHash(record.Definition, paths, files.Directory) != newest.StateAfter)
            {
                throw new ContentException(ContentError.UndoUnavailable, string.Join(", ", newest.Summary));
            }

            await _engine.CommitAsync(record, files, Guid.NewGuid(), newest.InverseMoves(), newest.LockBefore,
                ContentChangeKind.Undo, newest.Summary, cancellationToken, undoOf: newest.TxId);
        });

    /// <summary>Drops a prepared local file (under the server's gate, so it cannot race its installation).</summary>
    public Task DiscardLocalAsync(Guid serverId, LocalCandidate candidate) =>
        _operations.RunExclusiveAsync(serverId, () =>
        {
            var paths = _registry.PathsFor(serverId);
            if (LoadRecord(serverId).PendingContent?.TxId != candidate.TxId)
            {
                SafeFiles.DeleteTree(Path.Combine(paths.ServerDir, PendingContent.TxDirPrefix + candidate.TxId.ToString("N")), paths.ServerDir);
            }

            return Task.FromResult(true);
        });

    private sealed class Forward(Action<DownloadProgress> report) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) => report(value);
    }

    // ------------------------------------------------------------------ checks

    private static void RequireNoDependents(ContentInventory inventory, InstalledItem item)
    {
        if (!item.IsKnown)
        {
            return; // unknown identity: no known dependents (the UI says so)
        }

        var dependents = inventory.Active
            .Where(i => i != item && i.Dependencies.Any(d => d.Type == DependencyType.Required && d.Concerns(item.ProjectId, item.VersionId)))
            .Select(i => i.Title)
            .ToArray();
        if (dependents.Length > 0)
        {
            throw new ContentException(ContentError.HasDependents, dependents);
        }
    }

    private static void RequireEnableable(ContentInventory inventory, InstalledItem item)
    {
        var conflicts = Incompatibilities(inventory, item.ProjectId, item.VersionId, item.Dependencies);
        if (conflicts.Count > 0)
        {
            throw new ContentException(ContentError.Incompatible, [.. conflicts]);
        }

        if (item.ProjectId is not null && inventory.Active.Any(i => i.ProjectId == item.ProjectId))
        {
            throw new ContentException(ContentError.AlreadyInstalled, item.Title);
        }

        var missing = MissingRequired(inventory, item.Dependencies);
        if (missing.Count > 0)
        {
            throw new ContentException(ContentError.MissingDependencies, [.. missing]);
        }
    }

    /// <summary>Incompatibility in both directions between a candidate and the active known add-ons.</summary>
    private static List<string> Incompatibilities(ContentInventory inventory, string? projectId, string? versionId, IReadOnlyList<LockDependency> deps)
    {
        var result = new List<string>();
        foreach (var active in inventory.Active.Where(i => i.IsKnown))
        {
            if (deps.Any(d => d.Type == DependencyType.Incompatible && d.Names(active.ProjectId, active.VersionId))
                || ((projectId is not null || versionId is not null)
                    && active.Dependencies.Any(d => d.Type == DependencyType.Incompatible && d.Names(projectId, versionId))))
            {
                result.Add(active.Title);
            }
        }

        return result;
    }

    /// <summary>Required dependencies (with exact versions where given) that no active known file satisfies.</summary>
    private static List<string> MissingRequired(ContentInventory inventory, IEnumerable<LockDependency> deps) =>
        deps.Where(d => d.Type == DependencyType.Required)
            .Where(d => !inventory.Active.Any(i => i.IsKnown && d.Names(i.ProjectId, i.VersionId)))
            .Select(d => d.ProjectId ?? d.VersionId ?? "?")
            .ToList();

    // ------------------------------------------------------------------ plumbing

    private Task Exclusive(Guid serverId, Func<ServerRecord, ServerPaths, ContentInventory, ContentFiles, Task> action, Guid? keepStaging = null) =>
        ExclusiveResult<bool>(serverId, async (r, p, i, f) =>
        {
            await action(r, p, i, f);
            return true;
        }, keepStaging);

    private Task<T> ExclusiveResult<T>(
        Guid serverId, Func<ServerRecord, ServerPaths, ContentInventory, ContentFiles, Task<T>> action, Guid? keepStaging = null,
        bool requireStopped = true) =>
        // The whole operation (hashing, renames, fsync, journal writes) runs on the thread pool, so the
        // UI stays responsive; callers continue on their own context afterwards.
        _operations.RunExclusiveAsync(serverId, () => Task.Run(async () =>
        {
            _operations.RequireConnected();
            var record = LoadRecord(serverId);
            var profile = RequireProfile(record);
            if (record.HasPendingOperation)
            {
                throw new ServerOperationException(ServerOperationError.SettingsChangePending, serverId.ToString("N"));
            }

            if (requireStopped && record.ContainerId is not null
                && (await _runtime.ListAsync(CancellationToken.None)).FirstOrDefault(s => s.ContainerId == record.ContainerId) is { } state
                && !ContentEngine.IsStopped(state))
            {
                throw new ServerOperationException(ServerOperationError.StillRunning, serverId.ToString("N"));
            }

            var paths = _registry.PathsFor(serverId);
            var files = new ContentFiles(paths, profile.Directory);
            files.EnsureLayout();
            if (requireStopped)
            {
                _engine.CleanStaleStaging(paths, keepStaging); // never under a running change of metadata only
            }
            var inventory = await ContentInventoryBuilder.BuildAsync(record.Definition, paths, _modrinth, CancellationToken.None);
            return await action(record, paths, inventory, files);
        }));

    private ServerRecord LoadRecord(Guid serverId) =>
        _registry.Load(serverId)?.Record
        ?? throw new ServerOperationException(ServerOperationError.RecordUnavailable, serverId.ToString("N"));

    private static ContentProfile RequireProfile(ServerRecord record) =>
        ContentProfile.For(record.Definition) ?? throw new ContentException(ContentError.NotSupported);
}
