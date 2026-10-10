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

    public async Task<PortCheck> CheckAsync(PortExposure exposure, int port, Guid? ignoreServerId, CancellationToken cancellationToken)
    {
        var hostIp = PortRules.HostIp(exposure);

        var ours = (await Task.Run(_registry.LoadAll, cancellationToken))
            .Select(e => e.Record?.Definition)
            .OfType<ServerDefinition>()
            .Where(d => d.Id != ignoreServerId);
        if (ours.Any(d => PortRules.Overlaps(hostIp, port, PortRules.HostIp(d.Exposure), d.Port)))
        {
            return PortCheck.UsedByBonfireServer;
        }

        IReadOnlyList<PortBinding> published;
        try
        {
            published = await _runtime.ListPublishedPortsAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return PortCheck.Unknown;
        }

        if (published.Any(b => b.Protocol == "tcp" && PortRules.Overlaps(hostIp, port, b.HostIp, b.HostPort)))
        {
            return PortCheck.UsedByContainer;
        }

        return TryBind(hostIp, port);
    }

    public async Task<int?> FindFreeAsync(PortExposure exposure, CancellationToken cancellationToken)
    {
        for (var port = PortRules.SearchStart; port < PortRules.SearchStart + PortRules.SearchCount; port++)
        {
            if (await CheckAsync(exposure, port, null, cancellationToken) == PortCheck.Free)
            {
                return port;
            }
        }

        return null;
    }

    internal static PortCheck TryBind(string hostIp, int port)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Parse(hostIp), port);
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
            return PortCheck.Unknown; // e.g. permission problems: not proof of anything
        }
    }
}
