using System.Net;
using System.Net.Sockets;
using Bonfire.Core.Docker;
using Docker.DotNet;

namespace Bonfire.Infrastructure.Docker;

/// <summary>Maps transport and API exceptions to a connection status using typed errors, never message text.</summary>
internal static class DockerErrorClassifier
{
    private const int ENOENT = 2;
    private const int EACCES = 13;
    private const int ECONNREFUSED = 111;
    private const int MaxDetailLength = 400;

    public static DockerConnectionStatus Classify(Exception exception)
    {
        if (exception is DockerApiException api)
        {
            return api.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                ? DockerConnectionStatus.ApiAccessDenied
                : DockerConnectionStatus.Failed;
        }

        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SocketException socket)
            {
                return socket.SocketErrorCode switch
                {
                    SocketError.AccessDenied => DockerConnectionStatus.PermissionDenied,
                    SocketError.AddressNotAvailable => DockerConnectionStatus.SocketMissing,
                    SocketError.ConnectionRefused => DockerConnectionStatus.DaemonNotResponding,
                    _ => socket.NativeErrorCode switch
                    {
                        EACCES => DockerConnectionStatus.PermissionDenied,
                        ENOENT => DockerConnectionStatus.SocketMissing,
                        ECONNREFUSED => DockerConnectionStatus.DaemonNotResponding,
                        _ => DockerConnectionStatus.DaemonNotResponding,
                    },
                };
            }
        }

        return exception is HttpRequestException or IOException
            ? DockerConnectionStatus.DaemonNotResponding
            : DockerConnectionStatus.Failed;
    }

    /// <summary>Short technical description: exception types and messages, bounded in length.</summary>
    public static string Describe(Exception exception)
    {
        var parts = new List<string>();
        for (var current = exception; current is not null && parts.Count < 3; current = current.InnerException)
        {
            parts.Add(current is DockerApiException api
                ? $"DockerApiException: HTTP {(int)api.StatusCode}"
                : $"{current.GetType().Name}: {current.Message}");
        }

        var text = string.Join(" → ", parts);
        return text.Length <= MaxDetailLength ? text : text[..MaxDetailLength] + "…";
    }
}
