using System.Net.Sockets;
using Bonfire.Core.Docker;
using Docker.DotNet;
using Docker.DotNet.Unix;

namespace Bonfire.Infrastructure.Docker;

/// <summary>
/// A negotiated connection to the local daemon: a versioned Docker.DotNet client plus a raw
/// HTTP client for the few calls the library cannot express (e.g. stop with <c>t=-1</c>).
/// </summary>
public sealed class DockerConnection : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DockerClient? _client;
    private HttpClient? _raw;
    private Version? _apiVersion;

    public DockerConnection(DockerEndpoint endpoint) => Endpoint = endpoint;

    public DockerEndpoint Endpoint { get; }

    public async Task<DockerClient> GetClientAsync(CancellationToken cancellationToken)
    {
        await EnsureAsync(cancellationToken);
        return _client!;
    }

    /// <summary>HTTP client whose base address already contains the negotiated API version.</summary>
    public async Task<HttpClient> GetRawClientAsync(CancellationToken cancellationToken)
    {
        await EnsureAsync(cancellationToken);
        return _raw!;
    }

    private async Task EnsureAsync(CancellationToken cancellationToken)
    {
        if (_client is not null)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_client is not null)
            {
                return;
            }

            using var probe = new DockerClientBuilder().WithEndpoint(Endpoint.Uri)
                .WithTransportOptions(new UnixSocketTransportOptions()).Build();
            var version = await probe.System.GetVersionAsync(cancellationToken);
            _apiVersion = Version.TryParse(version.MinAPIVersion, out var min) && Version.TryParse(version.APIVersion, out var max)
                ? DockerApiVersion.Negotiate(min, max)
                : null;
            if (_apiVersion is null)
            {
                throw new InvalidOperationException(
                    $"Docker API {version.MinAPIVersion}–{version.APIVersion} is not compatible with Bonfire.");
            }

            _raw = new HttpClient(new SocketsHttpHandler
            {
                ConnectCallback = async (_, ct) =>
                {
                    var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    try
                    {
                        await socket.ConnectAsync(new UnixDomainSocketEndPoint(Endpoint.SocketPath), ct);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                },
            })
            {
                BaseAddress = new Uri($"http://docker/v{_apiVersion}/"),
                Timeout = Timeout.InfiniteTimeSpan, // callers bound waits with their tokens
            };

            _client = new DockerClientBuilder().WithEndpoint(Endpoint.Uri).WithApiVersion(_apiVersion)
                .WithTransportOptions(new UnixSocketTransportOptions()).Build();
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _client?.Dispose();
        _raw?.Dispose();
        _gate.Dispose();
    }
}
