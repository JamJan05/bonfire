using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace Bonfire.Core.Servers;

/// <summary>An address the user may publish the game port on, with hints for the list.</summary>
/// <param name="LooksLikeVpn">A hint from the interface name or address range only — never security evidence.</param>
public sealed record AddressSuggestion(string Address, AddressFamily Family, string Interface, bool LooksLikeVpn);

/// <summary>
/// Pure rules for host addresses: which literals may become a port binding, how two spellings
/// compare, whether an address is currently assigned, and which addresses are worth suggesting.
/// </summary>
public static partial class HostAddressRules
{
    // ASCII digits only: .NET's \d also matches other scripts' digits, which IPAddress would then reject.
    [GeneratedRegex(@"^(25[0-5]|2[0-4][0-9]|1[0-9][0-9]|[1-9]?[0-9])(\.(25[0-5]|2[0-4][0-9]|1[0-9][0-9]|[1-9]?[0-9])){3}\z")]
    private static partial Regex DottedQuad();

    /// <summary>Docker's bridge names: docker0 and br-&lt;12 hex digits&gt;; veth pairs; libvirt's virbr.</summary>
    [GeneratedRegex(@"^(docker\d+|br-[0-9a-f]{12}|veth[0-9A-Za-z]*|virbr\d+)\z")]
    private static partial Regex VirtualInterface();

    [GeneratedRegex(@"^(tailscale|wg|zt|tun|nebula)[0-9A-Za-z_-]*\z")]
    private static partial Regex VpnInterface();

    /// <summary>
    /// Parses an address literal into its canonical form: trimmed, IPv4 only as a plain dotted
    /// quad (no octal, hex or shorthand spellings), IPv6 without brackets or a scope id, an
    /// IPv4-mapped IPv6 address reduced to the IPv4 it names.
    /// </summary>
    public static bool TryCanonicalize(string? input, out string canonical)
    {
        canonical = "";
        if (input is null)
        {
            return false;
        }

        var text = input.Trim();
        if (text.Length == 0 || text.Length > 64 || text.Contains('%') || text.Contains('[') || text.Contains(']') || text.Contains('/'))
        {
            return false;
        }

        if (!text.Contains(':'))
        {
            if (!DottedQuad().IsMatch(text) || !IPAddress.TryParse(text, out var v4) || v4.AddressFamily != AddressFamily.InterNetwork)
            {
                return false;
            }

            canonical = v4.ToString();
            return true;
        }

        if (!IPAddress.TryParse(text, out var parsed) || parsed.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return false;
        }

        if (parsed.IsIPv4MappedToIPv6)
        {
            parsed = parsed.MapToIPv4();
        }

        canonical = parsed.ToString();
        return true;
    }

    /// <summary>
    /// Whether a canonical address may be used as the port's host address: a unicast address that
    /// a VPN or LAN interface can carry. Wildcards, loopback, link-local, multicast and broadcast
    /// are refused; loopback and "all interfaces" have their own exposure modes.
    /// </summary>
    public static bool IsBindable(string canonical)
    {
        if (!IPAddress.TryParse(canonical, out var ip))
        {
            return false;
        }

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] != 0 && b[0] != 127 && !(b[0] == 169 && b[1] == 254) && b[0] < 224;
        }

        return ip.AddressFamily == AddressFamily.InterNetworkV6
            && !ip.Equals(IPAddress.IPv6Any)
            && !ip.Equals(IPAddress.IPv6Loopback)
            && !ip.IsIPv6LinkLocal
            && !ip.IsIPv6Multicast
            && !ip.IsIPv4MappedToIPv6
            && !ip.IsIPv6SiteLocal;
    }

    /// <summary>Two spellings of the same address compare equal; unparseable text only equals itself.</summary>
    public static bool AreEqual(string a, string b) =>
        IPAddress.TryParse(a, out var x) && IPAddress.TryParse(b, out var y) ? x.Equals(y) : string.Equals(a, b, StringComparison.Ordinal);

    /// <summary>Assigned to any interface, up or not: the kernel can bind it either way.</summary>
    public static bool IsPresent(IEnumerable<HostInterface> interfaces, string address) =>
        interfaces.Any(i => i.Addresses.Any(a => AreEqual(a.Address, address)));

    /// <summary>The interface carrying the address, or null.</summary>
    public static HostInterface? Find(IEnumerable<HostInterface> interfaces, string address) =>
        interfaces.FirstOrDefault(i => i.Addresses.Any(a => AreEqual(a.Address, address)));

    /// <summary>host:port, with IPv6 in brackets, as a Minecraft client expects it.</summary>
    public static string Endpoint(string address, int port) =>
        address.Contains(':') ? $"[{address}]:{port}" : $"{address}:{port}";

    public static bool IsLinkLocal(string address) =>
        IPAddress.TryParse(address, out var ip)
        && (ip.IsIPv6LinkLocal || (ip.AddressFamily == AddressFamily.InterNetwork && ip.GetAddressBytes() is [169, 254, ..]));

    public static bool IsLoopback(string address) => IPAddress.TryParse(address, out var ip) && IPAddress.IsLoopback(ip);

    public static bool IsVirtualInterface(string name) => VirtualInterface().IsMatch(name);

    /// <summary>Interface names of common overlay VPNs, or the CGNAT range Tailscale uses (100.64.0.0/10).</summary>
    public static bool LooksLikeVpn(string interfaceName, string address)
    {
        if (VpnInterface().IsMatch(interfaceName))
        {
            return true;
        }

        return IPAddress.TryParse(address, out var ip)
            && ip.AddressFamily == AddressFamily.InterNetwork
            && ip.GetAddressBytes() is [100, var second, ..] && second is >= 64 and <= 127;
    }

    /// <summary>
    /// Addresses worth offering as a binding or telling players: bindable, on an interface that is
    /// not down and not a Docker/libvirt bridge. VPN-looking addresses first, then IPv4 before IPv6.
    /// </summary>
    public static IReadOnlyList<AddressSuggestion> Suggestions(IEnumerable<HostInterface> interfaces) =>
        interfaces
            .Where(i => !i.IsDown && !IsVirtualInterface(i.Name))
            .SelectMany(i => i.Addresses
                .Where(a => TryCanonicalize(a.Address, out var c) && IsBindable(c))
                .Select(a => new AddressSuggestion(a.Address, a.Family, i.Name, LooksLikeVpn(i.Name, a.Address))))
            .OrderByDescending(s => s.LooksLikeVpn)
            .ThenBy(s => s.Family == AddressFamily.InterNetwork ? 0 : 1)
            .ThenBy(s => s.Interface, StringComparer.Ordinal)
            .ThenBy(s => s.Address, StringComparer.Ordinal)
            .ToList();
}
