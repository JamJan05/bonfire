using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Bonfire.Core;
using Bonfire.Core.Metadata;

namespace Bonfire.Infrastructure.Metadata;

/// <summary>Which URLs one provider may be fetched from: HTTPS, one exact host, given path prefixes.</summary>
public sealed record ProviderPolicy(string Host, IReadOnlyList<string> PathPrefixes)
{
    public bool Allows(Uri uri) =>
        uri.IsAbsoluteUri
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.IsDefaultPort
        && string.Equals(uri.Host, Host, StringComparison.OrdinalIgnoreCase)
        && string.IsNullOrEmpty(uri.UserInfo)
        && string.IsNullOrEmpty(uri.Fragment)
        && PathPrefixes.Any(p => uri.AbsolutePath.StartsWith(p, StringComparison.Ordinal));
}

/// <summary>
/// Fetches small JSON documents from metadata providers with strict limits. Responses are
/// untrusted: size, depth and time are bounded, redirects are refused, and connections to
/// private or loopback addresses are rejected even if DNS points there.
/// </summary>
public sealed class MetadataHttpClient : IDisposable
{
    public const long MaxResponseBytes = 8 * 1024 * 1024;
    public static readonly TimeSpan RequestDeadline = TimeSpan.FromSeconds(15);

    private static readonly JsonDocumentOptions JsonOptions = new() { MaxDepth = 64 };

    private readonly HttpClient _http;
    private readonly TimeSpan _deadline;

    public MetadataHttpClient()
        : this(CreateHandler())
    {
    }

    /// <summary>For tests: any handler (connection-level protections then do not apply).</summary>
    public MetadataHttpClient(HttpMessageHandler handler, TimeSpan? deadline = null)
    {
        _deadline = deadline ?? RequestDeadline;
        _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
    }

    public static string UserAgent { get; } =
        $"Bonfire/{AppIdentity.Version} (+https://github.com/JamJan05/bonfire)";

    public Task<JsonDocument> GetJsonAsync(Uri uri, ProviderPolicy policy, CancellationToken cancellationToken) =>
        SendJsonAsync(() => new HttpRequestMessage(HttpMethod.Get, uri), uri, policy, cancellationToken);

    /// <summary>POST with a JSON body (e.g. Modrinth's bulk hash lookup); same limits as GET.</summary>
    public Task<JsonDocument> PostJsonAsync(Uri uri, ProviderPolicy policy, string json, CancellationToken cancellationToken) =>
        SendJsonAsync(
            () => new HttpRequestMessage(HttpMethod.Post, uri) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") },
            uri, policy, cancellationToken);

    private async Task<JsonDocument> SendJsonAsync(Func<HttpRequestMessage> request, Uri uri, ProviderPolicy policy, CancellationToken cancellationToken)
    {
        if (!policy.Allows(uri))
        {
            throw new MetadataException(MetadataError.InvalidResponse, $"URL not allowed for {policy.Host}");
        }

        // One deadline for headers, body and parsing — a slow body cannot outlive it.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_deadline);
        try
        {
            using var message = request();
            using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                throw new MetadataException(MetadataError.RateLimited, policy.Host)
                {
                    RetryAfter = response.Headers.RetryAfter?.Delta,
                };
            }

            if (!response.IsSuccessStatusCode)
            {
                // Includes 3xx: redirects are never followed.
                throw new MetadataException(MetadataError.ProviderUnavailable, $"{policy.Host}: HTTP {(int)response.StatusCode}");
            }

            if (response.Content.Headers.ContentLength > MaxResponseBytes)
            {
                throw new MetadataException(MetadataError.InvalidResponse, "Response too large");
            }

            await using var body = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            // Counted after decompression, so a small compressed bomb cannot exceed the cap.
            while ((read = await body.ReadAsync(chunk, deadline.Token)) > 0)
            {
                if (buffer.Length + read > MaxResponseBytes)
                {
                    throw new MetadataException(MetadataError.InvalidResponse, "Response too large");
                }

                buffer.Write(chunk, 0, read);
            }

            buffer.Position = 0;
            deadline.Token.ThrowIfCancellationRequested();
            var document = JsonDocument.Parse(buffer, JsonOptions);
            if (deadline.IsCancellationRequested)
            {
                // Parsing itself ran past the deadline: the limit covers parsing too.
                document.Dispose();
                deadline.Token.ThrowIfCancellationRequested();
            }

            return document;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new MetadataException(MetadataError.ProviderUnavailable, $"{policy.Host}: timed out");
        }
        catch (HttpRequestException ex)
        {
            throw new MetadataException(MetadataError.ProviderUnavailable, $"{policy.Host}: {ex.Message}", ex);
        }
        catch (JsonException ex)
        {
            throw new MetadataException(MetadataError.InvalidResponse, $"{policy.Host}: malformed JSON", ex);
        }
    }

    /// <summary>The hardened handler: no redirects, decompression, public addresses only.</summary>
    public static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectCallback = ConnectToPublicAddressAsync,
    };

    /// <summary>Resolves the host and connects only to a public address (no SSRF to local services).</summary>
    private static async ValueTask<Stream> ConnectToPublicAddressAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken);
        var target = addresses.FirstOrDefault(IsPublic)
            ?? throw new HttpRequestException($"{context.DnsEndPoint.Host} does not resolve to a public address");

        var socket = new Socket(target.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(new IPEndPoint(target, context.DnsEndPoint.Port), cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    internal static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
        {
            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return !(b[0] == 10
                     || (b[0] == 172 && b[1] is >= 16 and <= 31)
                     || (b[0] == 192 && b[1] == 168)
                     || (b[0] == 169 && b[1] == 254)
                     || (b[0] == 100 && b[1] is >= 64 and <= 127)
                     || b[0] == 0
                     || b[0] >= 224);
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = address.GetAddressBytes();
            return !(address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast
                     || (b[0] & 0xFE) == 0xFC); // unique local fc00::/7
        }

        return false;
    }

    public void Dispose() => _http.Dispose();
}
