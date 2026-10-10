using Bonfire.Core.Servers;

namespace Bonfire.Tests;

public sealed class ServerDefinitionTests
{
    private static readonly ServerPaths Paths = new("/srv/bonfire/servers/x");
    private static readonly Guid Installation = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public void ValidDefinitionHasNoErrors() =>
        Assert.Empty(ServerDefinitionValidator.Validate(ServerTestData.Vanilla()));

    [Theory]
    [InlineData("", "Validation_NameEmpty")]
    [InlineData(" padded ", "Validation_NameInvalid")]
    [InlineData("bad\nname", "Validation_NameInvalid")]
    public void InvalidNamesAreRejected(string name, string error) =>
        Assert.Contains(error, ServerDefinitionValidator.Validate(ServerTestData.Vanilla() with { DisplayName = name }));

    [Theory]
    [InlineData(80)]
    [InlineData(1023)]
    [InlineData(65536)]
    public void PrivilegedOrOutOfRangePortsAreRejected(int port) =>
        Assert.Contains("Validation_PortInvalid", ServerDefinitionValidator.Validate(ServerTestData.Vanilla() with { Port = port }));

    [Theory]
    [InlineData("latest")]
    [InlineData("LATEST")]
    [InlineData("snapshot")]
    public void FloatingVersionAliasesAreRejected(string alias)
    {
        Assert.Contains("Validation_VersionInvalid", ServerDefinitionValidator.Validate(ServerTestData.Vanilla() with { MinecraftVersion = alias }));
        Assert.Contains("Validation_LoaderMissing", ServerDefinitionValidator.Validate(ServerTestData.Vanilla() with
        {
            Type = ServerType.Fabric, FabricLoaderVersion = alias, FabricLauncherVersion = "1.1.2",
        }));
        Assert.Throws<ArgumentException>(() => ContainerSpecBuilder.Build(
            ServerTestData.Vanilla() with { MinecraftVersion = alias }, Paths, 1000, 1000, Installation));
    }

    [Fact]
    public void HeapMinAboveMaxIsRejected() =>
        Assert.Contains("Validation_MemoryInvalid", ServerDefinitionValidator.Validate(
            ServerTestData.Vanilla() with { Resources = new ResourceLimits(2048, 1024, 1) }));

    [Fact]
    public void FabricNeedsPinnedLoaderAndLauncher() =>
        Assert.Contains("Validation_LoaderMissing", ServerDefinitionValidator.Validate(
            ServerTestData.Vanilla() with { Type = ServerType.Fabric, FabricLoaderVersion = "0.19.5" }));

    [Fact]
    public void PaperNeedsPinnedBuild() =>
        Assert.Contains("Validation_LoaderMissing", ServerDefinitionValidator.Validate(
            ServerTestData.Vanilla() with { Type = ServerType.Paper }));

    [Theory]
    [InlineData("java25", "sha256:abc")]
    [InlineData("latest", ServerTestData.Digest)]
    [InlineData("java21", ServerTestData.Digest)] // tag must match JavaMajor 25
    public void ImageMustBePinnedAndMatchJava(string tag, string digest) =>
        Assert.Contains("Validation_ImageInvalid", ServerDefinitionValidator.Validate(
            ServerTestData.Vanilla() with { Image = new ImageReference(ImageReference.ItzgRepository, tag, digest) }));

    [Fact]
    public void WhitelistEntriesMustBePlayerNamesOrUuids()
    {
        var game = new GameSettings(true, Difficulty.Easy, 10, true, ["Steve", "x;rm -rf"], null);

        Assert.Contains("Validation_WhitelistInvalid", ServerDefinitionValidator.Validate(ServerTestData.Vanilla() with { Game = game }));
    }

    [Fact]
    public void EulaMustBeAccepted() =>
        Assert.Contains("Validation_EulaRequired", ServerDefinitionValidator.Validate(ServerTestData.Vanilla() with { EulaAcceptedAt = default }));

    [Fact]
    public void SpecIsHardened()
    {
        var spec = ContainerSpecBuilder.Build(ServerTestData.Vanilla(), Paths, 1000, 1000, Installation);

        Assert.Equal("1000:1000", spec.User);
        Assert.True(spec.ReadOnlyRootfs);
        Assert.Equal(["ALL"], spec.CapDrop);
        Assert.Contains("no-new-privileges", spec.SecurityOptions);
        Assert.Equal(spec.MemoryBytes, spec.MemorySwapBytes);
        Assert.Equal(512, spec.PidsLimit);
        Assert.Contains("noexec", spec.Tmpfs["/tmp"]);
        Assert.Equal("127.0.0.1", spec.Port.HostIp);
        Assert.StartsWith("itzg/minecraft-server@sha256:", spec.Image);
        Assert.Equal(spec.Name, spec.NetworkName);
        Assert.Equal("0", spec.Environment["STOP_DURATION"]);
        Assert.Equal("/run/secrets/rcon", spec.Environment["RCON_PASSWORD_FILE"]);
        Assert.DoesNotContain("RCON_PASSWORD", spec.Environment.Keys);
    }

    [Fact]
    public void OnlyTheServerDataAndRconSecretAreMounted()
    {
        var spec = ContainerSpecBuilder.Build(ServerTestData.Vanilla(), Paths, 1000, 1000, Installation);

        Assert.Equal(
            [new BindMount("/srv/bonfire/servers/x/data", "/data", false), new BindMount("/srv/bonfire/servers/x/rcon-password", "/run/secrets/rcon", true)],
            spec.Binds);
    }

    [Fact]
    public void AllInterfacesExposureBindsEveryIpv4Address()
    {
        var spec = ContainerSpecBuilder.Build(ServerTestData.Vanilla() with { Exposure = PortExposure.AllInterfaces }, Paths, 1000, 1000, Installation);

        Assert.Equal("0.0.0.0", spec.Port.HostIp);
    }

    [Fact]
    public void ResourceNameComesFromIdNotDisplayName()
    {
        var id = Guid.NewGuid();
        var spec = ContainerSpecBuilder.Build(ServerTestData.Vanilla(id) with { DisplayName = "../../evil" }, Paths, 1000, 1000, Installation);

        Assert.Equal($"bonfire-{id:N}", spec.Name);
        Assert.Equal(id.ToString("N"), spec.Labels[ServerLabels.ServerId]);
        Assert.Equal(Installation.ToString("N"), spec.Labels[ServerLabels.Installation]);
    }

    [Fact]
    public void FabricAndPaperVersionsArePinned()
    {
        var fabric = ContainerSpecBuilder.Build(ServerTestData.Vanilla() with
        {
            Type = ServerType.Fabric, FabricLoaderVersion = "0.19.5", FabricLauncherVersion = "1.1.2",
        }, Paths, 1000, 1000, Installation);
        var paper = ContainerSpecBuilder.Build(ServerTestData.Vanilla() with
        {
            Type = ServerType.Paper, PaperBuild = 166, PaperExperimentalBuild = true,
        }, Paths, 1000, 1000, Installation);

        Assert.Equal("FABRIC", fabric.Environment["TYPE"]);
        Assert.Equal("0.19.5", fabric.Environment["FABRIC_LOADER_VERSION"]);
        Assert.Equal("1.1.2", fabric.Environment["FABRIC_LAUNCHER_VERSION"]);
        Assert.Equal("166", paper.Environment["PAPER_BUILD"]);
        Assert.Equal("experimental", paper.Environment["PAPER_CHANNEL"]);
    }

    [Fact]
    public void WhitelistUsesSeparateSwitchAndEntries()
    {
        var spec = ContainerSpecBuilder.Build(ServerTestData.Vanilla() with
        {
            Game = new GameSettings(true, Difficulty.Hard, 5, true, ["Alex", "Steve"], "-123"),
        }, Paths, 1000, 1000, Installation);

        Assert.Equal("TRUE", spec.Environment["ENABLE_WHITELIST"]);
        Assert.Equal("Alex,Steve", spec.Environment["WHITELIST"]);
        Assert.Equal("-123", spec.Environment["SEED"]);
        Assert.Equal("hard", spec.Environment["DIFFICULTY"]);
    }

    [Fact]
    public void RootIsNeverUsed() =>
        Assert.Throws<ArgumentException>(() => ContainerSpecBuilder.Build(ServerTestData.Vanilla(), Paths, 0, 0, Installation));

    [Fact]
    public void ContainerMemoryHasHeadroom()
    {
        var limits = new ResourceLimits(1024, 4096, 2);

        Assert.Equal((4096L * 5 / 4 + 256) * 1024 * 1024, limits.ContainerMemoryBytes);
    }
}
