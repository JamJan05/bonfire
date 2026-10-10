using Bonfire.Core.Metadata;
using Bonfire.Core.Servers;
using Bonfire.Infrastructure.Metadata;

namespace Bonfire.Tests;

/// <summary>Read-only checks against the real Mojang, Fabric and Paper APIs. Opt-in: BONFIRE_NETWORK_TESTS=1.</summary>
public sealed class RealNetworkTests
{
    [Fact]
    public async Task ResolvesAllTypesFromRealApis()
    {
        if (Environment.GetEnvironmentVariable("BONFIRE_NETWORK_TESTS") != "1")
        {
            Assert.Skip("Set BONFIRE_NETWORK_TESTS=1 to query the real metadata APIs.");
        }

        var ct = TestContext.Current.CancellationToken;
        using var http = new MetadataHttpClient(); // production handler: TLS, public-address check
        var metadata = new ServerMetadataService(http);

        var releases = await metadata.GetReleasesAsync(ct);
        Assert.NotEmpty(releases);
        var newest = releases[0].Id;

        var vanilla = await metadata.ResolveAsync(ServerType.Vanilla, newest, ct);
        var fabric = await metadata.ResolveAsync(ServerType.Fabric, newest, ct);
        ResolvedServerVersion? paper = null;
        foreach (var release in releases.Take(5))
        {
            try
            {
                paper = await metadata.ResolveAsync(ServerType.Paper, release.Id, ct);
                break;
            }
            catch (MetadataException ex) when (ex.Error == MetadataError.Unsupported)
            {
                // newest releases may not have a stable Paper build yet
            }
        }

        Assert.NotNull(paper);
        Assert.Contains(vanilla.JavaMajor, ServerDefinitionValidator.SupportedJavaMajors);
        Assert.True(ServerDefinitionValidator.IsConcreteVersion(fabric.FabricLoaderVersion));
        // Modrinth: search, pick the file of a compatible version and download it through the
        // production client (TLS, public-address check) with SHA-512 verification.
        var modrinth = new Bonfire.Infrastructure.Content.ModrinthClient(http);
        var profile = new Bonfire.Core.Content.ContentProfile("mods", ["fabric"], newest, ServerType.Fabric);
        var hits = await modrinth.SearchAsync("lithium", profile, Bonfire.Core.Content.SearchSort.Relevance, 0, ct);
        var lithium = hits.Hits.First(h => h.Slug == "lithium");
        var versions = await modrinth.GetCompatibleVersionsAsync(lithium.Id, profile, ct);
        var file = Bonfire.Core.Content.ContentRules.SelectFile(versions.First(v => v.IsRelease))!;
        using var temp = new TempDirectory();
        using (var downloader = new Bonfire.Infrastructure.Content.ContentDownloader())
        {
            await downloader.DownloadAsync(file, Path.Combine(temp.Path, "lithium.jar"), null, ct);
        }

        var identified = await modrinth.GetVersionsByHashAsync([file.Sha512], ct);
        Assert.Equal(lithium.Id, identified[file.Sha512].ProjectId);

        TestContext.Current.TestOutputHelper?.WriteLine(
            $"Lithium {file.FileName} ({file.Size} B) downloaded and verified; " +
            $"Vanilla {vanilla.MinecraftVersion} Java {vanilla.JavaMajor}; Fabric loader {fabric.FabricLoaderVersion} installer {fabric.FabricLauncherVersion}; " +
            $"Paper {paper!.MinecraftVersion} build {paper.PaperBuild} Java {paper.JavaMajor}");
    }
}
