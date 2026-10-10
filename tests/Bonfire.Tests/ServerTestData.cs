using Bonfire.Core.Servers;

namespace Bonfire.Tests;

internal static class ServerTestData
{
    public const string Digest = "sha256:46cfd0bcbd9bbd16900a765fcdd5d6f57bc0cf3471c383ad96f87e84998d93e8";

    public static ServerDefinition Vanilla(Guid? id = null) => new(
        Id: id ?? Guid.NewGuid(),
        DisplayName: "Test world",
        Type: ServerType.Vanilla,
        MinecraftVersion: "26.3",
        FabricLoaderVersion: null,
        FabricLauncherVersion: null,
        PaperBuild: null,
        PaperExperimentalBuild: false,
        JavaMajor: 25,
        Image: new ImageReference(ImageReference.ItzgRepository, "java25", Digest),
        Resources: new ResourceLimits(512, 1024, 1),
        Port: 25599,
        Exposure: PortExposure.Localhost,
        RestartPolicy: RestartPolicyKind.No,
        Game: new GameSettings(true, Difficulty.Normal, 10, false, [], null),
        EulaAcceptedAt: DateTimeOffset.UtcNow,
        CreatedAt: DateTimeOffset.UtcNow);
}
