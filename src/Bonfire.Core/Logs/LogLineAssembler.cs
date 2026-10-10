using System.Text;

namespace Bonfire.Core.Logs;

/// <summary>
/// Turns raw stdout/stderr byte chunks into bounded lines: incremental UTF-8 decoding (characters
/// split across chunks survive), redaction of secrets on the whole line before anything is cut,
/// and a hard length limit — a stream without newlines can never grow memory without bound.
/// </summary>
public sealed class LogLineAssembler
{
    public const int MaxLineLength = 4000;

    private readonly IReadOnlyList<string> _secrets;
    private readonly int _maxSecretLength;
    private readonly Dictionary<LogSource, StreamState> _streams = new()
    {
        [LogSource.Stdout] = new StreamState(),
        [LogSource.Stderr] = new StreamState(),
    };

    public LogLineAssembler(IReadOnlyList<string> secrets)
    {
        _secrets = secrets.Where(s => s.Length > 0).ToList();
        _maxSecretLength = _secrets.Count == 0 ? 0 : _secrets.Max(s => s.Length);
    }

    private sealed class StreamState
    {
        public Decoder Decoder { get; } = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false).GetDecoder();

        public StringBuilder Pending { get; } = new();

        /// <summary>The current line was already emitted truncated; drop everything until the next newline.</summary>
        public bool Discarding { get; set; }
    }

    /// <summary>Feeds bytes from one stream; returns the lines completed by them.</summary>
    public IReadOnlyList<LogLine> Feed(LogSource source, ReadOnlySpan<byte> bytes)
    {
        var state = _streams[source];
        var chars = new char[state.Decoder.GetCharCount(bytes, flush: false)];
        state.Decoder.GetChars(bytes, chars, flush: false);

        var lines = new List<LogLine>();
        foreach (var c in chars)
        {
            if (c == '\n')
            {
                if (state.Discarding)
                {
                    state.Discarding = false;
                }
                else
                {
                    lines.Add(Build(source, state.Pending.ToString(), truncated: false));
                }

                state.Pending.Clear();
                continue;
            }

            if (state.Discarding)
            {
                continue;
            }

            state.Pending.Append(c);

            // Keep enough beyond the limit to redact a secret that straddles the cut.
            if (state.Pending.Length > MaxLineLength + _maxSecretLength)
            {
                lines.Add(Build(source, state.Pending.ToString(), truncated: true));
                state.Pending.Clear();
                state.Discarding = true;
            }
        }

        return lines;
    }

    /// <summary>Emits whatever is left (stream ended without a final newline).</summary>
    public IReadOnlyList<LogLine> Flush()
    {
        var lines = new List<LogLine>();
        foreach (var (source, state) in _streams)
        {
            var rest = new char[state.Decoder.GetCharCount([], flush: true)];
            state.Decoder.GetChars([], rest, flush: true);
            state.Pending.Append(rest);
            if (!state.Discarding && state.Pending.Length > 0)
            {
                lines.Add(Build(source, state.Pending.ToString(), truncated: false));
            }

            state.Pending.Clear();
            state.Discarding = false;
        }

        return lines;
    }

    private LogLine Build(LogSource source, string raw, bool truncated)
    {
        raw = raw.TrimEnd('\r');
        var (rawTimestamp, timestamp, rest) = LogLine.SplitTimestamp(raw);
        // Order matters: redact the full text first, then sanitize, then cut.
        var text = LogText.Sanitize(LogText.Redact(rest, _secrets));
        if (truncated)
        {
            // The rest of this line is discarded: an incomplete secret may end the buffer.
            text = LogText.StripPartialSecretSuffix(text, _secrets);
        }

        if (text.Length > MaxLineLength)
        {
            text = text[..MaxLineLength];
            truncated = true;
        }

        return new LogLine(rawTimestamp, timestamp, text, source, LogText.DetectLevel(text), truncated);
    }
}
