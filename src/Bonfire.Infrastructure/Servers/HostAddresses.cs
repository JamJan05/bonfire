using System.Net.NetworkInformation;
using System.Net.Sockets;
using Bonfire.Core.Servers;

namespace Bonfire.Infrastructure.Servers;

/// <summary>
/// Host interfaces through System.Net.NetworkInformation (netlink/procfs underneath, no process
/// is spawned). Returns the raw assignment: filtering for suggestions is <see cref="HostAddressRules"/>'s job.
/// </summary>
public sealed class HostAddresses : IHostAddresses
{
    public IReadOnlyList<HostInterface> Read()
    {
        NetworkInterface[] interfaces;
        try
        {
            interfaces = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (Exception ex) when (ex is NetworkInformationException or IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            throw new HostAddressException("Could not enumerate the host's network interfaces.", ex);
        }

        var result = new List<HostInterface>(interfaces.Length);
        foreach (var nic in interfaces)
        {
            try
            {
                var addresses = nic.GetIPProperties().UnicastAddresses
                    .Where(u => u.Address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
                    .Select(u => new HostAddress(Canonical(u.Address), u.Address.AddressFamily))
                    .ToList();
                // Only an explicit "down" disqualifies: WireGuard and dummy interfaces report "unknown".
                result.Add(new HostInterface(nic.Name, nic.OperationalStatus == OperationalStatus.Down, addresses));
            }
            catch (Exception ex) when (ex is NetworkInformationException or IOException)
            {
                // An interface that vanished between the list and its properties is simply absent.
            }
        }

        return result;
    }

    /// <summary>IPv6 addresses carry a scope id on link-local addresses; stored without it, like a binding.</summary>
    private static string Canonical(System.Net.IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId != 0
            ? new System.Net.IPAddress(address.GetAddressBytes()).ToString()
            : address.ToString();
}
