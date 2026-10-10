using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bonfire.Core.Content;
using Bonfire.Core.Metadata;
using Bonfire.Core.Servers;

namespace Bonfire.Infrastructure.Content;

/// <summary>Reconciles content-lock.json with the files on disk and identifies unknown files by hash.</summary>
public static class ContentInventoryBuilder
{
    /// <param name="modrinth">Null: no network lookup (identities come from the lock only).</param>
    public static async Task<ContentInventory> BuildAsync(
        ServerDefinition definition, ServerPaths paths, IModrinth? modrinth, CancellationToken cancellationToken)
    {
        var profile = ContentProfile.For(definition) ?? throw new InvalidOperationException("This server type has no add-ons");
        var files = new ContentFiles(paths, profile.Directory);
        var contentLock = ContentLockStore.Load(paths);
        var (onDisk, skipped) = files.Scan();

        var byName = contentLock.Entries
            .GroupBy(e => (e.FileName, e.Disabled))
            .ToDictionary(g => g.Key, g => g.First());

        var items = new List<InstalledItem>();
        foreach (var file in onDisk)
        {
            var entry = byName.GetValueOrDefault((file.FileName, file.Disabled));
            var state = entry is null ? ItemState.Untracked : entry.Sha512 == file.Sha512 ? ItemState.Tracked : ItemState.Modified;
            items.Add(new InstalledItem(file.FileName, file.Disabled, file.Sha512, file.Size, state, entry, null));
        }

        var missing = contentLock.Entries
            .Where(e => !onDisk.Any(f => f.FileName == e.FileName && f.Disabled == e.Disabled))
            .ToList();

        var identificationFailed = false;
        var unknown = items.Where(i => i.State != ItemState.Tracked).Select(i => i.Sha512).Distinct().ToList();
        if (modrinth is not null && unknown.Count > 0)
        {
            try
            {
                var found = await modrinth.GetVersionsByHashAsync(unknown, cancellationToken);
                items = items.Select(i => i.State != ItemState.Tracked && found.TryGetValue(i.Sha512, out var v) ? i with { Identified = v } : i).ToList();
            }
            catch (MetadataException)
            {
                identificationFailed = true; // "could not check" — not the same as "unknown"
            }
        }
        else if (modrinth is null && unknown.Count > 0)
        {
            identificationFailed = true;
        }

        return new ContentInventory(profile, contentLock, items, missing, skipped, identificationFailed,
            Fingerprint(definition, paths, onDisk, skipped));
    }

    /// <summary>
    /// Everything a plan depends on: the server definition (versions, loader), the exact lock file
    /// and every file on disk with its hash. Any change means the plan must be made again.
    /// </summary>
    public static string Fingerprint(ServerDefinition definition, ServerPaths paths, IEnumerable<ContentFile> files, IEnumerable<string> skipped)
    {
        var text = new StringBuilder();
        text.Append(JsonSerializer.Serialize(definition)).Append('\n');
        var lockPath = ContentLockStore.PathFor(paths);
        text.Append(File.Exists(lockPath) ? Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(lockPath))) : "-").Append('\n');
        foreach (var f in files.OrderBy(f => f.Disabled).ThenBy(f => f.FileName, StringComparer.Ordinal))
        {
            text.Append(f.Disabled ? 'D' : 'L').Append(' ').Append(f.FileName).Append(' ').Append(f.Sha512).Append('\n');
        }

        foreach (var s in skipped.Order(StringComparer.Ordinal))
        {
            text.Append("S ").Append(s).Append('\n');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }
}
