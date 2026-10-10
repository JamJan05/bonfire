using System.Security.Cryptography;

namespace Bonfire.Infrastructure.Backups;

/// <summary>Read-only pass-through stream that hashes every byte read and counts them.</summary>
internal sealed class HashingReadStream(Stream inner) : Stream
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

    public long BytesRead { get; private set; }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => BytesRead; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var n = inner.Read(buffer);
        _hash.AppendData(buffer[..n]);
        BytesRead += n;
        return n;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var n = await inner.ReadAsync(buffer, cancellationToken);
        _hash.AppendData(buffer.Span[..n]);
        BytesRead += n;
        return n;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <summary>Reads whatever remains so the hash covers the whole underlying stream.</summary>
    public async Task DrainAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        while (await ReadAsync(buffer, cancellationToken) > 0)
        {
        }
    }

    public string HashHex() => Convert.ToHexStringLower(_hash.GetCurrentHash());

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hash.Dispose();
            inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
