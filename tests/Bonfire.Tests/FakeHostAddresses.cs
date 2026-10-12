using System.Net.Sockets;
using Bonfire.Core.Servers;

namespace Bonfire.Tests;

/// <summary>In-memory host interfaces; every Read() counts, so tests can assert the enumeration is fresh.</summary>
internal sealed class FakeHostAddresses : IHostAddresses
{
    public List<HostInterface> Interfaces { get; } = [];

    public int Reads { get; private set; }

    public bool Fail { get; set; }

    /// <summary>Runs inside Read(), i.e. while the start guard is enumerating (for interleaving tests).</summary>
    public Action? OnRead { get; set; }

    public IReadOnlyList<HostInterface> Read()
    {
        Reads++;
        OnRead?.Invoke();
        return Fail ? throw new HostAddressException("netlink unavailable") : Interfaces.ToList();
    }

    public static HostInterface Interface(string name, params string[] addresses) => new(
        name,
        IsDown: false,
        addresses.Select(a => new HostAddress(a, a.Contains(':') ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork)).ToList());

    /// <summary>A typical desktop: loopback, a LAN interface with IPv4 + IPv6, a WireGuard interface and Docker's bridge.</summary>
    public static FakeHostAddresses Typical()
    {
        var fake = new FakeHostAddresses();
        fake.Interfaces.Add(Interface("lo", "127.0.0.1", "::1"));
        fake.Interfaces.Add(Interface("wlan0", "192.168.1.20", "fe80::1", "2001:db8::20"));
        fake.Interfaces.Add(Interface("wg0", "10.8.0.1"));
        fake.Interfaces.Add(Interface("docker0", "172.17.0.1"));
        return fake;
    }
}
