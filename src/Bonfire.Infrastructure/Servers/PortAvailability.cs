using System.Net;
using System.Net.Sockets;
using Bonfire.Core.Servers;

namespace Bonfire.Infrastructure.Servers;

/// <summary>Checks Bonfire's own servers, every Docker container and finally the host itself.</summary>
public sealed class PortAvailability : IPortAvailability
{
    private readonly ServerRegistry _registry;
    private readonly IServerRuntime _runtime;

    public PortAvailability(ServerRegistry registry, IServerRuntime runtime)
    {
        _registry = registry;
        _runtime = runtime;
    }

    public async Task<PortCheck> CheckAsync(string hostIp, int port, PortCheckScope scope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var ours = (await Task.Run(_registry.LoadAll, cancellationToken))
            .Select(e => e.Record?.Definition)
            .OfType<ServerDefinition>()
            .Where(d => d.Id != scope.IgnoreServerId);
        if (ours.Any(d => PortRules.Overlaps(hostIp, port, PortRules.HostIp(d), d.Port)))
        {
            return PortCheck.UsedByBonfireServer;
        }

        IReadOnlyList<PublishedPort> published;
        try
        {
            published = await _runtime.ListPublishedPortsAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return PortCheck.Unknown;
        }

        // Only the edited server's own container is ignored — never every binding that happens to match.
        if (published.Any(p => p.ContainerId != scope.IgnoreContainerId
                               && p.Binding.Protocol == "tcp"
                               && PortRules.Overlaps(hostIp, port, p.Binding.HostIp, p.Binding.HostPort)))
        {
            return PortCheck.UsedByContainer;
        }

        return scope.ProbeHost ? TryBind(hostIp, port) : PortCheck.Free;
    }

    public async Task<int?> FindFreeAsync(string hostIp, CancellationToken cancellationToken)
    {
        for (var port = PortRules.SearchStart; port < PortRules.SearchStart + PortRules.SearchCount; port++)
        {
            if (await CheckAsync(hostIp, port, PortCheckScope.All, cancellationToken) == PortCheck.Free)
            {
                return port;
            }
        }

        return null;
    }

    internal static PortCheck TryBind(string hostIp, int port)
    {
        if (!IPAddress.TryParse(hostIp, out var address))
        {
            return PortCheck.Unknown;
        }

        try
        {
            using var listener = new TcpListener(address, port);
            listener.Server.ExclusiveAddressUse = true;
            listener.Start();
            return PortCheck.Free;
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            return PortCheck.UsedByHost;
        }
        catch (SocketException)
        {
            return PortCheck.Unknown; // e.g. permission problems or an address not assigned: not proof of anything
        }
    }
}
