using System.Diagnostics;
using Bonfire.Infrastructure.Linux;

namespace Bonfire.Tests;

public sealed class InstanceLockTests
{
    [Fact]
    public void SecondAcquisitionIsRejectedWhileFirstIsHeld()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "instance.lock");

        Assert.Equal(InstanceLockStatus.Acquired, InstanceLock.TryAcquire(path, out var first));
        using (first)
        {
            // flock locks belong to the open file description, so a second open in the
            // same process contends exactly like another process would.
            Assert.Equal(InstanceLockStatus.AlreadyHeld, InstanceLock.TryAcquire(path, out var second));
            Assert.Null(second);
        }
    }

    [Fact]
    public void LockCanBeReacquiredAfterRelease()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "instance.lock");

        Assert.Equal(InstanceLockStatus.Acquired, InstanceLock.TryAcquire(path, out var first));
        first!.Dispose();

        Assert.Equal(InstanceLockStatus.Acquired, InstanceLock.TryAcquire(path, out var again));
        again!.Dispose();
        Assert.True(File.Exists(path), "Lock file must never be removed.");
    }

    [Fact]
    public void LockFileIsCreatedOwnerOnly()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "instance.lock");

        InstanceLock.TryAcquire(path, out var held);
        using (held)
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
    }

    [Fact]
    public void RefusesSymlinkAsLockFile()
    {
        using var temp = new TempDirectory();
        var target = Path.Combine(temp.Path, "target");
        File.WriteAllText(target, "");
        var link = Path.Combine(temp.Path, "instance.lock");
        File.CreateSymbolicLink(link, target);

        Assert.Throws<IOException>(() => InstanceLock.TryAcquire(link, out _));
    }

    [Fact]
    public void LockHeldByAnotherProcessIsDetectedAndFreedWhenItExits()
    {
        if (!File.Exists("/usr/bin/flock"))
        {
            Assert.Skip("util-linux flock(1) is not available");
        }

        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "instance.lock");
        File.WriteAllText(path, "");

        // Another process holds the lock until we close its stdin.
        using var holder = Process.Start(new ProcessStartInfo("/usr/bin/flock")
        {
            ArgumentList = { "--exclusive", path, "cat" },
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
        })!;

        var deadline = DateTime.UtcNow.AddSeconds(10);
        InstanceLockStatus status;
        while ((status = InstanceLock.TryAcquire(path, out var probe)) == InstanceLockStatus.Acquired)
        {
            // flock(1) has not taken the lock yet.
            probe!.Dispose();
            Assert.True(DateTime.UtcNow < deadline, "Child process never acquired the lock");
            Thread.Sleep(50);
        }

        Assert.Equal(InstanceLockStatus.AlreadyHeld, status);

        holder.StandardInput.Close();
        Assert.True(holder.WaitForExit(10_000));

        Assert.Equal(InstanceLockStatus.Acquired, InstanceLock.TryAcquire(path, out var after));
        after!.Dispose();
    }
}
