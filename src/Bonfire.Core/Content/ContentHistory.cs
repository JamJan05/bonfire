namespace Bonfire.Core.Content;

/// <summary>
/// One committed add-on change that can be undone: its moves, the lock before it, and a hash of the
/// complete state right after it. Stored as content-history/&lt;txId&gt;/entry.json next to the
/// removed files (files/), outside the container mount.
/// </summary>
public sealed record HistoryEntry(
    Guid TxId,
    long Sequence,
    DateTimeOffset Time,
    ContentChangeKind Kind,
    IReadOnlyList<string> Summary,
    string Directory,
    IReadOnlyList<ContentMove> Moves,
    ContentLock LockBefore,
    string StateAfter)
{
    public bool IsWellFormed()
    {
        if (TxId == Guid.Empty || Sequence <= 0 || Kind == ContentChangeKind.Undo || !Enum.IsDefined(Kind)
            || Directory is not ("mods" or "plugins") || Summary is null || Summary.Count > PendingContent.MaxSummary
            || Summary.Any(t => t is null || t.Length > 200) || LockBefore is null || !LockBefore.IsWellFormed()
            || StateAfter is not { Length: 64 } || !StateAfter.All(Uri.IsHexDigit)
            || Moves is not { Count: > 0 and <= PendingContent.MaxMoves })
        {
            return false;
        }

        var used = new HashSet<(ContentLocation, string)>();
        return Moves.All(m => m is not null && m.From != m.To && Enum.IsDefined(m.From) && Enum.IsDefined(m.To)
                              && m.From is not (ContentLocation.Trash or ContentLocation.Archive) && m.To != ContentLocation.Archive
                              && ContentRules.IsSafeJarName(m.FileName) && ContentRules.IsSha512(m.Sha512)
                              && used.Add((m.From, m.FileName)) && used.Add((m.To, m.FileName)));
    }

    /// <summary>
    /// The moves that revert this entry, in reverse order. Removed files come back from the archive;
    /// installed files go to the undo's trash; disable and enable swap.
    /// </summary>
    public List<ContentMove> InverseMoves() => Moves.Reverse().Select(m => (m.From, m.To) switch
    {
        (ContentLocation.Staging, var to) => new ContentMove(to, ContentLocation.Trash, m.FileName, m.Sha512),
        (var from, ContentLocation.Trash) => new ContentMove(ContentLocation.Archive, from, m.FileName, m.Sha512),
        var (from, to) => new ContentMove(to, from, m.FileName, m.Sha512),
    }).ToList();
}
