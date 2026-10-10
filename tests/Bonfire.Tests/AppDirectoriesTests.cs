using Bonfire.Core;
using Bonfire.Infrastructure;

namespace Bonfire.Tests;

public sealed class AppDirectoriesTests
{
    [Fact]
    public void CreatesAllDirectoriesOwnerOnly()
    {
        using var temp = new TempDirectory();
        var paths = AppPaths.Resolve(key => key == "HOME" ? temp.Path : null);

        AppDirectories.Ensure(paths);

        foreach (var dir in paths.OwnedDirectories)
        {
            Assert.True(Directory.Exists(dir), dir);
            Assert.Equal(AppDirectories.OwnerOnly, File.GetUnixFileMode(dir));
        }
    }

    [Fact]
    public void TightensPermissionsOfExistingDirectory()
    {
        using var temp = new TempDirectory();
        var dir = Path.Combine(temp.Path, "app");
        Directory.CreateDirectory(dir);
        File.SetUnixFileMode(dir, AppDirectories.OwnerOnly | UnixFileMode.GroupRead | UnixFileMode.OtherExecute);

        AppDirectories.EnsureOwnedDirectory(dir);

        Assert.Equal(AppDirectories.OwnerOnly, File.GetUnixFileMode(dir));
    }

    [Fact]
    public void DoesNotChangeSharedParentPermissions()
    {
        using var temp = new TempDirectory();
        var parent = Path.Combine(temp.Path, "shared");
        Directory.CreateDirectory(parent);
        const UnixFileMode parentMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
        File.SetUnixFileMode(parent, parentMode);

        AppDirectories.EnsureOwnedDirectory(Path.Combine(parent, "bonfire"));

        Assert.Equal(parentMode, File.GetUnixFileMode(parent));
    }

    [Fact]
    public void RefusesSymlinkedDirectory()
    {
        using var temp = new TempDirectory();
        var target = Path.Combine(temp.Path, "target");
        Directory.CreateDirectory(target);
        var link = Path.Combine(temp.Path, "link");
        Directory.CreateSymbolicLink(link, target);

        Assert.Throws<IOException>(() => AppDirectories.EnsureOwnedDirectory(link));
    }

    [Fact]
    public void RefusesDanglingSymlink()
    {
        using var temp = new TempDirectory();
        var link = Path.Combine(temp.Path, "link");
        Directory.CreateSymbolicLink(link, Path.Combine(temp.Path, "missing"));

        Assert.Throws<IOException>(() => AppDirectories.EnsureOwnedDirectory(link));
    }
}
