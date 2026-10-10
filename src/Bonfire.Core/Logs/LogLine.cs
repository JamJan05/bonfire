using System.Globalization;

namespace Bonfire.Core.Logs;

public enum LogSource
{
    Stdout,
    Stderr,
}

public enum LogLevel
{
    /// <summary>The line has no recognisable level (stack traces, startup output).</summary>
    Unknown,
    Info,
    Warn,
    Error,
}

/// <summary>One sanitized, redacted log line.</summary>
/// <param name="RawTimestamp">Docker's timestamp exactly as sent (nanosecond precision), used as the reconnect cursor.</param>
public sealed record LogLine(string? RawTimestamp, DateTimeOffset? Timestamp, string Text, LogSource Source, LogLevel Level, bool Truncated)
{
    /// <summary>Splits Docker's "2026-10-09T12:13:42.123456789Z message" prefix.</summary>
    public static (string? Raw, DateTimeOffset? Parsed, string Message) SplitTimestamp(string line)
    {
        var space = line.IndexOf(' ', StringComparison.Ordinal);
        if (space is < 20 or > 40)
        {
            return (null, null, line);
        }

        var raw = line[..space];
        // DateTimeOffset holds 100 ns; trim extra fraction digits for parsing only.
        var forParsing = raw.Length > 28 && raw.EndsWith('Z') ? raw[..27] + "Z" : raw;
        return DateTimeOffset.TryParse(forParsing, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? (raw, parsed, line[(space + 1)..])
            : (null, null, line);
    }
}
