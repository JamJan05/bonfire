using System.Net;
using System.Security.Cryptography;
using Bonfire.Core;
using Bonfire.Core.Content;
using Bonfire.Core.Metadata;
using Bonfire.Infrastructure.Metadata;

namespace Bonfire.Infrastructure.Content;

public sealed record DownloadProgress(long BytesDone, long BytesTotal);

/// <summary>
/// Downloads add-on files from Modrinth's CDN into a private staging file and verifies them
/// against the size and SHA-512 the API declared. A file that does not match is deleted.
/// </summary>
public sealed class ContentDownloader : IDisposable
{
    public static readonly TimeSpan FileDeadline = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(60);

    private readonly HttpClient _http;

    public ContentDownloader()
        : this(MetadataHttpClient.CreateHandler())
    {
    }

    public ContentDownloader(HttpMessageHandler handler)
    {
        _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(MetadataHttpClient.UserAgent);
    }

    /// <summary>Writes the verified file to <paramref name="targetPath"/> (which must not exist).</summary>
    public async Task DownloadAsync(ModrinthFile file, string targetPath, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        var uri = new Uri(file.Url);
        if (!ModrinthClient.Cdn.Allows(uri) || file.Size is <= 0 or > ContentRules.MaxFileBytes)
        {
            throw new MetadataException(MetadataError.InvalidResponse, "file not downloadable");
        }

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await DownloadOnceAsync(uri, file, targetPath, progress, cancellationToken);
                return;
            }
            catch (MetadataException ex) when (ex.Error == MetadataError.RateLimited && attempt == 0
                                               && (ex.RetryAfter ?? TimeSpan.FromSeconds(5)) <= MaxRetryAfter)
            {
                await Task.Delay(ex.RetryAfter ?? TimeSpan.FromSeconds(5), cancellationToken);
            }
        }
    }

    private async Task DownloadOnceAsync(Uri uri, ModrinthFile file, string targetPath, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(FileDeadline);
        var created = false;
        var complete = false;
        try
        {
            using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                // Retry-After may be a delay or an HTTP date.
                var retry = response.Headers.RetryAfter;
                var delay = retry?.Delta ?? (retry?.Date is { } date ? date - DateTimeOffset.UtcNow : null);
                throw new MetadataException(MetadataError.RateLimited, uri.Host)
                {
                    RetryAfter = delay is { } d ? (d < TimeSpan.Zero ? TimeSpan.Zero : d) : null,
                };
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new MetadataException(MetadataError.ProviderUnavailable, $"HTTP {(int)response.StatusCode}");
            }

            if (response.Content.Headers.ContentLength is { } length && length != file.Size)
            {
                throw new MetadataException(MetadataError.InvalidResponse, "size differs from the declared size");
            }

            await using var body = await response.Content.ReadAsStreamAsync(deadline.Token);
            await using var output = new FileStream(targetPath, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            });
            created = true; // from here on the file is ours to delete on failure
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
            var buffer = new byte[81920];
            long done = 0;
            int n;
            while ((n = await body.ReadAsync(buffer, deadline.Token)) > 0)
            {
                done += n;
                if (done > file.Size)
                {
                    throw new MetadataException(MetadataError.InvalidResponse, "file longer than declared");
                }

                hash.AppendData(buffer, 0, n);
                await output.WriteAsync(buffer.AsMemory(0, n), deadline.Token);
                progress?.Report(new DownloadProgress(done, file.Size));
            }

            if (done != file.Size || Convert.ToHexStringLower(hash.GetCurrentHash()) != file.Sha512)
            {
                throw new MetadataException(MetadataError.InvalidResponse, "checksum does not match");
            }

            output.Flush(flushToDisk: true);
            complete = true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new MetadataException(MetadataError.ProviderUnavailable, "download timed out");
        }
        catch (HttpRequestException ex)
        {
            throw new MetadataException(MetadataError.ProviderUnavailable, ex.Message, ex);
        }
        finally
        {
            if (created && !complete)
            {
                File.Delete(targetPath); // never leave an unverified file behind — and never touch one we did not create
            }
        }
    }

    public void Dispose() => _http.Dispose();
}
