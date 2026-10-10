using System.Globalization;

namespace Bonfire.Infrastructure.Linux;

public sealed record MemoryInfo(long TotalBytes, long AvailableBytes);

/// <summary>Host memory from /proc/meminfo (advisory only — never used to refuse an action).</summary>
public static class HostMemory
{
    public static MemoryInfo? Read(string path = "/proc/meminfo")
    {
        try
        {
            long? total = null, available = null;
            foreach (var line in File.ReadLines(path))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var kib))
                {
                    if (parts[0] == "MemTotal:")
                    {
                        total = kib * 1024;
                    }
                    else if (parts[0] == "MemAvailable:")
                    {
                        available = kib * 1024;
                    }
                }
            }

            return total is { } t && available is { } a ? new MemoryInfo(t, a) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
