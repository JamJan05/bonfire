using System.Text.RegularExpressions;
using Bonfire.Core.Logs;

namespace Bonfire.Core.Rcon;

public enum RconOutcome
{
    /// <summary>rcon-cli finished; <see cref="RconResult.Output"/> is the server's answer.</summary>
    Completed,

    /// <summary>No answer in time: the command may or may not have been executed. Never retried.</summary>
    Unknown,
}

/// <param name="ExitCode">rcon-cli's exit code when known; it reports connection problems, not command errors.</param>
public sealed record RconResult(RconOutcome Outcome, string Output, long? ExitCode, bool OutputTruncated);

public static partial class RconCommands
{
    public const int MaxLength = 1000;
    public const int MaxOutputBytes = 64 * 1024;

    /// <summary>Returns a resource key describing why the command is refused, or null when it may be sent.</summary>
    public static string? Validate(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return "Rcon_Empty";
        }

        if (command.Length > MaxLength)
        {
            return "Rcon_TooLong";
        }

        // One command per call: newlines or other control characters could smuggle extra input.
        return command.Any(char.IsControl) ? "Rcon_InvalidCharacters" : null;
    }

    /// <summary>The console's "stop" is routed through Bonfire's own graceful stop.</summary>
    public static bool IsStopCommand(string command) =>
        string.Equals(command.Trim().TrimStart('/'), "stop", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Sanitizes server output line by line and redacts secrets. When the output was cut off (size
    /// cap or timeout), a trailing fragment that could start a secret is removed as well.
    /// </summary>
    public static string CleanOutput(string output, IReadOnlyList<string> secrets, bool cutOff = false)
    {
        var redacted = LogText.Redact(output, secrets);
        if (cutOff)
        {
            redacted = LogText.StripPartialSecretSuffix(redacted, secrets);
        }

        return string.Join('\n', redacted
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split('\n')
                .Select(LogText.Sanitize))
            .TrimEnd();
    }

    [GeneratedRegex(@"There are (?<n>\d+) (?:of a max of|out of maximum|/) ?(?<m>\d+) players? online[.:]?(?<rest>.*)", RegexOptions.Singleline)]
    private static partial Regex ListPattern();

    [GeneratedRegex(@"^(?<n>\d+)\s*/\s*(?<m>\d+)")]
    private static partial Regex ShortPattern();

    /// <summary>Best-effort parse of the "list" command (Vanilla, Paper and plugin variants).</summary>
    public static PlayerList ParseList(string output)
    {
        var text = output.Trim();
        var match = ListPattern().Match(text);
        if (!match.Success)
        {
            var shortMatch = ShortPattern().Match(text);
            return shortMatch.Success
                ? new PlayerList(int.Parse(shortMatch.Groups["n"].Value), int.Parse(shortMatch.Groups["m"].Value), null)
                : PlayerList.Unknown;
        }

        var online = int.Parse(match.Groups["n"].Value);
        var max = int.Parse(match.Groups["m"].Value);
        if (online == 0)
        {
            return new PlayerList(0, max, []);
        }

        // Vanilla: names after the colon. Paper: "group: a, b" lines. Anything else: names unknown.
        var rest = match.Groups["rest"].Value;
        var names = rest
            .Split('\n')
            .Select(l => l.Contains(':', StringComparison.Ordinal) ? l[(l.LastIndexOf(':') + 1)..] : l)
            .SelectMany(l => l.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(n => PlayerName().IsMatch(n))
            .ToList();
        return new PlayerList(online, max, names.Count == online ? names : null);
    }

    [GeneratedRegex(@"^[A-Za-z0-9_]{1,16}\z")]
    private static partial Regex PlayerName();
}

/// <param name="Names">null when the count is known but the names could not be read reliably.</param>
public sealed record PlayerList(int? Online, int? Max, IReadOnlyList<string>? Names)
{
    public static PlayerList Unknown { get; } = new(null, null, null);
}
