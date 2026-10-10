using System.Text;
using System.Text.RegularExpressions;

namespace Bonfire.Core.Logs;

/// <summary>Sanitizing, redaction and level detection for container log text (all untrusted).</summary>
public static partial class LogText
{
    public const string Redacted = "********";

    // CSI (ESC [ ... final), OSC (ESC ] ... BEL or ESC \), and other two-byte ESC sequences.
    [GeneratedRegex(@"\x1B\[[0-?]*[ -/]*[@-~]|\x1B\][^\x07\x1B]*(\x07|\x1B\\)?|\x1B[@-Z\\-_]")]
    private static partial Regex Escapes();

    // Minecraft formatting codes: § followed by one character.
    [GeneratedRegex("§.")]
    private static partial Regex FormattingCodes();

    // Vanilla/Fabric: "[12:13:44] [Server thread/INFO]:"  Paper: "[12:13:44 INFO]:"
    [GeneratedRegex(@"^\[\d{2}:\d{2}:\d{2}(?: (?<p>[A-Z]+))?\](?: \[[^\]/]*/(?<v>[A-Z]+)\])?")]
    private static partial Regex LevelPrefix();

    /// <summary>Removes terminal escapes, formatting codes and control characters (tab kept).</summary>
    public static string Sanitize(string text)
    {
        var withoutEscapes = FormattingCodes().Replace(Escapes().Replace(text, ""), "");
        var sb = new StringBuilder(withoutEscapes.Length);
        foreach (var c in withoutEscapes)
        {
            if (c == '\t' || !char.IsControl(c))
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    public static string Redact(string text, IReadOnlyList<string> secrets)
    {
        foreach (var secret in secrets)
        {
            if (secret.Length > 0)
            {
                text = text.Replace(secret, Redacted, StringComparison.Ordinal);
            }
        }

        return text;
    }

    /// <summary>
    /// Removes a trailing fragment that could be the beginning of a secret (used when the rest of
    /// the line is cut off, so a partially received secret is never shown).
    /// </summary>
    public static string StripPartialSecretSuffix(string text, IReadOnlyList<string> secrets)
    {
        var cut = 0;
        foreach (var secret in secrets)
        {
            for (var k = Math.Min(secret.Length - 1, text.Length); k > cut; k--)
            {
                if (text.AsSpan(text.Length - k).SequenceEqual(secret.AsSpan(0, k)))
                {
                    cut = k;
                    break;
                }
            }
        }

        return cut == 0 ? text : text[..^cut];
    }

    /// <summary>Best-effort severity; lines without a recognised prefix are Unknown and never hidden by level filters.</summary>
    public static LogLevel DetectLevel(string text)
    {
        var match = LevelPrefix().Match(text);
        var level = match.Groups["v"].Success ? match.Groups["v"].Value : match.Groups["p"].Success ? match.Groups["p"].Value : null;
        return level switch
        {
            "INFO" => LogLevel.Info,
            "WARN" or "WARNING" => LogLevel.Warn,
            "ERROR" or "FATAL" or "SEVERE" => LogLevel.Error,
            _ => LogLevel.Unknown,
        };
    }
}
