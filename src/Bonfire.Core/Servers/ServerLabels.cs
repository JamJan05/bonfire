namespace Bonfire.Core.Servers;

/// <summary>Docker labels identifying resources that belong to this Bonfire installation.</summary>
public static class ServerLabels
{
    private const string Prefix = "io.github.jamjan05.bonfire.";

    public const string App = Prefix + "app";
    public const string AppValue = "bonfire";
    public const string Installation = Prefix + "installation";
    public const string ServerId = Prefix + "server-id";
    public const string Schema = Prefix + "schema";

    /// <summary>Version of the container specification layout (label value); not the server.json format.</summary>
    public const string SpecVersion = "1";
    public const string HardeningProfile = Prefix + "hardening";

    /// <summary>Version of the container hardening profile; bump when <see cref="ContainerSpecBuilder"/> changes it.</summary>
    public const string HardeningProfileVersion = "1";
}
