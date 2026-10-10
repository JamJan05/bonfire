using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Bonfire.Core.Content;
using Bonfire.Core.Metadata;
using Bonfire.Core.Servers;
using Bonfire.Infrastructure.Content;
using Bonfire.Infrastructure.Metadata;

namespace Bonfire.Tests;

public sealed class ContentRulesTests
{
    [Theory]
    [InlineData("client_only", null, EnvironmentSupport.ClientOnly)]
    [InlineData("singleplayer_only", null, EnvironmentSupport.ClientOnly)]
    [InlineData("server_only", null, EnvironmentSupport.Server)]
    [InlineData("client_or_server_prefers_both", null, EnvironmentSupport.Server)]
    [InlineData("dedicated_server_only", "unsupported", EnvironmentSupport.Server)] // version level wins
    [InlineData("unknown", "required", EnvironmentSupport.Server)]                  // fallback to project
    [InlineData(null, "unsupported", EnvironmentSupport.ClientOnly)]
    [InlineData(null, null, EnvironmentSupport.Unknown)]
    public void EnvironmentClassification(string? version, string? project, EnvironmentSupport expected) =>
        Assert.Equal(expected, ContentRules.Environment(version, project));

    private static ModrinthFile F(string name, bool primary, string? type = null) => new("https://cdn.modrinth.com/data/x", name, "", new string('a', 128), 1, primary, type);

    private static ModrinthVersion V(params ModrinthFile[] files) =>
        new("v1", "p1", "", "", "release", ["26.3"], ["fabric"], null, DateTimeOffset.UtcNow, files, []);

    [Fact]
    public void PrimaryJarIsChosen() => Assert.Equal("a.jar", ContentRules.SelectFile(V(F("a.jar", true), F("b.jar", false)))!.FileName);

    [Fact]
    public void SingleInstallableJarIsChosenWithoutPrimary() =>
        Assert.Equal("a.jar", ContentRules.SelectFile(V(F("a.jar", false), F("a-sources.jar", false, "sources-jar")))!.FileName);

    [Fact]
    public void AmbiguousChoiceIsRefused() => Assert.Null(ContentRules.SelectFile(V(F("a.jar", false), F("b.jar", false))));

    [Fact]
    public void NonJarPrimaryIsNotInstalled() => Assert.Null(ContentRules.SelectFile(V(F("pack.zip", true))));

    [Fact]
    public void TrailingNewlinesNeverPassValidation()
    {
        Assert.False(ContentRules.IsSafeId("abc\n"));
        Assert.Equal("AAAAAAAA-BBBBBBBB.jar", ContentRules.InstallFileName("a.jar\n", "AAAAAAAA", "BBBBBBBB"));
        Assert.Contains("Validation_VersionInvalid", ServerDefinitionValidator.Validate(ServerTestData.Vanilla() with { MinecraftVersion = "26.3\n" }));
        Assert.Contains("Validation_WhitelistInvalid", ServerDefinitionValidator.Validate(ServerTestData.Vanilla() with
        {
            Game = ServerTestData.Vanilla().Game with { WhitelistEnabled = true, Whitelist = ["Steve\n"] },
        }));
    }

    [Theory]
    [InlineData("lithium-fabric-0.26.2+mc26.3.jar", "lithium-fabric-0.26.2+mc26.3.jar")]
    [InlineData("../../evil.jar", "AAAAAAAA-BBBBBBBB.jar")]
    [InlineData("na me.jar", "AAAAAAAA-BBBBBBBB.jar")]
    [InlineData("x.jar.exe", "AAAAAAAA-BBBBBBBB.jar")]
    [InlineData("a..b.jar", "AAAAAAAA-BBBBBBBB.jar")]
    public void InstallFileNames(string api, string expected) => Assert.Equal(expected, ContentRules.InstallFileName(api, "AAAAAAAA", "BBBBBBBB"));

    [Fact]
    public void ProfilesFollowTheServerType()
    {
        Assert.Null(ContentProfile.For(ServerTestData.Vanilla()));
        Assert.Equal("mods", ContentProfile.For(ServerTestData.Vanilla() with { Type = ServerType.Fabric })!.Directory);
        var paper = ContentProfile.For(ServerTestData.Vanilla() with { Type = ServerType.Paper })!;
        Assert.Equal("plugins", paper.Directory);
        Assert.Equal(["paper", "spigot", "bukkit"], paper.Loaders);
    }
}

public sealed class ModrinthClientTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly ContentProfile Fabric = new("mods", ["fabric"], "26.3", ServerType.Fabric);
    private static readonly string Sha = new('a', 128);

    private static string VersionJson(string id = "VVVVVVVV", string game = "26.3", string url = "https://cdn.modrinth.com/data/PPPPPPPP/versions/x/a.jar",
        string sha = "", string deps = "[]", string loaders = "[\"fabric\"]") =>
        $$$"""
        {"id":"{{{id}}}","project_id":"PPPPPPPP","name":"n","version_number":"1.0","version_type":"release",
         "game_versions":["{{{game}}}"],"loaders":{{{loaders}}},"environment":"server_only","date_published":"2026-09-01T00:00:00Z",
         "files":[{"url":"{{{url}}}","filename":"a.jar","primary":true,"size":10,"hashes":{"sha1":"x","sha512":"{{{(sha.Length == 0 ? Sha : sha)}}}"}}],
         "dependencies":{{{deps}}}}
        """;

    [Fact]
    public async Task VersionsAreRecheckedAndUnverifiableFilesDropped()
    {
        var http = new FakeHttp();
        http.Json("https://api.modrinth.com/v2/project/PPPPPPPP/version?loaders=%5B%22fabric%22%5D&game_versions=%5B%2226.3%22%5D",
            "[" + VersionJson("V1111111") + "," + VersionJson("V2222222", game: "1.20.1") + "," +
            VersionJson("V3333333", url: "https://evil.example/a.jar") + "," + VersionJson("V4444444", sha: "zz") + "," +
            VersionJson("../../xx") + "]");
        var client = new ModrinthClient(new MetadataHttpClient(http));

        var versions = await client.GetCompatibleVersionsAsync("PPPPPPPP", Fabric, Ct);

        Assert.Equal(["V1111111", "V3333333", "V4444444"], versions.Select(v => v.Id));
        Assert.Single(versions[0].Files);
        Assert.Empty(versions[1].Files); // non-CDN URL
        Assert.Empty(versions[2].Files); // bad hash
    }

    [Fact]
    public async Task DependenciesAreParsedWithMissingFields()
    {
        var http = new FakeHttp();
        http.Json("https://api.modrinth.com/v2/version/VVVVVVVV", VersionJson(deps:
            """[{"project_id":"P7dR8mSH","version_id":null,"dependency_type":"required"},{"project_id":null,"version_id":"Xx123456","dependency_type":"required"},{"file_name":"x.jar","dependency_type":"optional"},{"project_id":"../bad","dependency_type":"incompatible"},{"project_id":"P1","dependency_type":"weird"}]"""));
        var version = await new ModrinthClient(new MetadataHttpClient(http)).GetVersionAsync("VVVVVVVV", Ct);

        Assert.Equal(4, version.Dependencies.Count); // unknown type dropped
        Assert.Equal(new ModrinthDependency("P7dR8mSH", null, null, DependencyType.Required), version.Dependencies[0]);
        Assert.Equal(new ModrinthDependency(null, "Xx123456", null, DependencyType.Required), version.Dependencies[1]);
        Assert.Null(version.Dependencies[3].ProjectId); // unsafe id never used
    }

    [Fact]
    public async Task HashLookupAcceptsOnlyMatchingAnswersForAskedHashes()
    {
        var asked = new string('b', 128);
        var other = new string('c', 128);
        var http = new FakeHttp();
        http.Routes["https://api.modrinth.com/v2/version_files"] = () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($$"""{"{{asked}}":{{VersionJson(sha: asked)}},"{{other}}":{{VersionJson(sha: other)}},"{{Sha}}":{{VersionJson(sha: asked)}}}""", Encoding.UTF8, "application/json"),
        };
        var client = new ModrinthClient(new MetadataHttpClient(http));

        var result = await client.GetVersionsByHashAsync([asked, Sha], Ct);

        Assert.True(result.ContainsKey(asked));
        Assert.False(result.ContainsKey(other)); // not asked
        Assert.False(result.ContainsKey(Sha));   // answer's file does not have that hash
    }

    [Fact]
    public async Task SearchSkipsHitsWithInvalidIds()
    {
        var http = new FakeHttp();
        http.Routes.Clear();
        var client = new ModrinthClient(new MetadataHttpClient(new Router(_ => """{"hits":[{"project_id":"Good1234","title":"Good"},{"project_id":"../x","title":"Bad"}],"offset":0,"limit":20,"total_hits":2}""")));

        var page = await client.SearchAsync("x", Fabric, SearchSort.Relevance, 0, Ct);

        Assert.Equal(["Good1234"], page.Hits.Select(h => h.Id));
        Assert.Equal(2, page.Total);
    }

    private sealed class Router(Func<HttpRequestMessage, string> json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json(request), Encoding.UTF8, "application/json") });
    }
}

public sealed class ContentDownloaderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly byte[] Payload = Encoding.ASCII.GetBytes("jar-bytes!");

    private static ModrinthFile File(long? size = null, string? sha = null, string url = "https://cdn.modrinth.com/data/P/versions/V/a.jar") =>
        new(url, "a.jar", "", sha ?? Convert.ToHexStringLower(SHA512.HashData(Payload)), size ?? Payload.Length, true, null);

    private sealed class Server(Func<int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(Calls++));
    }

    private static HttpResponseMessage Ok(byte[] body) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

    [Fact]
    public async Task VerifiedFileIsWritten()
    {
        using var temp = new TempDirectory();
        var target = Path.Combine(temp.Path, "a.jar");
        using var downloader = new ContentDownloader(new Server(_ => Ok(Payload)));

        await downloader.DownloadAsync(File(), target, null, Ct);

        Assert.Equal(Payload, await System.IO.File.ReadAllBytesAsync(target, Ct));
    }

    public static TheoryData<string> Bad => new() { "hash", "longer", "shorter", "host" };

    [Theory]
    [MemberData(nameof(Bad))]
    public async Task MismatchesLeaveNoFile(string kind)
    {
        using var temp = new TempDirectory();
        var target = Path.Combine(temp.Path, "a.jar");
        var file = kind switch
        {
            "hash" => File(sha: new string('0', 128)),
            "longer" => File(size: Payload.Length - 2),
            "shorter" => File(size: Payload.Length + 5),
            _ => File(url: "https://evil.example/data/a.jar"),
        };
        using var downloader = new ContentDownloader(new Server(_ =>
        {
            // Non-seekable body: no Content-Length, so only the streaming checks can catch a mismatch.
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new NonSeekable(Payload)) };
            Assert.Null(response.Content.Headers.ContentLength);
            return response;
        }));

        await Assert.ThrowsAsync<MetadataException>(() => downloader.DownloadAsync(file, target, null, Ct));
        Assert.False(System.IO.File.Exists(target));
    }

    [Fact]
    public async Task OversizedStreamIsCutOffEarly()
    {
        using var temp = new TempDirectory();
        // An endless body: without the streaming cap this would only end at the 10-minute deadline.
        using var downloader = new ContentDownloader(new Server(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new EndlessStream()),
        }));

        var download = downloader.DownloadAsync(File(), Path.Combine(temp.Path, "a.jar"), null, Ct);

        Assert.Same(download, await Task.WhenAny(download, Task.Delay(TimeSpan.FromSeconds(5), Ct)));
        await Assert.ThrowsAsync<MetadataException>(() => download);
    }

    [Fact]
    public async Task AnExistingFileIsNeverDeletedByAFailedDownload()
    {
        using var temp = new TempDirectory();
        var target = Path.Combine(temp.Path, "a.jar");
        await System.IO.File.WriteAllTextAsync(target, "keep me", Ct);
        using var downloader = new ContentDownloader(new Server(_ => Ok(Payload)));

        await Assert.ThrowsAnyAsync<IOException>(() => downloader.DownloadAsync(File(), target, null, Ct));

        Assert.Equal("keep me", await System.IO.File.ReadAllTextAsync(target, Ct));
    }

    [Fact]
    public async Task RetryAfterAsAnHttpDateIsHonoured()
    {
        using var temp = new TempDirectory();
        var server = new Server(call =>
        {
            if (call > 0)
            {
                return Ok(Payload);
            }

            var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddMinutes(5));
            return limited;
        });
        using var downloader = new ContentDownloader(server);

        // A five-minute wait exceeds the bound: no retry, the rate limit is reported.
        var error = await Assert.ThrowsAsync<MetadataException>(() => downloader.DownloadAsync(File(), Path.Combine(temp.Path, "a.jar"), null, Ct));
        Assert.Equal(MetadataError.RateLimited, error.Error);
        Assert.Equal(1, server.Calls);
    }

    private sealed class NonSeekable(byte[] data) : Stream
    {
        private readonly MemoryStream _inner = new(data);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task RateLimitIsRetriedOnceHonouringRetryAfter()
    {
        using var temp = new TempDirectory();
        var server = new Server(call =>
        {
            if (call > 0)
            {
                return Ok(Payload);
            }

            var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMilliseconds(10));
            return limited;
        });
        using var downloader = new ContentDownloader(server);

        await downloader.DownloadAsync(File(), Path.Combine(temp.Path, "a.jar"), null, Ct);

        Assert.Equal(2, server.Calls);
    }
}

public sealed class LocalJarTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string MakeJar(string dir, params (string Name, string Content)[] entries)
    {
        var path = Path.Combine(dir, $"{Guid.NewGuid():N}.jar");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            using var w = new StreamWriter(zip.CreateEntry(name).Open());
            w.Write(content);
        }

        return path;
    }

    [Fact]
    public async Task FabricModDescriptorIsRead()
    {
        using var temp = new TempDirectory();
        var jar = MakeJar(temp.Path, ("fabric.mod.json", """{"id":"mymod","name":"My \u0007Mod","version":"1.2.3"}"""), ("a/B.class", "x"));

        var info = await LocalJar.StageAsync(jar, Path.Combine(temp.Path, "staged.jar"), Ct);

        Assert.Equal(JarKind.FabricMod, info.Kind);
        Assert.Equal("mymod", info.Id);
        Assert.Equal("My Mod", info.Name); // control characters stripped
        Assert.Equal(Convert.ToHexStringLower(SHA512.HashData(System.IO.File.ReadAllBytes(jar))), info.Sha512);
    }

    [Fact]
    public async Task PluginDescriptorIsRead()
    {
        using var temp = new TempDirectory();
        var jar = MakeJar(temp.Path, ("plugin.yml", "name: Essentials\nversion: '2.21'\nmain: x.Y\n"));

        var info = await LocalJar.StageAsync(jar, Path.Combine(temp.Path, "staged.jar"), Ct);

        Assert.Equal(JarKind.BukkitPlugin, info.Kind);
        Assert.Equal("Essentials", info.Name);
        Assert.Equal("2.21", info.Version);
    }

    [Fact]
    public async Task InvalidFilesAreRejectedAndNotLeftInStaging()
    {
        using var temp = new TempDirectory();
        var notZip = Path.Combine(temp.Path, "x.jar");
        await System.IO.File.WriteAllTextAsync(notZip, "hello", Ct);
        var huge = MakeJar(temp.Path, ("fabric.mod.json", new string(' ', (int)LocalJar.MaxDescriptorBytes + 10)));
        var dup = MakeJar(temp.Path, ("a.txt", "1"), ("a.txt", "2"));

        foreach (var bad in new[] { notZip, huge, dup })
        {
            var staged = Path.Combine(temp.Path, "staged-" + Path.GetFileName(bad));
            await Assert.ThrowsAsync<InvalidJarException>(() => LocalJar.StageAsync(bad, staged, Ct));
            Assert.False(System.IO.File.Exists(staged));
        }
    }

    [Fact]
    public async Task SymlinkedSourceIsRefused()
    {
        using var temp = new TempDirectory();
        var jar = MakeJar(temp.Path, ("plugin.yml", "name: X\n"));
        var link = Path.Combine(temp.Path, "link.jar");
        System.IO.File.CreateSymbolicLink(link, jar);

        await Assert.ThrowsAnyAsync<IOException>(() => LocalJar.StageAsync(link, Path.Combine(temp.Path, "s.jar"), Ct));
    }
}
