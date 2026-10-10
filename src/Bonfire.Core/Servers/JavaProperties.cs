using System.Globalization;
using System.Text;

namespace Bonfire.Core.Servers;

/// <summary>
/// Parser for the java.util.Properties text format used by server.properties: comments (# !),
/// line continuations, escapes (\t \n \r \f \uXXXX) and the three separators (= : whitespace).
/// For duplicate keys the last one wins, as in Java. Input is untrusted; malformed escapes are
/// kept literally rather than throwing.
/// </summary>
public static class JavaProperties
{
    public static IReadOnlyDictionary<string, string> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var logical in LogicalLines(text))
        {
            var line = logical.TrimStart(' ', '\t', '\f');
            if (line.Length == 0 || line[0] is '#' or '!')
            {
                continue;
            }

            var (key, value) = SplitKeyValue(line);
            result[Unescape(key)] = Unescape(value);
        }

        return result;
    }

    /// <summary>Joins physical lines ending with an odd number of backslashes.</summary>
    private static IEnumerable<string> LogicalLines(string text)
    {
        var current = new StringBuilder();
        foreach (var raw in text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            var physical = current.Length > 0 ? raw.TrimStart(' ', '\t', '\f') : raw;
            var trailing = 0;
            for (var i = physical.Length - 1; i >= 0 && physical[i] == '\\'; i--)
            {
                trailing++;
            }

            if (trailing % 2 == 1 && !IsComment(current, physical))
            {
                current.Append(physical, 0, physical.Length - 1);
                continue;
            }

            current.Append(physical);
            yield return current.ToString();
            current.Clear();
        }

        if (current.Length > 0)
        {
            yield return current.ToString();
        }
    }

    // A comment line never continues (Java ignores the trailing backslash there).
    private static bool IsComment(StringBuilder current, string physical) =>
        current.Length == 0 && physical.TrimStart(' ', '\t', '\f') is { Length: > 0 } t && t[0] is '#' or '!';

    private static (string Key, string Value) SplitKeyValue(string line)
    {
        var i = 0;
        while (i < line.Length)
        {
            var c = line[i];
            if (c == '\\')
            {
                i += 2; // skip the escaped character
                continue;
            }

            if (c is '=' or ':' or ' ' or '\t' or '\f')
            {
                break;
            }

            i++;
        }

        var key = line[..Math.Min(i, line.Length)];
        var rest = i < line.Length ? line[i..] : "";
        rest = rest.TrimStart(' ', '\t', '\f');
        if (rest.Length > 0 && rest[0] is '=' or ':')
        {
            rest = rest[1..].TrimStart(' ', '\t', '\f');
        }

        return (key, rest);
    }

    private static string Unescape(string s)
    {
        if (!s.Contains('\\', StringComparison.Ordinal))
        {
            return s;
        }

        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] != '\\' || i == s.Length - 1)
            {
                sb.Append(s[i]);
                continue;
            }

            var next = s[++i];
            switch (next)
            {
                case 't': sb.Append('\t'); break;
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 'f': sb.Append('\f'); break;
                case 'u' when i + 4 < s.Length
                              && int.TryParse(s.AsSpan(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code):
                    sb.Append((char)code);
                    i += 4;
                    break;
                default: sb.Append(next); break;
            }
        }

        return sb.ToString();
    }
}
