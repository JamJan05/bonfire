using Bonfire.Core.Docker;

namespace Bonfire.Infrastructure.Docker;

/// <summary>Parses <c>SecurityOptions</c> entries such as <c>name=seccomp,profile=builtin</c>.</summary>
internal static class SecurityOptions
{
    public static DockerCapabilities ToCapabilities(IEnumerable<string>? options, string? cgroupVersion, string? cgroupDriver)
    {
        if (options is null)
        {
            return new DockerCapabilities(null, null, null, cgroupVersion, cgroupDriver);
        }

        var parsed = options.Select(Parse).ToList();
        var seccomp = parsed.FirstOrDefault(o => o.GetValueOrDefault("name") == "seccomp");

        return new DockerCapabilities(
            Rootless: parsed.Any(o => o.GetValueOrDefault("name") == "rootless"),
            Seccomp: seccomp is not null,
            SeccompProfile: seccomp?.GetValueOrDefault("profile"),
            CgroupVersion: NullIfEmpty(cgroupVersion),
            CgroupDriver: NullIfEmpty(cgroupDriver));
    }

    internal static Dictionary<string, string> Parse(string option)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in option.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = part.IndexOf('=', StringComparison.Ordinal);
            if (separator > 0)
            {
                result.TryAdd(part[..separator], part[(separator + 1)..]);
            }
        }

        return result;
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
