using System.Runtime.InteropServices;
using System.Text;

namespace Bonfire.Infrastructure.Linux;

/// <summary>Durable, atomic replacement of small files.</summary>
public static class AtomicFile
{
    /// <summary>
    /// Writes to a new owner-only temporary file in the same directory, flushes it to disk,
    /// renames it over <paramref name="path"/> and fsyncs the directory so the rename itself
    /// survives a power loss. Readers see either the old or the new content, never a mix.
    /// </summary>
    public static async Task WriteAllTextAsync(string path, string content, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))
            ?? throw new ArgumentException("Path has no parent directory.", nameof(path));
        var temp = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            };
            await using (var stream = new FileStream(temp, options))
            {
                await stream.WriteAsync(Encoding.UTF8.GetBytes(content), cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }

        FsyncDirectory(directory);
    }

    private static void FsyncDirectory(string directory)
    {
        var fd = LibC.Open(directory, LibC.O_RDONLY | LibC.O_DIRECTORY | LibC.O_CLOEXEC, 0);
        if (fd < 0)
        {
            throw new IOException(
                $"Cannot open directory {directory} for fsync: {Marshal.GetPInvokeErrorMessage(Marshal.GetLastPInvokeError())}");
        }

        using var handle = new Microsoft.Win32.SafeHandles.SafeFileHandle(fd, ownsHandle: true);
        if (LibC.Fsync(fd) != 0)
        {
            throw new IOException(
                $"fsync of directory {directory} failed: {Marshal.GetPInvokeErrorMessage(Marshal.GetLastPInvokeError())}");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
