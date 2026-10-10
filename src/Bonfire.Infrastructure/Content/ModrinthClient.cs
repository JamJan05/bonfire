using System.Globalization;
using System.Text.Json;
using Bonfire.Core.Content;
using Bonfire.Core.Metadata;
using Bonfire.Infrastructure.Metadata;

namespace Bonfire.Infrastructure.Content;

/// <summary>
/// Modrinth API v2 over the hardened metadata client. Everything returned is untrusted: ids,
/// hashes, sizes and file URLs are validated, and file URLs must point to Modrinth's CDN.
/// </summary>
public sealed class ModrinthClient : IModrinth
{
    public const int PageSize = 20;
    public static readonly ProviderPolicy Api = new("api.modrinth.com", ["/v2/"]);
    public static readonly ProviderPolicy Cdn = new("cdn.modrinth.com", ["/data/"]);

    private readonly MetadataHttpClient _http;

    public ModrinthClient(MetadataHttpClient http) => _http = http;

    public async Task<SearchPage> SearchAsync(string query, ContentProfile profile, SearchSort sort, int offset, CancellationToken cancellationToken)
    {
        // Facets: AND between the inner arrays, OR inside one.
        var facets = JsonSerializer.Serialize(new[]
        {
            profile.Loaders.Select(l => $"categories:{l}").ToArray(),
            [$"versions:{profile.GameVersion}"],
            ["server_side:required", "server_side:optional", "server_side:unknown"],
        });
        var index = sort switch
        {
            SearchSort.Downloads => "downloads",
            SearchSort.Updated => "updated",
            _ => "relevance",
        };
        var uri = new Uri($"https://api.modrinth.com/v2/search?query={Uri.EscapeDataString(query.Trim())}&facets={Uri.EscapeDataString(facets)}"
                          + $"&index={index}&limit={PageSize}&offset={Math.Max(0, offset)}");
        using var doc = await _http.GetJsonAsync(uri, Api, cancellationToken);
        var root = doc.RootElement;
        var hits = Array(root, "hits").Select(ParseSearchHit).OfType<ModrinthProject>().ToList();
        return new SearchPage(hits, Int(root, "total_hits"), Int(root, "offset"));
    }

    public async Task<ModrinthProject> GetProjectAsync(string projectId, CancellationToken cancellationToken)
    {
        RequireId(projectId);
        using var doc = await _http.GetJsonAsync(new Uri($"https://api.modrinth.com/v2/project/{projectId}"), Api, cancellationToken);
        var p = doc.RootElement;
        return new ModrinthProject(
            RequireIdValue(Str(p, "id")), Str(p, "slug") ?? "", Str(p, "title") ?? "", Str(p, "description") ?? "", null,
            p.TryGetProperty("license", out var lic) && lic.ValueKind == JsonValueKind.Object ? Str(lic, "id") : null,
            Long(p, "downloads"), Date(p, "updated"), Str(p, "client_side"), Str(p, "server_side"), null,
            Strings(p, "game_versions"), Strings(p, "loaders"));
    }

    public async Task<IReadOnlyList<ModrinthVersion>> GetCompatibleVersionsAsync(string projectId, ContentProfile profile, CancellationToken cancellationToken)
    {
        RequireId(projectId);
        var loaders = Uri.EscapeDataString(JsonSerializer.Serialize(profile.Loaders));
        var games = Uri.EscapeDataString(JsonSerializer.Serialize(new[] { profile.GameVersion }));
        using var doc = await _http.GetJsonAsync(
            new Uri($"https://api.modrinth.com/v2/project/{projectId}/version?loaders={loaders}&game_versions={games}"), Api, cancellationToken);
        return RootArray(doc).Select(ParseVersion).OfType<ModrinthVersion>()
            // Re-check what the API claims: the filter is not trusted alone.
            .Where(v => v.GameVersions.Contains(profile.GameVersion) && v.Loaders.Any(profile.Loaders.Contains))
            .OrderByDescending(v => v.Published)
            .ToList();
    }

    public async Task<ModrinthVersion> GetVersionAsync(string versionId, CancellationToken cancellationToken)
    {
        RequireId(versionId);
        using var doc = await _http.GetJsonAsync(new Uri($"https://api.modrinth.com/v2/version/{versionId}"), Api, cancellationToken);
        return ParseVersion(doc.RootElement) ?? throw new MetadataException(MetadataError.InvalidResponse, "invalid version");
    }

    public async Task<IReadOnlyDictionary<string, ModrinthVersion>> GetVersionsByHashAsync(IReadOnlyList<string> sha512, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, ModrinthVersion>(StringComparer.Ordinal);
        foreach (var batch in sha512.Where(IsSha512).Distinct().Chunk(100))
        {
            var body = JsonSerializer.Serialize(new { hashes = batch, algorithm = "sha512" });
            using var doc = await _http.PostJsonAsync(new Uri("https://api.modrinth.com/v2/version_files"), Api, body, cancellationToken);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new MetadataException(MetadataError.InvalidResponse, "expected an object");
            }

            foreach (var property in doc.RootElement.EnumerateObject())
            {
                // Only accept an answer for a hash we asked about whose file really has that hash.
                if (batch.Contains(property.Name) && ParseVersion(property.Value) is { } version
                    && version.Files.Any(f => f.Sha512 == property.Name))
                {
                    result[property.Name] = version;
                }
            }
        }

        return result;
    }

    // ------------------------------------------------------------------ parsing (fail closed)

    private static ModrinthProject? ParseSearchHit(JsonElement h)
    {
        var id = Str(h, "project_id");
        if (!ContentRules.IsSafeId(id))
        {
            return null;
        }

        return new ModrinthProject(
            id!, Str(h, "slug") ?? "", Str(h, "title") ?? id!, Str(h, "description") ?? "", Str(h, "author"), Str(h, "license"),
            Long(h, "downloads"), Date(h, "date_modified"), Str(h, "client_side"), Str(h, "server_side"),
            h.TryGetProperty("environment", out var env) && env.ValueKind == JsonValueKind.Array && env.GetArrayLength() > 0
                ? env[0].GetString() : null,
            Strings(h, "versions"), []);
    }

    internal static ModrinthVersion? ParseVersion(JsonElement v)
    {
        var id = Str(v, "id");
        var projectId = Str(v, "project_id");
        if (!ContentRules.IsSafeId(id) || !ContentRules.IsSafeId(projectId))
        {
            return null;
        }

        var files = new List<ModrinthFile>();
        foreach (var f in Array(v, "files"))
        {
            var hashes = f.TryGetProperty("hashes", out var h) ? h : default;
            var url = Str(f, "url");
            var size = Long(f, "size");
            var sha512 = hashes.ValueKind == JsonValueKind.Object ? Str(hashes, "sha512") : null;
            var sha1 = hashes.ValueKind == JsonValueKind.Object ? Str(hashes, "sha1") : null;
            if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var u) || !Cdn.Allows(u)
                || !IsSha512(sha512) || size is <= 0 or > ContentRules.MaxFileBytes || Str(f, "filename") is not { Length: > 0 } name)
            {
                continue; // a file we cannot verify is never offered
            }

            files.Add(new ModrinthFile(url, name, sha1 ?? "", sha512!, size, Bool(f, "primary"), Str(f, "file_type")));
        }

        var dependencies = new List<ModrinthDependency>();
        foreach (var d in Array(v, "dependencies"))
        {
            var type = Str(d, "dependency_type") switch
            {
                "required" => DependencyType.Required,
                "optional" => DependencyType.Optional,
                "incompatible" => DependencyType.Incompatible,
                "embedded" => DependencyType.Embedded,
                _ => (DependencyType?)null,
            };
            if (type is { } t)
            {
                var depProject = Str(d, "project_id");
                var depVersion = Str(d, "version_id");
                dependencies.Add(new ModrinthDependency(
                    ContentRules.IsSafeId(depProject) ? depProject : null,
                    ContentRules.IsSafeId(depVersion) ? depVersion : null,
                    Str(d, "file_name"), t));
            }
        }

        return new ModrinthVersion(
            id!, projectId!, Str(v, "name") ?? "", Str(v, "version_number") ?? "", Str(v, "version_type") ?? "alpha",
            Strings(v, "game_versions"), Strings(v, "loaders"),
            v.TryGetProperty("environment", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null,
            Date(v, "date_published"), files, dependencies);
    }

    private static bool IsSha512(string? hash) => ContentRules.IsSha512(hash);

    private static void RequireId(string id)
    {
        if (!ContentRules.IsSafeId(id))
        {
            throw new ArgumentException("Invalid Modrinth id", nameof(id));
        }
    }

    private static string RequireIdValue(string? id) =>
        ContentRules.IsSafeId(id) ? id! : throw new MetadataException(MetadataError.InvalidResponse, "invalid id");

    private static JsonElement.ArrayEnumerator RootArray(JsonDocument doc) =>
        doc.RootElement.ValueKind == JsonValueKind.Array
            ? doc.RootElement.EnumerateArray()
            : throw new MetadataException(MetadataError.InvalidResponse, "expected an array");

    private static IEnumerable<JsonElement> Array(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray() : [];

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long Long(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;

    private static int Int(JsonElement e, string name) => (int)Math.Clamp(Long(e, name), 0, int.MaxValue);

    private static bool Bool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static DateTimeOffset Date(JsonElement e, string name) =>
        DateTimeOffset.TryParse(Str(e, name), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : default;

    private static IReadOnlyList<string> Strings(JsonElement e, string name) =>
        Array(e, name).Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList();
}
