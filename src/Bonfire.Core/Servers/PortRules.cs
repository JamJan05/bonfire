namespace Bonfire.Core.Servers;

/// <summary>Whether two host port bindings would collide.</summary>
public static class PortRules
{
    /// <summary>Ports Bonfire offers automatically, starting at the Minecraft default.</summary>
    public const int SearchStart = 25565;
    public const int SearchCount = 100;

    /// <summary>
    /// Bindings collide when the port matches and the addresses overlap: a wildcard address
    /// (0.0.0.0, ::, empty) overlaps every address; otherwise the addresses must be equal.
    /// </summary>
    public static bool Overlaps(string hostIpA, int portA, string hostIpB, int portB) =>
        portA == portB && (IsWildcard(hostIpA) || IsWildcard(hostIpB) || hostIpA == hostIpB);

    public static string HostIp(PortExposure exposure) => exposure == PortExposure.Localhost ? "127.0.0.1" : "0.0.0.0";

    private static bool IsWildcard(string ip) => ip is "" or "0.0.0.0" or "::" or "[::]";
}
