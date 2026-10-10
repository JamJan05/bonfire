namespace Bonfire.Core.Servers;

/// <summary>Where a server is in its create/remove lifecycle; persisted before every Docker change.</summary>
public enum ProvisioningPhase
{
    /// <summary>Creation started; Docker resources may or may not exist yet.</summary>
    Provisioning,

    /// <summary>Network and container exist and were verified (or the container was removed on purpose).</summary>
    Ready,

    /// <summary>Removal of the container started.</summary>
    Removing,
}

public enum SettingsChangeStep
{
    /// <summary>Intent recorded; nothing changed in Docker yet.</summary>
    Prepared,

    /// <summary>The old container's restart policy was set to "no".</summary>
    PolicyDisabled,

    /// <summary>The old container is stopped.</summary>
    Stopped,

    /// <summary>The old container was removed; the replacement is being created.</summary>
    OldRemoved,

    /// <summary>The replacement exists (with restart policy "no"); the intended policy is being restored.</summary>
    ReplacementCreated,
}

/// <summary>
/// A settings change in progress, persisted before every Docker step so an interrupted change is
/// recognised and completed — never started with half-applied settings.
/// </summary>
public sealed record PendingSettingsChange(
    ServerDefinition OldDefinition,
    ServerDefinition NewDefinition,
    bool WasRunning,
    SettingsChangeStep Step);

public enum RestoreStep
{
    /// <summary>Intent recorded; nothing changed.</summary>
    Prepared,

    /// <summary>The container's restart policy is "no".</summary>
    PolicyDisabled,

    /// <summary>The archive was verified and extracted into the staging directory (synced to disk).</summary>
    Extracted,

    /// <summary>The live data directory was renamed to the rollback directory.</summary>
    DataMovedAside,

    /// <summary>The staging directory became the live data directory. From here on recovery only rolls forward.</summary>
    Swapped,

    /// <summary>The previous lock (with .bak) and disabled files were moved into the rollback set.</summary>
    ContentAside,

    /// <summary>The backup's lock and disabled files are in place.</summary>
    ContentApplied,
}

/// <summary>
/// A restore in progress, persisted before every step. Recovery either finishes it (once the
/// verified data is in place) or cancels it, and always leaves the server stopped.
/// </summary>
public sealed record PendingRestore(
    Guid OperationId,
    Guid BackupId,
    string ArchiveSha256,
    string StagingDirName,
    string RollbackDirName,
    RestartPolicyKind OriginalPolicy,
    RestoreStep Step,
    string? ExtrasDirName = null,
    bool ManagesContent = false)
{
    /// <summary>The previous add-on state, next to the previous data (one rollback set).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string ContentAsideDirName => RollbackDirName + ".content";
}

/// <summary>Persisted state of one server (server.json), stored outside the container mount.</summary>
public sealed record ServerRecord(
    int SchemaVersion,
    ServerDefinition Definition,
    ProvisioningPhase Phase,
    string? ContainerId,
    string? NetworkId,
    PendingSettingsChange? PendingChange = null,
    PendingRestore? PendingRestore = null,
    Bonfire.Core.Content.PendingContent? PendingContent = null)
{
    /// <summary>
    /// Format of server.json. 2 added <see cref="PendingChange"/>, 3 <see cref="PendingRestore"/>, 4 <see cref="PendingContent"/> —
    /// which an older Bonfire must not ignore — it refuses newer records. (Independent of the container label version.)
    /// </summary>
    public const int CurrentSchemaVersion = 4;

    /// <summary>An interrupted operation must be finished (Repair) before starting or changing the server.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasPendingOperation => PendingChange is not null || PendingRestore is not null || PendingContent is not null;

    public static ServerRecord New(ServerDefinition definition) =>
        new(CurrentSchemaVersion, definition, ProvisioningPhase.Provisioning, null, null);
}
