using Bonfire.Infrastructure.Linux;

namespace Bonfire.Tests;

public sealed class AtomicFileTests
{
    [Fact]
    public async Task ReplacesExistingContentAndLeavesNoTemporaryFiles()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "file.json");
        await File.WriteAllTextAsync(path, "old", TestContext.Current.CancellationToken);

        await AtomicFile.WriteAllTextAsync(path, "new", TestContext.Current.CancellationToken);

        Assert.Equal("new", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        Assert.Single(Directory.GetFiles(temp.Path));
    }

    [Fact]
    public async Task CreatesOwnerOnlyFile()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "file.json");

        await AtomicFile.WriteAllTextAsync(path, "x", TestContext.Current.CancellationToken);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    [Fact]
    public async Task FailureKeepsOriginalAndCleansUp()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "target");
        Directory.CreateDirectory(path); // rename over a directory fails

        await Assert.ThrowsAnyAsync<IOException>(
            () => AtomicFile.WriteAllTextAsync(path, "x", TestContext.Current.CancellationToken));

        Assert.True(Directory.Exists(path));
        Assert.Empty(Directory.GetFiles(temp.Path));
    }
}
