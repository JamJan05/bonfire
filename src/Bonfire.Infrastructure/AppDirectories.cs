using Bonfire.Core;

namespace Bonfire.Infrastructure;

/// <summary>Creates and checks directories owned by the application.</summary>
public static class AppDirectories
{
    public const UnixFileMode OwnerOnly =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>
    /// Ensures every application directory exists, is a real directory (not a symlink)
    /// and is accessible only by the owner. Shared XDG parent directories are created if
    /// missing but their permissions are never changed.
    /// </summary>
    public static void Ensure(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        foreach (var dir in paths.OwnedDirectories)
        {
            EnsureOwnedDirectory(dir);
        }
    }

    public static void EnsureOwnedDirectory(string path)
    {
        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
        {
            Directory.CreateDirectory(parent);
        }

        var info = new DirectoryInfo(path);
        if (info.Exists || info.LinkTarget is not null)
        {
            if (info.LinkTarget is not null)
            {
                throw new IOException($"Application directory {path} is a symbolic link; refusing to use it.");
            }

            // CreateDirectory does not fix an existing directory, so tighten it explicitly.
            if ((info.UnixFileMode & ~OwnerOnly) != 0)
            {
                File.SetUnixFileMode(path, OwnerOnly);
            }

            return;
        }

        Directory.CreateDirectory(path, OwnerOnly);
    }
}
