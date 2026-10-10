using System.Text.RegularExpressions;
using Bonfire.Core.Servers;

namespace Bonfire.Core.Content;

/// <summary>Where a server keeps add-ons and which Modrinth loaders it accepts.</summary>
public sealed record ContentProfile(string Directory, IReadOnlyList<string> Loaders, string GameVersion, ServerType ServerType)
{
    /// <summary>Null for servers without add-ons (Vanilla).</summary>
    public static ContentProfile? For(ServerDefinition definition) => definition.Type switch
    {
        ServerType.Fabric => new ContentProfile("mods", ["fabric"], definition.MinecraftVersion, definition.Type),
        // Paper runs Paper, Spigot and Bukkit plugins.
        ServerType.Paper => new ContentProfile("plugins", ["paper", "spigot", "bukkit"], definition.MinecraftVersion, definition.Type),
        _ => null,
    };
}

public enum EnvironmentSupport
{
    /// <summary>Runs on a dedicated server.</summary>
    Server,

    /// <summary>Client-side or single-player only: useless or harmful on a server.</summary>
    ClientOnly,

    /// <summary>Not declared; installing needs confirmation.</summary>
    Unknown,
}

public static partial class ContentRules
{
    public const long MaxFileBytes = 256L * 1024 * 1024;

    /// <summary>Version-level environment wins; the legacy project server_side is the fallback.</summary>
    public static EnvironmentSupport Environment(string? versionEnvironment, string? projectServerSide) => versionEnvironment switch
    {
        "client_only" or "singleplayer_only" => EnvironmentSupport.ClientOnly,
        "client_and_server" or "client_only_server_optional" or "server_only" or "server_only_client_optional"
            or "dedicated_server_only" or "client_or_server" or "client_or_server_prefers_both" => EnvironmentSupport.Server,
        _ => projectServerSide switch
        {
            "required" or "optional" => EnvironmentSupport.Server,
            "unsupported" => EnvironmentSupport.ClientOnly,
            _ => EnvironmentSupport.Unknown,
        },
    };

    private static readonly HashSet<string> NonInstallableTypes = new(StringComparer.Ordinal)
    {
        "sources-jar", "dev-jar", "javadoc-jar", "signature", "required-resource-pack", "optional-resource-pack",
    };

    /// <summary>
    /// The one JAR to install from a version: the primary file if it is an installable JAR,
    /// otherwise the only installable JAR. Null when the choice is ambiguous or there is none.
    /// </summary>
    public static ModrinthFile? SelectFile(ModrinthVersion version)
    {
        var jars = version.Files
            .Where(f => f.FileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)
                        && (f.FileType is null || !NonInstallableTypes.Contains(f.FileType)))
            .ToList();
        var primary = jars.Where(f => f.Primary).ToList();
        return primary.Count == 1 ? primary[0] : jars.Count == 1 ? jars[0] : null;
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._+\-]{0,126}\.jar\z")]
    private static partial Regex SafeJarName();

    [GeneratedRegex(@"^[A-Za-z0-9]{1,64}\z")]
    private static partial Regex SafeId();

    public static bool IsSafeId(string? id) => id is not null && SafeId().IsMatch(id);

    public static bool IsSha512(string? hash) =>
        hash is { Length: 128 } && hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static bool IsSafeJarName(string? name) => name is not null && SafeJarName().IsMatch(name) && !name.Contains("..", StringComparison.Ordinal);

    /// <summary>The file name to install under: the API's name if safe, else one derived from validated ids.</summary>
    public static string InstallFileName(string apiFileName, string projectId, string versionId)
    {
        if (SafeJarName().IsMatch(apiFileName) && !apiFileName.Contains("..", StringComparison.Ordinal))
        {
            return apiFileName;
        }

        if (!IsSafeId(projectId) || !IsSafeId(versionId))
        {
            throw new ArgumentException("Unsafe project or version id");
        }

        return $"{projectId}-{versionId}.jar";
    }
}
