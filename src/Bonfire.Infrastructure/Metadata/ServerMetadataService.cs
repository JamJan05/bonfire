using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Bonfire.Core.Metadata;
using Bonfire.Core.Servers;

namespace Bonfire.Infrastructure.Metadata;

/// <summary>
/// Resolves Minecraft releases, Java requirements, Fabric loader/installer and Paper builds from
/// the official APIs. Only stable artefacts are offered; anything unexpected fails closed.
/// </summary>
public sealed class ServerMetadataService : IServerMetadata
{
    private static readonly ProviderPolicy Mojang = new("piston-meta.mojang.com", ["/mc/game/version_manifest_v2.json", "/v1/packages/"]);
    private static readonly ProviderPolicy Fabric = new("meta.fabricmc.net", ["/v2/versions/"]);
    private static readonly ProviderPolicy Paper = new("fill.papermc.io", ["/v3/projects/paper"]);

    private static readonly Uri ManifestUri = new("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json");

    private readonly MetadataHttpClient _http;
    private readonly ConcurrentDictionary<string, Task<object>> _cache = new();

    public ServerMetadataService(MetadataHttpClient http) => _http = http;

    public void Invalidate() => _cache.Clear();

    public async Task<IReadOnlyList<GameVersion>> GetReleasesAsync(CancellationToken cancellationToken) =>
        (await LoadManifestAsync(cancellationToken)).Select(r => r.Version).ToList();

    public async Task<ResolvedServerVersion> ResolveAsync(ServerType type, string minecraftVersion, CancellationToken cancellationToken)
    {
        var release = (await LoadManifestAsync(cancellationToken)).FirstOrDefault(r => r.Version.Id == minecraftVersion)
            ?? throw new MetadataException(MetadataError.Unsupported, $"{minecraftVersion} is not a Minecraft release");
        var mojangJava = await Cached($"java:{minecraftVersion}", ct => LoadJavaAsync(release.Url, ct), cancellationToken);

        return type switch
        {
            ServerType.Vanilla => new ResolvedServerVersion(type, minecraftVersion, RequireJavaImage(mojangJava), null, null, null, true),
            ServerType.Fabric => await ResolveFabricAsync(minecraftVersion, mojangJava, cancellationToken),
            ServerType.Paper => await ResolvePaperAsync(minecraftVersion, mojangJava, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(type)),
        };
    }

    private sealed record Release(GameVersion Version, Uri Url);

    private Task<List<Release>> LoadManifestAsync(CancellationToken cancellationToken) =>
        Cached("manifest", async ct =>
        {
            using var doc = await _http.GetJsonAsync(ManifestUri, Mojang, ct);
            var releases = new List<Release>();
            foreach (var v in Array(doc.RootElement, "versions"))
            {
                if (Str(v, "type") != "release")
                {
                    continue; // no snapshots
                }

                var id = Str(v, "id");
                if (!ServerDefinitionValidator.IsConcreteVersion(id)
                    || !Uri.TryCreate(Str(v, "url"), UriKind.Absolute, out var url)
                    || !Mojang.Allows(url)
                    || !DateTimeOffset.TryParse(Str(v, "releaseTime"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
                {
                    continue; // skip entries we cannot fully trust
                }

                releases.Add(new Release(new GameVersion(id!, time), url));
            }

            if (releases.Count == 0)
            {
                throw new MetadataException(MetadataError.InvalidResponse, "Mojang manifest has no usable releases");
            }

            return releases.OrderByDescending(r => r.Version.ReleaseTime).ToList();
        }, cancellationToken);

    private async Task<int> LoadJavaAsync(Uri versionUrl, CancellationToken cancellationToken)
    {
        using var doc = await _http.GetJsonAsync(versionUrl, Mojang, cancellationToken);
        if (doc.RootElement.TryGetProperty("javaVersion", out var java)
            && java.TryGetProperty("majorVersion", out var major)
            && major.TryGetInt32(out var value))
        {
            return value;
        }

        // Historical versions without the field are not guessed.
        throw new MetadataException(MetadataError.Unsupported, "No Java requirement in version metadata");
    }

    private async Task<ResolvedServerVersion> ResolveFabricAsync(string version, int java, CancellationToken cancellationToken)
    {
        var games = await Cached("fabric:games", async ct =>
        {
            using var doc = await _http.GetJsonAsync(new Uri("https://meta.fabricmc.net/v2/versions/game"), Fabric, ct);
            return RootArray(doc).Select(e => Str(e, "version")).OfType<string>().ToHashSet();
        }, cancellationToken);
        if (!games.Contains(version))
        {
            throw new MetadataException(MetadataError.Unsupported, $"Fabric does not support {version}");
        }

        var loader = await Cached($"fabric:loader:{version}", async ct =>
        {
            using var doc = await _http.GetJsonAsync(
                new Uri($"https://meta.fabricmc.net/v2/versions/loader/{Uri.EscapeDataString(version)}"), Fabric, ct);
            return RootArray(doc)
                .Select(e => e.TryGetProperty("loader", out var l) ? l : default)
                .Where(l => l.ValueKind == JsonValueKind.Object && Bool(l, "stable") == true)
                .Select(l => Str(l, "version"))
                .FirstOrDefault(ServerDefinitionValidator.IsConcreteVersion);
        }, cancellationToken);

        var installer = await Cached("fabric:installer", async ct =>
        {
            using var doc = await _http.GetJsonAsync(new Uri("https://meta.fabricmc.net/v2/versions/installer"), Fabric, ct);
            return RootArray(doc)
                .Where(e => Bool(e, "stable") == true)
                .Select(e => Str(e, "version"))
                .FirstOrDefault(ServerDefinitionValidator.IsConcreteVersion);
        }, cancellationToken);

        if (loader is null || installer is null)
        {
            throw new MetadataException(MetadataError.Unsupported, $"No stable Fabric loader/installer for {version}");
        }

        return new ResolvedServerVersion(ServerType.Fabric, version, RequireJavaImage(java), loader, installer, null, true);
    }

    private async Task<ResolvedServerVersion> ResolvePaperAsync(string version, int mojangJava, CancellationToken cancellationToken)
    {
        var detail = await Cached($"paper:version:{version}", async ct =>
        {
            using var doc = await _http.GetJsonAsync(
                new Uri($"https://fill.papermc.io/v3/projects/paper/versions/{Uri.EscapeDataString(version)}"), Paper, ct);
            var v = doc.RootElement.TryGetProperty("version", out var ve) ? ve : default;
            if (v.ValueKind != JsonValueKind.Object
                || !v.TryGetProperty("java", out var java) || !java.TryGetProperty("version", out var jv)
                || !jv.TryGetProperty("minimum", out var min) || !min.TryGetInt32(out var minimum))
            {
                throw new MetadataException(MetadataError.InvalidResponse, "Paper version metadata incomplete");
            }

            var supported = v.TryGetProperty("support", out var support) && Str(support, "status") == "SUPPORTED";
            return (Minimum: minimum, Supported: supported);
        }, cancellationToken);

        var build = await Cached($"paper:builds:{version}", async ct =>
        {
            using var doc = await _http.GetJsonAsync(
                new Uri($"https://fill.papermc.io/v3/projects/paper/versions/{Uri.EscapeDataString(version)}/builds"), Paper, ct);
            // Stable builds only (Paper's own recommendation); unknown channels are ignored.
            return RootArray(doc)
                .Where(b => Str(b, "channel") == "STABLE")
                .Select(b => b.TryGetProperty("id", out var id) && id.TryGetInt32(out var n) ? n : 0)
                .Where(n => n > 0)
                .DefaultIfEmpty(0)
                .Max();
        }, cancellationToken);

        if (build == 0)
        {
            throw new MetadataException(MetadataError.Unsupported, $"No stable Paper build for {version} yet");
        }

        var java = Math.Max(mojangJava, detail.Minimum);
        return new ResolvedServerVersion(ServerType.Paper, version, RequireJavaImage(java), null, null, build, detail.Supported);
    }

    /// <summary>An itzg image with exactly this Java must exist; requirements are never rounded up.</summary>
    private static int RequireJavaImage(int java) =>
        ServerDefinitionValidator.SupportedJavaMajors.Contains(java)
            ? java
            : throw new MetadataException(MetadataError.Unsupported, $"No server image for Java {java}");

    /// <summary>
    /// Shared, cached work runs without any caller's token (each request is still bounded by the
    /// HTTP deadline); a caller that gives up only stops waiting, it never cancels the shared load.
    /// </summary>
    private async Task<T> Cached<T>(string key, Func<CancellationToken, Task<T>> load, CancellationToken cancellationToken)
    {
        var task = _cache.GetOrAdd(key, _ =>
        {
            var created = Box(() => load(CancellationToken.None));
            // Evict a failed load even if nobody is waiting any more, so a later lookup retries.
            created.ContinueWith(
                t => _cache.TryRemove(new KeyValuePair<string, Task<object>>(key, t)),
                CancellationToken.None,
                TaskContinuationOptions.NotOnRanToCompletion,
                TaskScheduler.Default);
            return created;
        });
        try
        {
            return (T)await task.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // the shared load keeps running for the next caller
        }
        catch
        {
            _cache.TryRemove(new KeyValuePair<string, Task<object>>(key, task)); // never cache failures
            throw;
        }

        static async Task<object> Box(Func<Task<T>> load) => (await load())!;
    }

    private static JsonElement.ArrayEnumerator RootArray(JsonDocument doc) =>
        doc.RootElement.ValueKind == JsonValueKind.Array
            ? doc.RootElement.EnumerateArray()
            : throw new MetadataException(MetadataError.InvalidResponse, "Expected a JSON array");

    private static IEnumerable<JsonElement> Array(JsonElement root, string property) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(property, out var a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray()
            : throw new MetadataException(MetadataError.InvalidResponse, $"Missing '{property}'");

    private static string? Str(JsonElement e, string property) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool? Bool(JsonElement e, string property) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(property, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean()
            : null;
}
