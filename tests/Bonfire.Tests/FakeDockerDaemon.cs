using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;

namespace Bonfire.Tests;

/// <summary>
/// Minimal HTTP/1.1 server on a Unix socket that answers like a Docker daemon.
/// The handler gets the request path and returns (status, body), or null to never answer.
/// </summary>
internal sealed class FakeDockerDaemon : IAsyncDisposable
{
    private readonly Socket _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly Func<string, (int Status, string Body)?> _handler;
    private readonly Task _loop;

    public FakeDockerDaemon(string socketPath, Func<string, (int Status, string Body)?> handler)
    {
        SocketPath = socketPath;
        _handler = handler;
        _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _listener.Bind(new UnixDomainSocketEndPoint(socketPath));
        _listener.Listen(16);
        _loop = Task.Run(AcceptLoopAsync);
    }

    public string SocketPath { get; }

    public ConcurrentQueue<string> RequestedPaths { get; } = new();

    public const string Version156 =
        """{"Version":"29.8.0","ApiVersion":"1.56","MinAPIVersion":"1.40","Os":"linux","Arch":"amd64","KernelVersion":"6.18.0"}""";

    public const string InfoSeccomp =
        """{"SecurityOptions":["name=seccomp,profile=builtin","name=cgroupns"],"CgroupVersion":"2","CgroupDriver":"cgroupfs"}""";

    /// <summary>Answers /version and /info like a healthy daemon.</summary>
    public static (int, string)? Healthy(string path) =>
        path.EndsWith("/version", StringComparison.Ordinal) ? (200, Version156)
        : path.EndsWith("/info", StringComparison.Ordinal) ? (200, InfoSeccomp)
        : (404, """{"message":"not found"}""");

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            Socket connection;
            try
            {
                connection = await _listener.AcceptAsync(_stop.Token);
            }
            catch (Exception) when (_stop.IsCancellationRequested)
            {
                return;
            }

            _ = Task.Run(() => ServeAsync(connection));
        }
    }

    private async Task ServeAsync(Socket connection)
    {
        using (connection)
        {
            try
            {
                var buffer = new byte[8192];
                var received = new StringBuilder();
                while (!received.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                {
                    var n = await connection.ReceiveAsync(buffer, _stop.Token);
                    if (n == 0)
                    {
                        return;
                    }

                    received.Append(Encoding.ASCII.GetString(buffer, 0, n));
                }

                var requestLine = received.ToString().Split("\r\n")[0];
                var path = requestLine.Split(' ')[1].Split('?')[0];
                RequestedPaths.Enqueue(path);

                if (_handler(path) is not { } response)
                {
                    await Task.Delay(Timeout.Infinite, _stop.Token);
                    return;
                }

                var body = Encoding.UTF8.GetBytes(response.Body);
                var head = $"HTTP/1.1 {response.Status} X\r\nContent-Type: application/json\r\n" +
                           $"Content-Length: {body.Length}\r\nConnection: close\r\n\r\n";
                await connection.SendAsync(Encoding.ASCII.GetBytes(head), _stop.Token);
                await connection.SendAsync(body, _stop.Token);
                connection.Shutdown(SocketShutdown.Both);
            }
            catch (Exception) when (_stop.IsCancellationRequested)
            {
            }
            catch (SocketException)
            {
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Dispose();
        await _loop;
        _stop.Dispose();
    }
}
