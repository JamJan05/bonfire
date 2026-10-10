using Bonfire.Core.Servers;

namespace Bonfire.Infrastructure.Servers;

/// <summary>
/// Reads a server's server.properties to pre-fill the settings editor. The file is written by the
/// server inside the container, so it is untrusted: size-limited, never followed through symbolic
/// links, and never logged (it contains the RCON password).
/// </summary>
public static class ServerPropertiesFile
{
    public const long MaxBytes = 64 * 1024;

    /// <summary>Returns null when the file is missing, too large, a link, or unreadable.</summary>
    public static IReadOnlyDictionary<string, string>? Read(ServerPaths paths)
    {
        try
        {
            var dataDir = new DirectoryInfo(paths.DataDir);
            var file = new FileInfo(Path.Combine(paths.DataDir, "server.properties"));
            if (!dataDir.Exists || dataDir.LinkTarget is not null || !file.Exists || file.LinkTarget is not null || file.Length > MaxBytes)
            {
                return null;
            }

            // O_NOFOLLOW: a link swapped in after the check above is refused by the kernel.
            // O_NONBLOCK: a FIFO planted by the server cannot block the open (or later reads).
            var fd = Linux.LibC.Open(
                file.FullName, Linux.LibC.O_RDONLY | Linux.LibC.O_NOFOLLOW | Linux.LibC.O_CLOEXEC | Linux.LibC.O_NONBLOCK, 0);
            if (fd < 0)
            {
                return null;
            }

            using var stream = new FileStream(new Microsoft.Win32.SafeHandles.SafeFileHandle(fd, ownsHandle: true), FileAccess.Read, bufferSize: 0);
            // Only regular files: FIFOs, sockets and devices are refused by their real type.
            if (Linux.LibC.FileType(fd) != Linux.LibC.S_IFREG || stream.Length > MaxBytes)
            {
                return null;
            }

            using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
            var buffer = new char[MaxBytes];
            var read = reader.ReadBlock(buffer, 0, buffer.Length);
            return JavaProperties.Parse(new string(buffer, 0, read));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
