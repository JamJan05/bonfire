using System.Net.Sockets;
using Bonfire.Core.Servers;

namespace Bonfire.Tests;

public sealed class HostAddressRulesTests
{
    [Theory]
    [InlineData("10.8.0.1", "10.8.0.1")]
    [InlineData("  192.168.1.20 ", "192.168.1.20")]
    [InlineData("fd00::1", "fd00::1")]
    [InlineData("FD00:0:0:0:0:0:0:1", "fd00::1")]
    [InlineData("::ffff:10.8.0.1", "10.8.0.1")] // IPv4-mapped IPv6 names an IPv4 address
    public void CanonicalizesAcceptedSpellings(string input, string expected)
    {
        Assert.True(HostAddressRules.TryCanonicalize(input, out var canonical));
        Assert.Equal(expected, canonical);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("10.8.0")]          // shorthand IPv4
    [InlineData("010.8.0.1")]       // octal-looking octet
    [InlineData("0x0a.8.0.1")]
    [InlineData("256.1.1.1")]
    [InlineData("10.8.0.١")]   // an Arabic-Indic digit: matches \d, not an address
    [InlineData("167772161")]       // integer form
    [InlineData("[fd00::1]")]       // brackets belong to endpoints, not addresses
    [InlineData("fe80::1%wlan0")]   // scope id
    [InlineData("10.8.0.1/24")]
    [InlineData("vpn.example")]
    public void RejectsOtherSpellings(string? input) => Assert.False(HostAddressRules.TryCanonicalize(input, out _));

    [Theory]
    [InlineData("10.8.0.1", true)]
    [InlineData("100.64.5.6", true)]
    [InlineData("192.168.1.20", true)]
    [InlineData("8.8.8.8", true)]
    [InlineData("fd00::1", true)]
    [InlineData("2001:db8::20", true)]
    [InlineData("0.0.0.0", false)]
    [InlineData("0.1.2.3", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("127.5.5.5", false)]
    [InlineData("169.254.1.1", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("255.255.255.255", false)]
    [InlineData("::", false)]
    [InlineData("::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("ff02::1", false)]
    [InlineData("::ffff:10.8.0.1", false)] // must be canonicalized first
    public void BindableAddressesAreUnicastAndNotLoopbackOrWildcard(string address, bool bindable) =>
        Assert.Equal(bindable, HostAddressRules.IsBindable(address));

    [Fact]
    public void EqualityComparesAddressesNotText()
    {
        Assert.True(HostAddressRules.AreEqual("fd00::1", "fd00:0:0:0:0:0:0:1"));
        Assert.True(HostAddressRules.AreEqual("10.8.0.1", "10.8.0.1"));
        Assert.False(HostAddressRules.AreEqual("10.8.0.1", "10.8.0.2"));
        Assert.False(HostAddressRules.AreEqual("not-an-address", "10.8.0.1"));
        Assert.True(HostAddressRules.AreEqual("not-an-address", "not-an-address"));
    }

    [Fact]
    public void PresenceIgnoresInterfaceStateAndSpelling()
    {
        var down = new HostInterface("wg0", IsDown: true, [new HostAddress("fd00::1", AddressFamily.InterNetworkV6)]);
        var interfaces = new[] { FakeHostAddresses.Interface("lo", "127.0.0.1"), down };

        Assert.True(HostAddressRules.IsPresent(interfaces, "fd00:0:0:0:0:0:0:1"));
        Assert.Same(down, HostAddressRules.Find(interfaces, "fd00::1"));
        Assert.False(HostAddressRules.IsPresent(interfaces, "10.8.0.1"));
        Assert.Null(HostAddressRules.Find(interfaces, "10.8.0.1"));
    }

    [Fact]
    public void SuggestionsSkipLoopbackLinkLocalDockerAndDownInterfaces()
    {
        var hosts = FakeHostAddresses.Typical();
        hosts.Interfaces.Add(new HostInterface("eth0", IsDown: true, [new HostAddress("10.0.0.5", AddressFamily.InterNetwork)]));
        hosts.Interfaces.Add(FakeHostAddresses.Interface("br-0bead698ad9f", "172.19.0.1"));
        hosts.Interfaces.Add(FakeHostAddresses.Interface("br0", "10.0.1.1")); // a real bridge keeps its address
        hosts.Interfaces.Add(FakeHostAddresses.Interface("veth1a2b3c", "10.0.2.1"));
        hosts.Interfaces.Add(FakeHostAddresses.Interface("virbr0", "192.168.122.1"));
        hosts.Interfaces.Add(FakeHostAddresses.Interface("tailscale0", "100.101.102.103", "fd7a:115c:a1e0::1"));

        var suggestions = HostAddressRules.Suggestions(hosts.Interfaces);

        Assert.Equal(
            ["100.101.102.103", "10.8.0.1", "fd7a:115c:a1e0::1", "10.0.1.1", "192.168.1.20", "2001:db8::20"],
            suggestions.Select(s => s.Address));
        Assert.Equal([true, true, true, false, false, false], suggestions.Select(s => s.LooksLikeVpn));
        Assert.Equal("wg0", suggestions[1].Interface);
    }

    [Theory]
    [InlineData("wg0", "10.8.0.1", true)]
    [InlineData("tailscale0", "100.100.1.1", true)]
    [InlineData("zt1abc", "10.147.0.1", true)]
    [InlineData("tun0", "10.9.0.2", true)]
    [InlineData("eth0", "100.64.0.1", true)]    // CGNAT range: Tailscale-like
    [InlineData("eth0", "100.127.255.255", true)]
    [InlineData("eth0", "100.128.0.1", false)]
    [InlineData("wlan0", "192.168.1.20", false)]
    [InlineData("wgx", "192.168.1.20", true)]   // name prefix alone is enough for a hint
    public void VpnHeuristicsAreNameOrRangeBased(string iface, string address, bool vpn) =>
        Assert.Equal(vpn, HostAddressRules.LooksLikeVpn(iface, address));

    [Theory]
    [InlineData("docker0", true)]
    [InlineData("docker1", true)]
    [InlineData("br-0bead698ad9f", true)]
    [InlineData("br-short", false)]
    [InlineData("br0", false)]
    [InlineData("vethabc123", true)]
    [InlineData("virbr0", true)]
    [InlineData("wlan0", false)]
    public void DockerInterfacesAreRecognisedByTheirNamingScheme(string name, bool virtualIface) =>
        Assert.Equal(virtualIface, HostAddressRules.IsVirtualInterface(name));

    [Fact]
    public void EndpointsBracketIpv6()
    {
        Assert.Equal("10.8.0.1:25565", HostAddressRules.Endpoint("10.8.0.1", 25565));
        Assert.Equal("[fd00::1]:25565", HostAddressRules.Endpoint("fd00::1", 25565));
    }
}
