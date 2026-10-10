namespace Bonfire.Infrastructure.Linux;

public static class ProcessUmask
{
    /// <summary>
    /// Restricts permissions of every file and directory the process creates (including
    /// rotated log files) to the owning user. Call once at startup, before other threads
    /// create files.
    /// </summary>
    public static void RestrictToOwner() => LibC.Umask(0b000_111_111); // 077
}
