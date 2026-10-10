using System.Reflection;

namespace Bonfire.Core;

/// <summary>Stable identifiers of the application.</summary>
public static class AppIdentity
{
    /// <summary>Directory name used under every XDG base directory.</summary>
    public const string DirectoryName = "bonfire";

    public const string DisplayName = "Bonfire";

    public static string Version { get; } =
        typeof(AppIdentity).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion.Split('+')[0]
        ?? "0.0.0";
}
