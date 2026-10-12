using System.Net.Sockets;

namespace Bonfire.Core.Servers;

/// <summary>One address assigned to a host interface, as enumerated (unfiltered).</summary>
public sealed record HostAddress(string Address, AddressFamily Family);

/// <summary>A host network interface with its addresses. <see cref="IsDown"/> is true only for an
/// interface the kernel reports as down; "unknown" (WireGuard, dummy interfaces) counts as usable.</summary>
public sealed record HostInterface(string Name, bool IsDown, IReadOnlyList<HostAddress> Addresses);

/// <summary>
/// Reads the host's interfaces and addresses. Every call enumerates afresh — presence of a VPN
/// address is checked immediately before it is relied on, never from a cached list.
/// </summary>
public interface IHostAddresses
{
    /// <exception cref="HostAddressException">The interfaces could not be enumerated.</exception>
    IReadOnlyList<HostInterface> Read();
}

public sealed class HostAddressException(string message, Exception? inner = null) : Exception(message, inner);
