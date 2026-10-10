namespace Bonfire.Core.Docker;

/// <summary>Docker Engine API version negotiation.</summary>
public static class DockerApiVersion
{
    /// <summary>Oldest API version Bonfire supports (Docker Engine 25).</summary>
    public static Version ClientMinimum { get; } = new(1, 44);

    /// <summary>Newest API version Bonfire has been tested against.</summary>
    public static Version ClientMaximum { get; } = new(1, 56);

    /// <summary>
    /// Picks the highest version supported by both sides, or null when the ranges do not
    /// overlap. The daemon's range is <paramref name="daemonMinimum"/>..<paramref name="daemonMaximum"/>.
    /// </summary>
    public static Version? Negotiate(Version daemonMinimum, Version daemonMaximum)
    {
        ArgumentNullException.ThrowIfNull(daemonMinimum);
        ArgumentNullException.ThrowIfNull(daemonMaximum);

        var candidate = daemonMaximum < ClientMaximum ? daemonMaximum : ClientMaximum;
        var floor = daemonMinimum > ClientMinimum ? daemonMinimum : ClientMinimum;
        return candidate >= floor ? candidate : null;
    }
}
