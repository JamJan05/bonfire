using System.Text;
using Bonfire.Core.Logs;

namespace Bonfire.Tests;

public sealed class LogTests
{
    private const string Ts = "2026-10-09T12:13:42.123456789Z ";

    private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void MultiByteCharactersSplitAcrossChunksSurvive()
    {
        var assembler = new LogLineAssembler([]);
        var bytes = B(Ts + "Zażółć gęślą jaźń\n");
        var lines = new List<LogLine>();
        for (var i = 0; i < bytes.Length; i++)
        {
            lines.AddRange(assembler.Feed(LogSource.Stdout, bytes.AsSpan(i, 1))); // one byte at a time
        }

        Assert.Equal("Zażółć gęślą jaźń", Assert.Single(lines).Text);
    }

    [Fact]
    public void TimestampKeepsFullPrecisionAsCursor()
    {
        var line = Assert.Single(new LogLineAssembler([]).Feed(LogSource.Stdout, B(Ts + "hello\n")));

        Assert.Equal("2026-10-09T12:13:42.123456789Z", line.RawTimestamp);
        Assert.Equal(new DateTimeOffset(2026, 10, 9, 12, 13, 42, TimeSpan.Zero).AddTicks(1234567), line.Timestamp);
        Assert.Equal("hello", line.Text);
    }

    [Fact]
    public void NewlineFreeFloodIsBoundedAndTruncatedOnce()
    {
        var assembler = new LogLineAssembler([]);
        var chunk = B(new string('x', 10_000));
        var lines = new List<LogLine>();
        for (var i = 0; i < 100; i++) // 1 MB without a newline
        {
            lines.AddRange(assembler.Feed(LogSource.Stdout, chunk));
        }

        lines.AddRange(assembler.Feed(LogSource.Stdout, B("\nnext\n")));

        Assert.Equal(2, lines.Count);
        Assert.True(lines[0].Truncated);
        Assert.Equal(LogLineAssembler.MaxLineLength, lines[0].Text.Length);
        Assert.Equal("next", lines[1].Text);
    }

    [Fact]
    public void SecretSplitAcrossChunksIsRedacted()
    {
        var assembler = new LogLineAssembler(["s3cr3t-Pa55word"]);
        var lines = new List<LogLine>();
        lines.AddRange(assembler.Feed(LogSource.Stdout, B(Ts + "rcon.password=s3cr3t-")));
        lines.AddRange(assembler.Feed(LogSource.Stdout, B("Pa55word ok\n")));

        Assert.Equal($"rcon.password={LogText.Redacted} ok", Assert.Single(lines).Text);
    }

    [Fact]
    public void SecretStraddlingTheTruncationPointNeverLeaks()
    {
        const string secret = "TOPSECRETTOPSECRET";
        var assembler = new LogLineAssembler([secret]);
        var text = new string('a', LogLineAssembler.MaxLineLength - 5) + secret + new string('b', 100);

        var line = Assert.Single(assembler.Feed(LogSource.Stdout, B(text + "\n")));

        Assert.DoesNotContain("TOPSE", line.Text);
        Assert.True(line.Truncated);
    }

    [Theory]
    [InlineData(Ts, 3980)]                       // reviewer's example: timestamp counted in the buffer bound
    [InlineData(Ts + "\u001b[31m\u001b[0m", 3970)] // escape sequences removed after buffering
    [InlineData("", LogLineAssembler.MaxLineLength - 5)]
    public void PartialSecretAtTheEndOfATruncatedLineNeverLeaks(string prefix, int fill)
    {
        const string secret = "TOPSECRETTOPSECRET";
        var assembler = new LogLineAssembler([secret]);
        var text = System.Text.RegularExpressions.Regex.Unescape(prefix) + new string('a', fill) + secret + new string('b', 100);

        var lines = assembler.Feed(LogSource.Stdout, B(text + "\n"));

        var line = Assert.Single(lines);
        Assert.True(line.Truncated);
        for (var k = 2; k < secret.Length; k++)
        {
            Assert.False(line.Text.EndsWith(secret[..k], StringComparison.Ordinal), $"leaked {secret[..k]}");
        }

        Assert.DoesNotContain(secret, line.Text);
    }

    [Fact]
    public void TerminalEscapesFormattingCodesAndControlCharactersAreRemoved()
    {
        var line = Assert.Single(new LogLineAssembler([]).Feed(LogSource.Stdout,
            B("\u001b[31mred\u001b[0m §aGreen\u0007 \u001b]0;title\u0007tab\there\r\n")));

        Assert.Equal("red Green tab\there", line.Text);
    }

    [Theory]
    [InlineData("[12:13:44] [Server thread/INFO]: Done (2.800s)!", LogLevel.Info)]
    [InlineData("[12:13:44] [Server thread/WARN]: Can't keep up!", LogLevel.Warn)]
    [InlineData("[12:13:44] [Server thread/ERROR]: Boom", LogLevel.Error)]
    [InlineData("[12:15:49 INFO]: Done (10.9s)!", LogLevel.Info)]
    [InlineData("[12:15:49 WARN]: Paper warning", LogLevel.Warn)]
    [InlineData("\tat net.minecraft.Foo.bar(Foo.java:1)", LogLevel.Unknown)]
    [InlineData("[init] Running as uid=1000", LogLevel.Unknown)]
    public void LevelDetection(string text, LogLevel level) => Assert.Equal(level, LogText.DetectLevel(text));

    [Fact]
    public void PartialLastLineIsFlushed()
    {
        var assembler = new LogLineAssembler([]);
        Assert.Empty(assembler.Feed(LogSource.Stderr, B("no newline")));

        var line = Assert.Single(assembler.Flush());
        Assert.Equal("no newline", line.Text);
        Assert.Equal(LogSource.Stderr, line.Source);
    }

    [Fact]
    public void StdoutAndStderrAreAssembledSeparately()
    {
        var assembler = new LogLineAssembler([]);
        var lines = new List<LogLine>();
        lines.AddRange(assembler.Feed(LogSource.Stdout, B("out-")));
        lines.AddRange(assembler.Feed(LogSource.Stderr, B("err\n")));
        lines.AddRange(assembler.Feed(LogSource.Stdout, B("line\n")));

        Assert.Equal(["err", "out-line"], lines.Select(l => l.Text));
    }
}
