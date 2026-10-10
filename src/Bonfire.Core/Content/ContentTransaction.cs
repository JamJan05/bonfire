using Bonfire.Core.Servers;

namespace Bonfire.Core.Content;

/// <summary>Where an add-on file can be. Stored as enums — paths are always derived, never persisted.</summary>
public enum ContentLocation
{
    /// <summary>Verified downloads or local copies of the transaction (servers/&lt;id&gt;/content-tx-&lt;tx&gt;/in).</summary>
    Staging,

    /// <summary>Loaded by the server (data/mods or data/plugins).</summary>
    Live,

    /// <summary>Disabled: outside the container mount (servers/&lt;id&gt;/disabled-content/&lt;dir&gt;).</summary>
    Disabled,

    /// <summary>Removed by the transaction; kept as the history entry's preimages once committed (content-tx-&lt;tx&gt;/out).</summary>
    Trash,

    /// <summary>Preimages of the history entry being undone (content-history/&lt;entry&gt;/files); only in undo transactions.</summary>
    Archive,
}

public enum ContentChangeKind
{
    Install,
    Update,
    Remove,
    Disable,
    Enable,
    LocalInstall,

    /// <summary>Reverts the newest history entry; never recorded itself (no redo).</summary>
    Undo,
}

/// <summary>One rename inside the server directory. The moved file is its own preimage.</summary>
public sealed record ContentMove(ContentLocation From, ContentLocation To, string FileName, string Sha512);

public enum ContentTxStep
{
    /// <summary>Intent recorded; files may be partly moved. Recovery moves them back.</summary>
    Prepared,

    /// <summary>All moves are durable; recovery finishes (lock, cleanup, policy).</summary>
    Committed,

    /// <summary>All moves were undone and that is durable; only cleanup (staging, policy) remains.</summary>
    RolledBack,
}

/// <summary>
/// An add-on transaction in progress (in server.json, like the other pending operations, so the
/// start gate and Repair cover it).
/// </summary>
public sealed record PendingContent(
    Guid TxId,
    string Directory,
    IReadOnlyList<ContentMove> Moves,
    ContentLock LockAfter,
    RestartPolicyKind OriginalPolicy,
    ContentTxStep Step,
    ContentLock? LockBefore = null,
    ContentChangeKind Kind = ContentChangeKind.Install,
    IReadOnlyList<string>? Summary = null,
    Guid? UndoOf = null,
    long Sequence = 0,
    string? StateAfter = null)
{
    public const int MaxMoves = 200;
    public const int MaxSummary = 200;

    [System.Text.Json.Serialization.JsonIgnore]
    public string TxDirName => TxDirPrefix + TxId.ToString("N");

    public const string TxDirPrefix = "content-tx-";

    /// <summary>Structural check of a journal read from disk; a malformed journal is never acted on.</summary>
    public bool IsWellFormed()
    {
        if (TxId == Guid.Empty || Directory is not ("mods" or "plugins") || Moves is not { Count: > 0 and <= MaxMoves }
            || LockAfter is null || !LockAfter.IsWellFormed() || !Enum.IsDefined(OriginalPolicy) || !Enum.IsDefined(Step)
            || (LockBefore is not null && !LockBefore.IsWellFormed()) || !Enum.IsDefined(Kind) || Sequence < 0
            || (Summary is not null && (Summary.Count > MaxSummary || Summary.Any(t => t is null || t.Length > 200)))
            || (Kind == ContentChangeKind.Undo) != (UndoOf is { } u && u != Guid.Empty)
            || (StateAfter is not null && (StateAfter.Length != 64 || !StateAfter.All(Uri.IsHexDigit)))
            || (UndoOf is null && Moves.Any(m => m is null || m.From == ContentLocation.Archive || m.To == ContentLocation.Archive)))
        {
            return false;
        }

        // Each location is used at most once: rollback judges every move on its own.
        var used = new HashSet<(ContentLocation, string)>();
        return Moves.All(m => m is not null && m.From != m.To && Enum.IsDefined(m.From) && Enum.IsDefined(m.To)
                              && ContentRules.IsSafeJarName(m.FileName) && ContentRules.IsSha512(m.Sha512)
                              && used.Add((m.From, m.FileName)) && used.Add((m.To, m.FileName)));
    }
}
