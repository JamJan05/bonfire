using System.Net;
using Bonfire.Core.Metadata;
using Bonfire.Core.Servers;
using Bonfire.Infrastructure.Metadata;

namespace Bonfire.Tests;

public sealed class MetadataTests
{
    private const string Manifest = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
    private const string V263 = "https://piston-meta.mojang.com/v1/packages/aaa/26.3.json";
    private const string V262 = "https://piston-meta.mojang.com/v1/packages/bbb/26.2.json";
    private const string V1122 = "https://piston-meta.mojang.com/v1/packages/ccc/1.12.2.json";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static FakeHttp Standard()
    {
        var http = new FakeHttp();
        http.Json(Manifest, $$"""
            {"latest":{"release":"26.3"},"versions":[
              {"id":"26.4-snapshot-3","type":"snapshot","url":"https://piston-meta.mojang.com/v1/packages/x/s.json","releaseTime":"2026-10-01T00:00:00+00:00"},
              {"id":"26.2","type":"release","url":"{{V262}}","releaseTime":"2026-06-01T00:00:00+00:00"},
              {"id":"26.3","type":"release","url":"{{V263}}","releaseTime":"2026-09-01T00:00:00+00:00"},
              {"id":"evil","type":"release","url":"https://evil.example/v.json","releaseTime":"2026-09-02T00:00:00+00:00"},
              {"id":"latest","type":"release","url":"https://piston-meta.mojang.com/v1/packages/y/l.json","releaseTime":"2026-09-03T00:00:00+00:00"},
              {"id":"1.12.2","type":"release","url":"{{V1122}}","releaseTime":"2017-09-18T00:00:00+00:00"}
            ]}
            """);
        http.Json(V263, """{"javaVersion":{"component":"java-runtime-epsilon","majorVersion":25}}""");
        http.Json(V262, """{"javaVersion":{"majorVersion":25}}""");
        http.Json(V1122, """{"id":"1.12.2"}""");
        http.Json("https://meta.fabricmc.net/v2/versions/game", """[{"version":"26.3","stable":true},{"version":"26.2","stable":true}]""");
        http.Json("https://meta.fabricmc.net/v2/versions/loader/26.3", """
            [{"loader":{"version":"0.20.0-beta.1","stable":false}},{"loader":{"version":"latest","stable":true}},{"loader":{"version":"0.19.5","stable":true}}]
            """);
        http.Json("https://meta.fabricmc.net/v2/versions/installer", """[{"version":"1.2.0","stable":false},{"version":"1.1.2","stable":true}]""");
        http.Json("https://fill.papermc.io/v3/projects/paper/versions/26.3", """{"version":{"id":"26.3","support":{"status":"SUPPORTED"},"java":{"version":{"minimum":25}}}}""");
        http.Json("https://fill.papermc.io/v3/projects/paper/versions/26.3/builds", """[{"id":166,"channel":"BETA"},{"id":150,"channel":"ALPHA"}]""");
        http.Json("https://fill.papermc.io/v3/projects/paper/versions/26.2", """{"version":{"id":"26.2","support":{"status":"UNSUPPORTED"},"java":{"version":{"minimum":25}}}}""");
        http.Json("https://fill.papermc.io/v3/projects/paper/versions/26.2/builds", """[{"id":90,"channel":"STABLE"},{"id":95,"channel":"BETA"},{"id":88,"channel":"STABLE"},{"id":99,"channel":"SPECIAL"}]""");
        return http;
    }

    private static ServerMetadataService Service(FakeHttp http) => new(new MetadataHttpClient(http));

    [Fact]
    public async Task ReleasesExcludeSnapshotsUntrustedUrlsAndAliasesNewestFirst()
    {
        var releases = await Service(Standard()).GetReleasesAsync(Ct);

        Assert.Equal(["26.3", "26.2", "1.12.2"], releases.Select(r => r.Id));
    }

    [Fact]
    public async Task VanillaUsesMojangJava()
    {
        var resolved = await Service(Standard()).ResolveAsync(ServerType.Vanilla, "26.3", Ct);

        Assert.Equal(25, resolved.JavaMajor);
    }

    [Fact]
    public async Task MissingJavaRequirementIsNotGuessed()
    {
        var error = await Assert.ThrowsAsync<MetadataException>(() => Service(Standard()).ResolveAsync(ServerType.Vanilla, "1.12.2", Ct));

        Assert.Equal(MetadataError.Unsupported, error.Error);
    }

    [Fact]
    public async Task JavaWithoutAnExactImageIsNotRoundedUp()
    {
        var http = Standard();
        http.Json(V263, """{"javaVersion":{"majorVersion":22}}""");

        var error = await Assert.ThrowsAsync<MetadataException>(() => Service(http).ResolveAsync(ServerType.Vanilla, "26.3", Ct));

        Assert.Equal(MetadataError.Unsupported, error.Error);
    }

    [Fact]
    public async Task FabricPinsNewestStableLoaderAndInstallerSkippingAliases()
    {
        var resolved = await Service(Standard()).ResolveAsync(ServerType.Fabric, "26.3", Ct);

        Assert.Equal("0.19.5", resolved.FabricLoaderVersion);
        Assert.Equal("1.1.2", resolved.FabricLauncherVersion);
    }

    [Fact]
    public async Task FabricMustListTheGameVersion()
    {
        var http = Standard();
        http.Json("https://meta.fabricmc.net/v2/versions/game", """[{"version":"26.2","stable":true}]""");

        var error = await Assert.ThrowsAsync<MetadataException>(() => Service(http).ResolveAsync(ServerType.Fabric, "26.3", Ct));

        Assert.Equal(MetadataError.Unsupported, error.Error);
    }

    [Fact]
    public async Task PaperUsesOnlyStableBuildsAndIgnoresUnknownChannels()
    {
        var resolved = await Service(Standard()).ResolveAsync(ServerType.Paper, "26.2", Ct);

        Assert.Equal(90, resolved.PaperBuild);
        Assert.False(resolved.PaperVersionSupported);
    }

    [Fact]
    public async Task PaperWithOnlyBetaBuildsIsNotOffered()
    {
        var error = await Assert.ThrowsAsync<MetadataException>(() => Service(Standard()).ResolveAsync(ServerType.Paper, "26.3", Ct));

        Assert.Equal(MetadataError.Unsupported, error.Error);
    }

    [Fact]
    public async Task PaperJavaIsTheHigherOfMojangAndPaper()
    {
        var http = Standard();
        http.Json(V262, """{"javaVersion":{"majorVersion":17}}""");
        http.Json("https://fill.papermc.io/v3/projects/paper/versions/26.2", """{"version":{"support":{"status":"SUPPORTED"},"java":{"version":{"minimum":21}}}}""");

        var resolved = await Service(http).ResolveAsync(ServerType.Paper, "26.2", Ct);

        Assert.Equal(21, resolved.JavaMajor);
    }

    [Fact]
    public async Task CancellingOneLookupDoesNotBreakAConcurrentOneSharingTheCache()
    {
        var http = Standard();
        http.Delay = TimeSpan.FromMilliseconds(100);
        var service = Service(http);
        using var first = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var cancelled = service.ResolveAsync(ServerType.Vanilla, "26.3", first.Token);
        var second = service.ResolveAsync(ServerType.Fabric, "26.3", Ct); // shares the manifest/java loads
        await first.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.Equal("0.19.5", (await second).FabricLoaderVersion);
    }

    [Fact]
    public async Task FailureAfterEveryCallerCancelledIsNotCached()
    {
        var http = Standard();
        var good = http.Routes[Manifest];
        http.Routes[Manifest] = () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        http.Delay = TimeSpan.FromMilliseconds(300); // the shared load is still running when the caller gives up…
        var service = Service(http);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var waiting = service.GetReleasesAsync(cts.Token);
        await Task.Delay(50, Ct);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);

        await Task.Delay(600, Ct); // …then it fails in the background and must be evicted
        http.Delay = TimeSpan.Zero;
        http.Routes[Manifest] = good; // provider recovered

        Assert.NotEmpty(await service.GetReleasesAsync(Ct));
    }

    [Fact]
    public async Task FailuresAreNotCached()
    {
        var http = Standard();
        http.Routes[Manifest] = () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        var service = Service(http);

        var error = await Assert.ThrowsAsync<MetadataException>(() => service.GetReleasesAsync(Ct));
        Assert.Equal(MetadataError.ProviderUnavailable, error.Error);

        var ok = Standard();
        http.Routes[Manifest] = ok.Routes[Manifest];
        Assert.NotEmpty(await service.GetReleasesAsync(Ct));
    }

    [Fact]
    public async Task RedirectsAreNotFollowed()
    {
        var http = Standard();
        http.Routes[Manifest] = () =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.Found);
            r.Headers.Location = new Uri("https://evil.example/");
            return r;
        };

        var error = await Assert.ThrowsAsync<MetadataException>(() => Service(http).GetReleasesAsync(Ct));

        Assert.Equal(MetadataError.ProviderUnavailable, error.Error);
        Assert.DoesNotContain(http.Requests, u => u.Contains("evil", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RateLimitCarriesRetryAfter()
    {
        var http = Standard();
        http.Routes[Manifest] = () =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
            return r;
        };

        var error = await Assert.ThrowsAsync<MetadataException>(() => Service(http).GetReleasesAsync(Ct));

        Assert.Equal(MetadataError.RateLimited, error.Error);
        Assert.Equal(TimeSpan.FromSeconds(30), error.RetryAfter);
    }

    [Fact]
    public async Task OversizedBodyWithoutLengthIsRejected()
    {
        var http = Standard();
        http.Routes[Manifest] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new EndlessStream()) };

        var error = await Assert.ThrowsAsync<MetadataException>(() => Service(http).GetReleasesAsync(Ct));

        Assert.Equal(MetadataError.InvalidResponse, error.Error);
    }

    [Fact]
    public async Task SlowBodyIsBoundedByTheDeadline()
    {
        var http = Standard();
        http.Routes[Manifest] = () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new EndlessStream { Delay = TimeSpan.FromMilliseconds(50) }, bufferSize: 16),
        };
        var service = new ServerMetadataService(new MetadataHttpClient(http, TimeSpan.FromMilliseconds(300)));

        var error = await Assert.ThrowsAsync<MetadataException>(() => service.GetReleasesAsync(Ct));

        Assert.Equal(MetadataError.ProviderUnavailable, error.Error);
    }

    [Fact]
    public async Task DeeplyNestedJsonIsRejected()
    {
        var http = Standard();
        http.Json(Manifest, new string('[', 200) + new string(']', 200));

        var error = await Assert.ThrowsAsync<MetadataException>(() => Service(http).GetReleasesAsync(Ct));

        Assert.Equal(MetadataError.InvalidResponse, error.Error);
    }

    [Theory]
    [InlineData("https://piston-meta.mojang.com/v1/packages/a.json", true)]
    [InlineData("http://piston-meta.mojang.com/v1/packages/a.json", false)]
    [InlineData("https://piston-meta.mojang.com:8443/v1/packages/a.json", false)]
    [InlineData("https://user:pw@piston-meta.mojang.com/v1/packages/a.json", false)]
    [InlineData("https://piston-meta.mojang.com.evil.example/v1/packages/a.json", false)]
    [InlineData("https://piston-meta.mojang.com/other/a.json", false)]
    [InlineData("https://piston-meta.mojang.com/v1/packages/a.json#x", false)]
    public void ProviderPolicyIsExact(string url, bool allowed)
    {
        var policy = new ProviderPolicy("piston-meta.mojang.com", ["/v1/packages/"]);

        Assert.Equal(allowed, policy.Allows(new Uri(url)));
    }

    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("2606:4700::1111", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.20.0.1", false)]
    [InlineData("192.168.1.1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("::ffff:127.0.0.1", false)]
    public void OnlyPublicAddressesAreConnected(string ip, bool isPublic) =>
        Assert.Equal(isPublic, MetadataHttpClient.IsPublic(IPAddress.Parse(ip)));
}
