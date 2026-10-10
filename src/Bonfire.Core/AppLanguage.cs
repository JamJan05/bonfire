namespace Bonfire.Core;

/// <summary>User interface languages shipped with the application.</summary>
public static class AppLanguage
{
    public const string English = "en";
    public const string Polish = "pl";

    public const string Default = English;

    public static IReadOnlyList<string> Supported { get; } = [English, Polish];

    /// <summary>Returns a supported language code, falling back to the default.</summary>
    public static string Normalize(string? code) =>
        code is not null && Supported.Contains(code, StringComparer.OrdinalIgnoreCase)
            ? code.ToLowerInvariant()
            : Default;
}
