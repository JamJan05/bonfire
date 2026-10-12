namespace Bonfire.Core.Servers;

/// <summary>Whether two host port bindings would collide, and which host address a definition binds.</summary>
public static class PortRules
{
    /// <summary>Ports Bonfire offers automatically, starting at the Minecraft default.</summary>
    public const int SearchStart = 25565;
    public const int SearchCount = 100;

    /// <summary>
    /// Bindings collide when the port matches and the addresses overlap: a wildcard address
    /// (0.0.0.0, ::, empty) overlaps every address; otherwise the addresses must be the same
    /// address (compared as addresses, so two spellings of one IPv6 address collide).
    /// </summary>
    public static bool Overlaps(string hostIpA, int portA, string hostIpB, int portB) =>
        portA == portB && (IsWildcard(hostIpA) || IsWildcard(hostIpB) || HostAddressRules.AreEqual(hostIpA, hostIpB));

    /// <summary>The host address the port is published on. Exhaustive: an unknown mode never becomes 0.0.0.0.</summary>
    public static string HostIp(PortExposure exposure, string? hostAddress) => exposure switch
    {
        PortExposure.Localhost => "127.0.0.1",
        PortExposure.AllInterfaces => "0.0.0.0",
        PortExposure.SpecificAddress => hostAddress ?? throw new ArgumentException("A specific address requires HostAddress.", nameof(hostAddress)),
        _ => throw new ArgumentOutOfRangeException(nameof(exposure)),
    };

    public static string HostIp(ServerDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return HostIp(definition.Exposure, definition.HostAddress);
    }

    /// <summary>The address players type, as host:port (IPv6 in brackets).</summary>
    public static string Endpoint(ServerDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return HostAddressRules.Endpoint(HostIp(definition), definition.Port);
    }

    private static bool IsWildcard(string ip) => ip is "" or "0.0.0.0" or "::" or "[::]";
}
