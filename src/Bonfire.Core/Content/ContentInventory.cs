namespace Bonfire.Core.Content;

public enum ItemState
{
    /// <summary>The file matches its lock entry.</summary>
    Tracked,

    /// <summary>A lock entry has this name, but the file's hash differs: the lock is not trusted for it.</summary>
    Modified,

    /// <summary>Not in the lock (copied in by hand, or the lock was reset by a restore).</summary>
    Untracked,
}

/// <summary>
/// One add-on file on disk. Its identity comes only from its hash: a tracked lock entry (written
/// from verified metadata when Bonfire installed it) or a Modrinth hash lookup — never the file name.
/// </summary>
public sealed record InstalledItem(
    string FileName,
    bool Disabled,
    string Sha512,
    long Size,
    ItemState State,
    LockEntry? Entry,
    ModrinthVersion? Identified)
{
    private LockEntry? TrustedEntry => State == ItemState.Tracked ? Entry : null;

    public string? ProjectId => TrustedEntry is { Provider: ContentProvider.Modrinth } e ? e.ProjectId : Identified?.ProjectId;

    public string? VersionId => TrustedEntry is { Provider: ContentProvider.Modrinth } e ? e.VersionId : Identified?.Id;

    /// <summary>Known identity (Modrinth project and version). Unverified files have none.</summary>
    public bool IsKnown => ProjectId is not null && VersionId is not null;

    public bool IsLocal => TrustedEntry is { Provider: ContentProvider.Local } && Identified is null;

    public string Title => TrustedEntry?.Title ?? Identified?.Name ?? FileName;

    public string? VersionNumber => TrustedEntry?.VersionNumber ?? Identified?.VersionNumber;

    public IReadOnlyList<LockDependency> Dependencies =>
        TrustedEntry is { Provider: ContentProvider.Modrinth } e
            ? e.Dependencies
            : Identified?.Dependencies.Select(d => new LockDependency(d.ProjectId, d.VersionId, d.Type)).ToList() ?? [];
}

/// <summary>A snapshot of a server's add-ons: lock and disk reconciled, plus a fingerprint for revalidation.</summary>
public sealed record ContentInventory(
    ContentProfile Profile,
    ContentLock Lock,
    IReadOnlyList<InstalledItem> Items,
    IReadOnlyList<LockEntry> Missing,
    IReadOnlyList<string> Skipped,
    bool IdentificationFailed,
    string Fingerprint)
{
    public IEnumerable<InstalledItem> Active => Items.Where(i => !i.Disabled);

    /// <summary>Active files whose identity is unknown: dependency checks cannot account for them.</summary>
    public bool HasUnverifiedActive => Active.Any(i => !i.IsKnown);

    public InstalledItem? Find(string fileName, bool disabled) =>
        Items.FirstOrDefault(i => i.FileName == fileName && i.Disabled == disabled);

    public bool NameTaken(string fileName) =>
        Items.Any(i => string.Equals(i.FileName, fileName, StringComparison.OrdinalIgnoreCase))
        || Skipped.Any(s => string.Equals(s, fileName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The lock after a transaction: what is known about the files that stay, keeping provenance
    /// for tracked entries and recording hash-identified files as detected.
    /// </summary>
    public List<LockEntry> CurrentEntries(DateTimeOffset now)
    {
        var entries = new List<LockEntry>();
        foreach (var item in Items)
        {
            if (item.State == ItemState.Tracked && item.Entry is { } entry)
            {
                entries.Add(entry with { Disabled = item.Disabled });
            }
            else if (item.Identified is { } v)
            {
                entries.Add(EntryFor(v, item.FileName, item.Sha512, item.Size, ContentOrigin.Detected, item.Disabled, null, now));
            }
        }

        return entries;
    }

    public LockEntry EntryFor(ModrinthVersion v, string fileName, string sha512, long size, ContentOrigin origin, bool disabled, string? title, DateTimeOffset now) =>
        new(ContentProvider.Modrinth, v.ProjectId, v.Id, title ?? v.Name, v.VersionNumber, fileName, sha512, size,
            Profile.GameVersion, Profile.Loaders[0], v.Loaders,
            v.Dependencies.Select(d => new LockDependency(d.ProjectId, d.VersionId, d.Type)).ToList(),
            now, false, origin, disabled);
}
