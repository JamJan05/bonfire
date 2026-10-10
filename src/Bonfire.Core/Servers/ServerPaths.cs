namespace Bonfire.Core.Servers;

/// <summary>On-disk layout of one server. Only <see cref="DataDir"/> is mounted into the container.</summary>
public sealed record ServerPaths(string ServerDir)
{
    public static ServerPaths For(AppPaths app, Guid serverId) =>
        new(Path.Combine(app.DataDir, "servers", serverId.ToString("N")));

    public string RecordFile => Path.Combine(ServerDir, "server.json");

    /// <summary>Mounted read-write as /data: world, configuration, mods and plugins.</summary>
    public string DataDir => Path.Combine(ServerDir, "data");

    /// <summary>Mounted read-only as a file; outside the data tree.</summary>
    public string RconPasswordFile => Path.Combine(ServerDir, "rcon-password");
}
