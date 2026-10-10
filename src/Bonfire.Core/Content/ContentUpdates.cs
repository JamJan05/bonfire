using Bonfire.Core.Metadata;

namespace Bonfire.Core.Content;

public enum UpdateStatus
{
    UpToDate,
    Available,

    /// <summary>Pinned by the user: never updated, not even as a dependency.</summary>
    Pinned,

    /// <summary>The installed version is no longer offered for this server: nothing is offered (it could be a downgrade).</summary>
    CurrentUnavailable,

    /// <summary>Modrinth could not be asked for this add-on: "unknown", not "up to date".</summary>
    CheckFailed,
}

public sealed record UpdateInfo(InstalledItem Item, UpdateStatus Status, ModrinthVersion? Latest);

public static class ContentUpdates
{
    /// <summary>
    /// Checks every active add-on with a known identity. Only versions published after the installed
    /// one are offered, releases only unless <paramref name="includePrereleases"/> is set.
    /// </summary>
    public static async Task<IReadOnlyList<UpdateInfo>> CheckAsync(
        IModrinth api, ContentInventory inventory, bool includePrereleases, CancellationToken cancellationToken)
    {
        var result = new List<UpdateInfo>();
        foreach (var item in inventory.Active.Where(i => i.IsKnown))
        {
            if (item.Entry is { Pinned: true } && item.State == ItemState.Tracked)
            {
                result.Add(new UpdateInfo(item, UpdateStatus.Pinned, null));
                continue;
            }

            IReadOnlyList<ModrinthVersion> versions;
            try
            {
                versions = await api.GetCompatibleVersionsAsync(item.ProjectId!, inventory.Profile, cancellationToken);
            }
            catch (MetadataException)
            {
                result.Add(new UpdateInfo(item, UpdateStatus.CheckFailed, null));
                continue;
            }

            var current = versions.FirstOrDefault(v => v.Id == item.VersionId);
            if (current is null)
            {
                result.Add(new UpdateInfo(item, UpdateStatus.CurrentUnavailable, null));
                continue;
            }

            var latest = versions
                .Where(v => v.Published > current.Published && (includePrereleases || v.IsRelease) && ContentRules.SelectFile(v) is not null)
                .OrderByDescending(v => v.Published)
                .FirstOrDefault();
            result.Add(new UpdateInfo(item, latest is null ? UpdateStatus.UpToDate : UpdateStatus.Available, latest));
        }

        return result;
    }
}
