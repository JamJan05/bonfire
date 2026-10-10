namespace Bonfire.Core;

/// <summary>
/// Application directories resolved according to the XDG Base Directory Specification.
/// Resolution is pure: nothing is created on disk here.
/// </summary>
public sealed record AppPaths(string ConfigDir, string DataDir, string StateDir, string CacheDir)
{
    public string LogDir => Path.Combine(StateDir, "logs");

    public string SettingsFile => Path.Combine(ConfigDir, "settings.json");

    /// <summary>
    /// Lock file guarding against a second application instance. It lives in the state
    /// directory (not XDG_RUNTIME_DIR) so every launch of the same user agrees on one path.
    /// </summary>
    public string InstanceLockFile => Path.Combine(StateDir, "instance.lock");

    /// <summary>All directories owned by the application, in creation order.</summary>
    public IReadOnlyList<string> OwnedDirectories => [ConfigDir, DataDir, StateDir, CacheDir, LogDir];

    /// <summary>
    /// Resolves paths from environment variables. Per the specification, unset, empty and
    /// relative values are ignored and the standard fallback under HOME is used.
    /// </summary>
    public static AppPaths Resolve(Func<string, string?> getEnvironmentVariable)
    {
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);

        var home = getEnvironmentVariable("HOME");
        if (string.IsNullOrEmpty(home) || !Path.IsPathFullyQualified(home))
        {
            throw new InvalidOperationException(
                "HOME is not set or is not an absolute path.");
        }

        string Base(string variable, string fallbackRelativeToHome)
        {
            var value = getEnvironmentVariable(variable);
            return !string.IsNullOrEmpty(value) && Path.IsPathFullyQualified(value)
                ? value
                : Path.Combine(home, fallbackRelativeToHome);
        }

        return new AppPaths(
            ConfigDir: Path.Combine(Base("XDG_CONFIG_HOME", ".config"), AppIdentity.DirectoryName),
            DataDir: Path.Combine(Base("XDG_DATA_HOME", Path.Combine(".local", "share")), AppIdentity.DirectoryName),
            StateDir: Path.Combine(Base("XDG_STATE_HOME", Path.Combine(".local", "state")), AppIdentity.DirectoryName),
            CacheDir: Path.Combine(Base("XDG_CACHE_HOME", ".cache"), AppIdentity.DirectoryName));
    }
}
