using System.Runtime.InteropServices;

namespace Bonfire.Infrastructure.Linux;

/// <summary>Minimal libc bindings. Only Linux on x86_64 and arm64 is supported.</summary>
internal static partial class LibC
{
    private const string Lib = "libc";

    public const int LOCK_EX = 2;
    public const int LOCK_NB = 4;
    public const int LOCK_UN = 8;

    public const int EINTR = 4;
    public const int EWOULDBLOCK = 11;

    public const int O_RDONLY = 0x0;
    public const int O_RDWR = 0x2;
    public const int O_CREAT = 0x40;
    public const int O_CLOEXEC = 0x80000;
    public const int O_NONBLOCK = 0x800;

    /// <summary>O_NOFOLLOW differs between architectures.</summary>
    public static int O_NOFOLLOW => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => 0x20000,
        Architecture.Arm64 => 0x8000,
        var other => throw new PlatformNotSupportedException($"Unsupported architecture: {other}."),
    };

    /// <summary>O_DIRECTORY differs between architectures.</summary>
    public static int O_DIRECTORY => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => 0x10000,
        Architecture.Arm64 => 0x4000,
        var other => throw new PlatformNotSupportedException($"Unsupported architecture: {other}."),
    };

    [LibraryImport(Lib, EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int Open(string path, int flags, uint mode);

    [LibraryImport(Lib, EntryPoint = "flock", SetLastError = true)]
    public static partial int Flock(int fd, int operation);

    [LibraryImport(Lib, EntryPoint = "fstat", SetLastError = true)]
    private static unsafe partial int FstatRaw(int fd, byte* buffer);

    private const uint S_IFMT = 0xF000;
    public const uint S_IFREG = 0x8000;

    /// <summary>
    /// The file type bits of an open descriptor (st_mode &amp; S_IFMT). Reads st_mode from a raw
    /// struct stat buffer at the architecture's offset (x86_64: 24, arm64: 16).
    /// </summary>
    public static unsafe uint FileType(int fd)
    {
        var offset = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => 24,
            Architecture.Arm64 => 16,
            var other => throw new PlatformNotSupportedException($"Unsupported architecture: {other}."),
        };
        var buffer = stackalloc byte[256];
        if (FstatRaw(fd, buffer) != 0)
        {
            throw new IOException($"fstat failed: {Marshal.GetPInvokeErrorMessage(Marshal.GetLastPInvokeError())}");
        }

        return *(uint*)(buffer + offset) & S_IFMT;
    }

    public const int AT_FDCWD = -100;
    public const uint RENAME_NOREPLACE = 1;
    public const int EEXIST = 17;

    [LibraryImport(Lib, EntryPoint = "renameat2", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int RenameAt2(int oldDirFd, string oldPath, int newDirFd, string newPath, uint flags);

    [LibraryImport(Lib, EntryPoint = "fsync", SetLastError = true)]
    public static partial int Fsync(int fd);

    [LibraryImport(Lib, EntryPoint = "getuid")]
    public static partial uint GetUid();

    [LibraryImport(Lib, EntryPoint = "getgid")]
    public static partial uint GetGid();

    [LibraryImport(Lib, EntryPoint = "umask")]
    public static partial uint Umask(uint mask);
}
