namespace Bonfire.Core.Docker;

/// <summary>A local Docker Engine reachable through a Unix domain socket.</summary>
public sealed record DockerEndpoint(string SocketPath)
{
    public const string DefaultSocketPath = "/var/run/docker.sock";

    public Uri Uri => new($"unix://{SocketPath}");

    public override string ToString() => $"unix://{SocketPath}";

    /// <summary>
    /// Resolves the endpoint from <c>DOCKER_HOST</c>. Only local Unix sockets are supported:
    /// remote engines would make bind-mount paths refer to another machine. Docker contexts are
    /// deliberately ignored for the same reason. The raw variable is never returned, because it
    /// may contain credentials.
    /// </summary>
    public static DockerEndpoint? Resolve(Func<string, string?> getEnvironmentVariable)
    {
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);

        var host = getEnvironmentVariable("DOCKER_HOST");
        if (string.IsNullOrEmpty(host))
        {
            return new DockerEndpoint(DefaultSocketPath);
        }

        const string scheme = "unix://";
        if (!host.StartsWith(scheme, StringComparison.Ordinal))
        {
            return null;
        }

        var path = host[scheme.Length..];

        // An absolute path only: no authority, query, fragment, percent-encoding or NUL.
        if (path.Length < 2
            || path[0] != '/'
            || path.StartsWith("//", StringComparison.Ordinal)
            || path.IndexOfAny(['?', '#', '%', '\0']) >= 0)
        {
            return null;
        }

        return new DockerEndpoint(path);
    }
}
