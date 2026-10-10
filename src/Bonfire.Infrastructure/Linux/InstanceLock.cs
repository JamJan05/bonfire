using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Bonfire.Infrastructure.Linux;

public enum InstanceLockStatus
{
    Acquired,
    AlreadyHeld,
}

/// <summary>
/// Exclusive advisory lock (flock) held for the lifetime of the application.
/// Cooperative only: it prevents two Bonfire instances of the same user from running at
/// once, it is not a defence against a hostile process. The lock file is never unlinked
/// or replaced, so all instances always contend on the same inode.
/// </summary>
public sealed class InstanceLock : IDisposable
{
    private readonly SafeFileHandle _handle;

    private InstanceLock(SafeFileHandle handle) => _handle = handle;

    /// <summary>
    /// Tries to take the lock. Returns <see cref="InstanceLockStatus.AlreadyHeld"/> when
    /// another process holds it; throws <see cref="IOException"/> for any other failure.
    /// </summary>
    public static InstanceLockStatus TryAcquire(string path, out InstanceLock? instanceLock)
    {
        instanceLock = null;

        var fd = LibC.Open(path, LibC.O_RDWR | LibC.O_CREAT | LibC.O_CLOEXEC | LibC.O_NOFOLLOW, 0b110_000_000);
        if (fd < 0)
        {
            throw new IOException(
                $"Cannot open lock file {path}: {Marshal.GetPInvokeErrorMessage(Marshal.GetLastPInvokeError())}");
        }

        var handle = new SafeFileHandle(fd, ownsHandle: true);
        try
        {
            while (true)
            {
                if (LibC.Flock(fd, LibC.LOCK_EX | LibC.LOCK_NB) == 0)
                {
                    instanceLock = new InstanceLock(handle);
                    return InstanceLockStatus.Acquired;
                }

                var errno = Marshal.GetLastPInvokeError();
                if (errno == LibC.EINTR)
                {
                    continue;
                }

                handle.Dispose();
                if (errno == LibC.EWOULDBLOCK)
                {
                    return InstanceLockStatus.AlreadyHeld;
                }

                throw new IOException(
                    $"Cannot lock file {path}: {Marshal.GetPInvokeErrorMessage(errno)}");
            }
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>Closing the descriptor releases the lock.</summary>
    public void Dispose() => _handle.Dispose();
}
