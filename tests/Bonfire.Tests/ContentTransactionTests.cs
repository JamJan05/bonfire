using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Bonfire.App.Services;
using Bonfire.Core;
using Bonfire.Core.Content;
using Bonfire.Core.Metadata;
using Bonfire.Core.Servers;
using Bonfire.Infrastructure.Content;
using Bonfire.Infrastructure.Servers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bonfire.Tests;

/// <summary>An in-memory Modrinth: projects, versions and the bytes of their files.</summary>
internal sealed class FakeModrinth : IModrinth
{
    public List<ModrinthVersion> Versions { get; } = [];
    public Dictionary<string, string> ServerSide { get; } = new();
    public Dictionary<string, byte[]> Bytes { get; } = new();
    public bool FailHashLookup { get; set; }
    public HashSet<string> FailVersionsOf { get; } = [];
    public int Calls { get; private set; }

    private static readonly ContentProfile Fabric = new("mods", ["fabric"], "26.3", ServerType.Fabric);

    public ModrinthVersion Add(string project, string version, string type = "release", int day = 1, string? env = "server_only",
        string game = "26.3", string? fileName = null, params ModrinthDependency[] deps)
    {
        var bytes = Encoding.UTF8.GetBytes($"{project}/{version}");
        var sha = Convert.ToHexStringLower(SHA512.HashData(bytes));
        var url = $"https://cdn.modrinth.com/data/{project}/versions/{version}/file.jar";
        Bytes[url] = bytes;
        var v = new ModrinthVersion(version, project, $"{project} {version}", version, type, [game], ["fabric"], env,
            new DateTimeOffset(2026, 9, day, 0, 0, 0, TimeSpan.Zero),
            [new ModrinthFile(url, fileName ?? $"{project.ToLowerInvariant()}-{version}.jar", "", sha, bytes.Length, true, null)], deps);
        Versions.Add(v);
        return v;
    }

    public static ModrinthDependency Requires(string project, string? version = null) => new(project, version, null, DependencyType.Required);

    public Task<SearchPage> SearchAsync(string query, ContentProfile profile, SearchSort sort, int offset, CancellationToken cancellationToken)
    {
        var hits = Versions.Select(v => v.ProjectId).Distinct().Skip(offset)
            .Select(p => new ModrinthProject(p, p.ToLowerInvariant(), $"Title {p}", "A project", "someone", "MIT", 10, DateTimeOffset.UtcNow,
                null, "required", null, [profile.GameVersion], []))
            .ToList();
        return Task.FromResult(new SearchPage(hits, hits.Count + offset, offset));
    }

    public Task<ModrinthProject> GetProjectAsync(string projectId, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(new ModrinthProject(projectId, projectId.ToLowerInvariant(), $"Title {projectId}", "", null, null, 0, default,
            null, ServerSide.GetValueOrDefault(projectId, "required"), null, [], []));
    }

    public Task<IReadOnlyList<ModrinthVersion>> GetCompatibleVersionsAsync(string projectId, ContentProfile profile, CancellationToken cancellationToken)
    {
        Calls++;
        if (FailVersionsOf.Contains(projectId))
        {
            throw new MetadataException(MetadataError.ProviderUnavailable, "offline");
        }

        return Task.FromResult<IReadOnlyList<ModrinthVersion>>(Versions
            .Where(v => v.ProjectId == projectId && v.GameVersions.Contains(profile.GameVersion))
            .OrderByDescending(v => v.Published).ToList());
    }

    public Task<ModrinthVersion> GetVersionAsync(string versionId, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(Versions.FirstOrDefault(v => v.Id == versionId)
                               ?? throw new MetadataException(MetadataError.InvalidResponse, "no such version"));
    }

    public Task<IReadOnlyDictionary<string, ModrinthVersion>> GetVersionsByHashAsync(IReadOnlyList<string> sha512, CancellationToken cancellationToken)
    {
        if (FailHashLookup)
        {
            throw new MetadataException(MetadataError.ProviderUnavailable, "offline");
        }

        var result = new Dictionary<string, ModrinthVersion>();
        foreach (var v in Versions)
        {
            foreach (var f in v.Files.Where(f => sha512.Contains(f.Sha512)))
            {
                result[f.Sha512] = v;
            }
        }

        return Task.FromResult<IReadOnlyDictionary<string, ModrinthVersion>>(result);
    }

    public sealed class Cdn(FakeModrinth modrinth) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(modrinth.Bytes.TryGetValue(request.RequestUri!.ToString(), out var b)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(b) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}

public sealed class ContentResolverTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly ContentProfile Fabric = new("mods", ["fabric"], "26.3", ServerType.Fabric);

    private static ContentInventory Inventory(params InstalledItem[] items) =>
        new(Fabric, ContentLock.Empty, items, [], [], false, "fp");

    private static InstalledItem Installed(ModrinthVersion v, bool disabled = false) =>
        new(v.Files[0].FileName, disabled, v.Files[0].Sha512, v.Files[0].Size, ItemState.Untracked, null, v);

    [Fact]
    public async Task RequiredDependenciesAreAddedAndReleasesPreferred()
    {
        var api = new FakeModrinth();
        api.Add("Api", "api1", day: 1);
        api.Add("Api", "api2beta", type: "beta", day: 5);
        api.Add("Mod", "mod1", deps: [FakeModrinth.Requires("Api"), new ModrinthDependency("Opt", null, null, DependencyType.Optional),
            new ModrinthDependency("Emb", null, null, DependencyType.Embedded)]);

        var plan = await new ContentResolver(api, Inventory()).PlanAsync("Mod", null, Ct);

        Assert.True(plan.CanInstall);
        Assert.Equal(["mod1", "api1"], plan.Items.Select(i => i.Version.Id));
        Assert.False(plan.Items[0].IsDependency);
        Assert.True(plan.Items[1].IsDependency);
        Assert.Equal([new OptionalDependency("Opt", null)], plan.OptionalDependencies);
        Assert.Empty(plan.Warnings);
        Assert.Equal("fp", plan.Fingerprint);
    }

    [Fact]
    public async Task InstalledDependencyIsReusedButADisabledOneBlocks()
    {
        var api = new FakeModrinth();
        var dep = api.Add("Api", "api1");
        api.Add("Mod", "mod1", deps: [FakeModrinth.Requires("Api")]);

        var reused = await new ContentResolver(api, Inventory(Installed(dep))).PlanAsync("Mod", null, Ct);
        Assert.Equal(["mod1"], reused.Items.Select(i => i.Version.Id));

        var blocked = await new ContentResolver(api, Inventory(Installed(dep, disabled: true))).PlanAsync("Mod", null, Ct);
        Assert.Contains(blocked.Problems, p => p.Kind == PlanProblemKind.DependencyDisabled);
        Assert.False(blocked.CanInstall);
    }

    [Fact]
    public async Task ALaterExactConstraintRestartsWithIt()
    {
        // Mod needs Lib (any) and Other; Other needs Lib exactly lib1, although lib2 is newer.
        var api = new FakeModrinth();
        api.Add("Lib", "lib1", day: 1);
        api.Add("Lib", "lib2", day: 9);
        api.Add("Other", "other1", deps: [FakeModrinth.Requires("Lib", "lib1")]);
        api.Add("Mod", "mod1", deps: [FakeModrinth.Requires("Lib"), FakeModrinth.Requires("Other")]);

        var plan = await new ContentResolver(api, Inventory()).PlanAsync("Mod", null, Ct);

        Assert.True(plan.CanInstall, string.Join(",", plan.Problems));
        Assert.Contains(plan.Items, i => i.Version.Id == "lib1");
        Assert.DoesNotContain(plan.Items, i => i.Version.Id == "lib2");
    }

    [Fact]
    public async Task TwoDifferentExactConstraintsConflict()
    {
        var api = new FakeModrinth();
        api.Add("Lib", "lib1");
        api.Add("Lib", "lib2");
        api.Add("A", "a1", deps: [FakeModrinth.Requires("Lib", "lib1")]);
        api.Add("B", "b1", deps: [FakeModrinth.Requires("Lib", "lib2")]);
        api.Add("Mod", "mod1", deps: [FakeModrinth.Requires("A"), FakeModrinth.Requires("B")]);

        var plan = await new ContentResolver(api, Inventory()).PlanAsync("Mod", null, Ct);

        Assert.Contains(plan.Problems, p => p.Kind == PlanProblemKind.VersionConflict);
    }

    [Fact]
    public async Task ExactConstraintAgainstAnotherInstalledVersionConflicts()
    {
        var api = new FakeModrinth();
        var installed = api.Add("Lib", "lib2");
        api.Add("Lib", "lib1");
        api.Add("Mod", "mod1", deps: [FakeModrinth.Requires("Lib", "lib1")]);

        var plan = await new ContentResolver(api, Inventory(Installed(installed))).PlanAsync("Mod", null, Ct);

        Assert.Contains(plan.Problems, p => p.Kind == PlanProblemKind.InstalledVersionConflict);
    }

    [Fact]
    public async Task IncompatibilityIsCheckedInBothDirections()
    {
        var api = new FakeModrinth();
        var hater = api.Add("Hater", "h1", deps: [new ModrinthDependency("Mod", null, null, DependencyType.Incompatible)]);
        api.Add("Mod", "mod1");
        var plain = api.Add("Plain", "p1");
        api.Add("Picky", "k1", deps: [new ModrinthDependency("Plain", null, null, DependencyType.Incompatible)]);

        var reverse = await new ContentResolver(api, Inventory(Installed(hater))).PlanAsync("Mod", null, Ct);
        Assert.Contains(reverse.Problems, p => p.Kind == PlanProblemKind.Incompatible);

        var forward = await new ContentResolver(api, Inventory(Installed(plain))).PlanAsync("Picky", null, Ct);
        Assert.Contains(forward.Problems, p => p.Kind == PlanProblemKind.Incompatible);
    }

    [Fact]
    public async Task VersionOnlyIncompatibilitiesCountInBothDirections()
    {
        var api = new FakeModrinth();
        var target = api.Add("Target", "t1");
        var hater = api.Add("Hater", "h1", deps: [new ModrinthDependency(null, "t1", null, DependencyType.Incompatible)]);

        var forward = await new ContentResolver(api, Inventory(Installed(target))).PlanAsync("Hater", null, Ct);
        Assert.Contains(forward.Problems, p => p.Kind == PlanProblemKind.Incompatible);

        var reverse = await new ContentResolver(api, Inventory(Installed(hater))).PlanAsync("Target", null, Ct);
        Assert.Contains(reverse.Problems, p => p.Kind == PlanProblemKind.Incompatible);
    }

    [Fact]
    public async Task WarningsAndUnresolvableDependencies()
    {
        var api = new FakeModrinth();
        api.Add("Beta", "b1", type: "beta", env: "client_only");
        api.Add("Odd", "o1", env: null);
        api.ServerSide["Odd"] = "unknown";
        api.Add("Ext", "e1", deps: [new ModrinthDependency(null, null, "something.jar", DependencyType.Required)]);

        var beta = await new ContentResolver(api, Inventory()).PlanAsync("Beta", null, Ct);
        Assert.Contains(beta.Warnings, w => w.Kind == PlanWarningKind.Prerelease);
        Assert.Contains(beta.Warnings, w => w.Kind == PlanWarningKind.ClientOnly);

        var odd = await new ContentResolver(api, Inventory()).PlanAsync("Odd", null, Ct);
        Assert.Contains(odd.Warnings, w => w.Kind == PlanWarningKind.EnvironmentUnknown);

        var ext = await new ContentResolver(api, Inventory()).PlanAsync("Ext", null, Ct);
        Assert.Contains(ext.Problems, p => p.Kind == PlanProblemKind.UnresolvedDependency);
    }

    [Fact]
    public async Task DependencyGivenOnlyByVersionIdIsResolved()
    {
        var api = new FakeModrinth();
        api.Add("Lib", "lib1");
        api.Add("Mod", "mod1", deps: [new ModrinthDependency(null, "lib1", null, DependencyType.Required)]);

        var plan = await new ContentResolver(api, Inventory()).PlanAsync("Mod", null, Ct);

        Assert.Equal(["mod1", "lib1"], plan.Items.Select(i => i.Version.Id));
    }

    [Fact]
    public async Task CyclesTerminateAndNamesNeverCollide()
    {
        var api = new FakeModrinth();
        api.Add("A", "a1", fileName: "same.jar", deps: [FakeModrinth.Requires("B")]);
        api.Add("B", "b1", fileName: "same.jar", deps: [FakeModrinth.Requires("A")]);
        var taken = new InstalledItem("A-a1.jar", false, new string('0', 128), 1, ItemState.Untracked, null, null);

        var plan = await new ContentResolver(api, Inventory(taken)).PlanAsync("A", null, Ct);

        // "same.jar" used by A; B falls back to its id name; A's fallback would be taken, but it did not need one.
        Assert.Equal(["same.jar", "B-b1.jar"], plan.Items.Select(i => i.InstallName));
        Assert.Contains(plan.Warnings, w => w.Kind == PlanWarningKind.UnverifiedFilesPresent);
    }

    [Fact]
    public async Task LongChainsHitTheLimitHonestly()
    {
        var api = new FakeModrinth();
        for (var i = 0; i <= ContentResolver.MaxProjects; i++)
        {
            api.Add($"P{i}", $"v{i}", deps: [FakeModrinth.Requires($"P{i + 1}")]);
        }

        var plan = await new ContentResolver(api, Inventory()).PlanAsync("P0", null, Ct);

        Assert.Equal(PlanProblemKind.LimitExceeded, Assert.Single(plan.Problems).Kind);
    }
}

public sealed class ContentTransactionTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly ServerRegistry _registry;
    private readonly FakeServerRuntime _runtime = new();
    private readonly FakeModrinth _api = new();
    private readonly ContentEngine _engine;
    private readonly ServerOperationsService _operations;
    private readonly ContentService _content;
    private readonly ServerRecord _record;
    private readonly ServerPaths _paths;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ContentTransactionTests()
    {
        var app = AppPaths.Resolve(k => k == "HOME" ? _temp.Path : null);
        Bonfire.Infrastructure.AppDirectories.Ensure(app);
        _registry = new ServerRegistry(app);
        var definition = ServerTestData.Vanilla() with { Type = ServerType.Fabric, FabricLoaderVersion = "0.19.5", FabricLauncherVersion = "1.1.2", RestartPolicy = RestartPolicyKind.UnlessStopped };
        _record = ServerRecord.New(definition) with { Phase = ProvisioningPhase.Ready, ContainerId = "c1", NetworkId = "n1" };
        _paths = _registry.CreateAsync(_record).GetAwaiter().GetResult();
        _runtime.Containers.Add(FakeServerRuntime.State(definition.Id, "exited"));
        _engine = new ContentEngine(_registry, _runtime, NullLogger<ContentEngine>.Instance);
        _operations = new ServerOperationsService(_registry, _runtime, new ServerCreationTests.SafeDocker(), NullLogger<ServerOperationsService>.Instance,
            new RestoreEngine(_registry, _runtime, TimeProvider.System, NullLogger<RestoreEngine>.Instance), _engine);
        _content = new ContentService(_registry, _operations, _runtime, _engine, _api, () => new ContentDownloader(new FakeModrinth.Cdn(_api)),
            TimeProvider.System, NullLogger<ContentService>.Instance);

        _api.Add("Api", "api1", fileName: "fabric-api.jar");
        _api.Add("Mod", "mod1", fileName: "mod.jar", deps: [FakeModrinth.Requires("Api")]);
    }

    public void Dispose() => _temp.Dispose();

    private Guid Id => _record.Definition.Id;
    private string Mods => Path.Combine(_paths.DataDir, "mods");
    private string Disabled => Path.Combine(_paths.ServerDir, "disabled-content", "mods");
    private ServerRecord Current => _registry.Load(Id)!.Record!;

    private string[] Names(string dir) => Directory.Exists(dir) ? Directory.GetFiles(dir).Select(f => Path.GetFileName(f)).Order().ToArray() : [];

    private bool NoTxDirs => !Directory.EnumerateDirectories(_paths.ServerDir, "content-tx-*").Any();

    private async Task InstallModAsync()
    {
        var plan = await _content.PlanInstallAsync(Id, "Mod", null, Ct);
        Assert.True(plan.CanInstall);
        await _content.InstallAsync(Id, plan, null, Ct);
    }

    [Fact]
    public async Task InstallPutsVerifiedFilesLiveAndRecordsTheLock()
    {
        await InstallModAsync();

        Assert.Equal(["fabric-api.jar", "mod.jar"], Names(Mods));
        var lockFile = ContentLockStore.Load(_paths);
        Assert.Equal(["mod1", "api1"], lockFile.Entries.Select(e => e.VersionId));
        Assert.All(lockFile.Entries, e => Assert.Equal(ContentOrigin.Installed, e.Origin));
        Assert.Equal("Title Mod", lockFile.Entries[0].Title);
        Assert.Null(Current.PendingContent);
        Assert.True(NoTxDirs);
        Assert.Equal(["policy:No", "policy:UnlessStopped"], _runtime.Calls.Where(c => c.StartsWith("policy", StringComparison.Ordinal)));

        var inventory = await _content.InventoryAsync(Id, Ct);
        Assert.All(inventory.Items, i => Assert.Equal(ItemState.Tracked, i.State));
    }

    [Fact]
    public async Task AChangeAfterPlanningMakesThePlanStale()
    {
        var plan = await _content.PlanInstallAsync(Id, "Mod", null, Ct);
        Directory.CreateDirectory(Mods);
        await File.WriteAllTextAsync(Path.Combine(Mods, "other.jar"), "x", Ct);

        var error = await Assert.ThrowsAsync<ContentException>(() => _content.InstallAsync(Id, plan, null, Ct));

        Assert.Equal(ContentError.PlanStale, error.Error);
        Assert.Equal(["other.jar"], Names(Mods));
    }

    [Fact]
    public async Task ARunningServerIsNotChanged()
    {
        var plan = await _content.PlanInstallAsync(Id, "Mod", null, Ct);
        _runtime.Containers[0] = FakeServerRuntime.State(Id, "running");

        var error = await Assert.ThrowsAsync<ServerOperationException>(() => _content.InstallAsync(Id, plan, null, Ct));

        Assert.Equal(ServerOperationError.StillRunning, error.Error);
        Assert.Empty(Names(Mods));
    }

    [Fact]
    public async Task AServerStartedAfterTheIntentIsRecordedRollsBack()
    {
        var plan = await _content.PlanInstallAsync(Id, "Mod", null, Ct);
        _runtime.PolicyBehavior = (_, policy) =>
        {
            if (policy == RestartPolicyKind.No)
            {
                _runtime.Containers[0] = FakeServerRuntime.State(Id, "paused"); // paused counts as running
            }

            return Task.CompletedTask;
        };

        await Assert.ThrowsAsync<ServerOperationException>(() => _content.InstallAsync(Id, plan, null, Ct));

        Assert.Empty(Names(Mods));
        Assert.Null(Current.PendingContent);
        Assert.True(NoTxDirs);
        Assert.Equal("policy:UnlessStopped", _runtime.Calls.Last(c => c.StartsWith("policy", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task FailureAfterAMoveRollsEverythingBack()
    {
        var plan = await _content.PlanInstallAsync(Id, "Mod", null, Ct);
        _engine.AfterMove = i =>
        {
            if (i == 0)
            {
                throw new IOException("disk on fire");
            }
        };

        await Assert.ThrowsAsync<IOException>(() => _content.InstallAsync(Id, plan, null, Ct));

        Assert.Empty(Names(Mods));
        Assert.False(File.Exists(ContentLockStore.PathFor(_paths)));
        Assert.Null(Current.PendingContent);
        Assert.True(NoTxDirs);
        Assert.Equal("policy:UnlessStopped", _runtime.Calls.Last(c => c.StartsWith("policy", StringComparison.Ordinal)));
    }

    /// <summary>Simulates a crash: the journal says Prepared, one of two files was already moved.</summary>
    private async Task<(PendingContent Pending, string TxDir)> CrashMidwayAsync(ContentTxStep step, bool moveBoth = false)
    {
        var txId = Guid.NewGuid();
        var files = new ContentFiles(_paths, "mods");
        files.EnsureLayout(txId);
        var a = Encoding.UTF8.GetBytes("a");
        var b = Encoding.UTF8.GetBytes("b");
        await File.WriteAllBytesAsync(files.PathFor(ContentLocation.Live, txId, "a.jar"), a, Ct);
        await File.WriteAllBytesAsync(files.PathFor(moveBoth ? ContentLocation.Live : ContentLocation.Staging, txId, "b.jar"), b, Ct);
        var lockAfter = new ContentLock(1, [new LockEntry(ContentProvider.Local, null, null, "A", null, "a.jar", Sha(a), 1, "26.3", "fabric", [], [], DateTimeOffset.UtcNow, false, ContentOrigin.Installed, false)]);
        var pending = new PendingContent(txId, "mods",
            [new ContentMove(ContentLocation.Staging, ContentLocation.Live, "a.jar", Sha(a)), new ContentMove(ContentLocation.Staging, ContentLocation.Live, "b.jar", Sha(b))],
            lockAfter, RestartPolicyKind.UnlessStopped, step);
        await _registry.SaveAsync(Current with { PendingContent = pending }, Ct);
        return (pending, files.TxDir(txId));
    }

    private static string Sha(byte[] b) => Convert.ToHexStringLower(SHA512.HashData(b));

    [Fact]
    public async Task RepairRollsBackAPreparedTransactionAndStopsARunningServer()
    {
        var (_, txDir) = await CrashMidwayAsync(ContentTxStep.Prepared);
        _runtime.Containers[0] = FakeServerRuntime.State(Id, "running"); // restarted by Docker after the crash
        _runtime.Stop = (_, _) =>
        {
            _runtime.Containers[0] = FakeServerRuntime.State(Id, "exited");
            return Task.CompletedTask;
        };

        await Assert.ThrowsAsync<ServerOperationException>(() => _operations.StartAsync(Id, Ct)); // blocked while pending
        await _operations.RepairAsync(Id, Ct);

        Assert.Empty(Names(Mods));
        Assert.False(Directory.Exists(txDir));
        Assert.Null(Current.PendingContent);
        Assert.Contains("stop", _runtime.Calls);
        Assert.DoesNotContain("start", _runtime.Calls);
    }

    [Fact]
    public async Task RepairRollsACommittedTransactionForward()
    {
        var (pending, txDir) = await CrashMidwayAsync(ContentTxStep.Committed, moveBoth: true);

        await _operations.RepairAsync(Id, Ct);

        Assert.Equal(["a.jar", "b.jar"], Names(Mods));
        Assert.Equal(pending.LockAfter.Entries.Select(e => (e.FileName, e.Sha512)), ContentLockStore.Load(_paths).Entries.Select(e => (e.FileName, e.Sha512)));
        Assert.False(Directory.Exists(txDir));
        Assert.Null(Current.PendingContent);
    }

    [Fact]
    public async Task UnexpectedFilesKeepTheJournalAndBlockStarting()
    {
        var (_, txDir) = await CrashMidwayAsync(ContentTxStep.Prepared);
        await File.WriteAllTextAsync(Path.Combine(Mods, "a.jar"), "changed by someone", Ct);

        await Assert.ThrowsAsync<ContentRecoveryException>(() => _operations.RepairAsync(Id, Ct));

        Assert.NotNull(Current.PendingContent);
        Assert.True(Directory.Exists(txDir)); // evidence preserved
        Assert.Equal("changed by someone", await File.ReadAllTextAsync(Path.Combine(Mods, "a.jar"), Ct));
        var error = await Assert.ThrowsAsync<ServerOperationException>(() => _operations.StartAsync(Id, Ct));
        Assert.Equal(ServerOperationError.SettingsChangePending, error.Error);
    }

    [Fact]
    public async Task FailingPolicyRestoreAfterRollbackIsFinishedByRepair()
    {
        var plan = await _content.PlanInstallAsync(Id, "Mod", null, Ct);
        _engine.AfterMove = _ => throw new IOException("disk on fire");
        var failOnce = true;
        _runtime.PolicyBehavior = (_, policy) =>
        {
            if (policy == RestartPolicyKind.UnlessStopped && failOnce)
            {
                failOnce = false;
                throw new ServerOperationException(ServerOperationError.Docker, "daemon hiccup");
            }

            return Task.CompletedTask;
        };

        await Assert.ThrowsAsync<ServerOperationException>(() => _content.InstallAsync(Id, plan, null, Ct));
        Assert.Equal(ContentTxStep.RolledBack, Current.PendingContent!.Step); // evidence gone, but known to be undone
        Assert.Empty(Names(Mods));

        await _operations.RepairAsync(Id, Ct);

        Assert.Null(Current.PendingContent);
        Assert.Equal("policy:UnlessStopped", _runtime.Calls.Last(c => c.StartsWith("policy", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task RecoveryRefusesALinkPlantedWhileTheServerRan()
    {
        var (_, _) = await CrashMidwayAsync(ContentTxStep.Prepared);
        var elsewhere = Path.Combine(_temp.Path, "elsewhere");
        Directory.Move(Mods, elsewhere);
        Directory.CreateSymbolicLink(Mods, elsewhere);

        await Assert.ThrowsAsync<ContentRecoveryException>(() => _operations.RepairAsync(Id, Ct));

        Assert.NotNull(Current.PendingContent);
        Assert.Equal(["a.jar"], Directory.GetFiles(elsewhere).Select(f => Path.GetFileName(f)));
    }

    [Fact]
    public async Task VersionOnlyDependenciesProtectTheirLibrary()
    {
        _api.Add("Lib", "lib1", fileName: "lib.jar");
        _api.Add("User", "user1", fileName: "user.jar", deps: [new ModrinthDependency(null, "lib1", null, DependencyType.Required)]);
        var plan = await _content.PlanInstallAsync(Id, "User", null, Ct);
        await _content.InstallAsync(Id, plan, null, Ct);

        var remove = await Assert.ThrowsAsync<ContentException>(() => _content.RemoveAsync(Id, "lib.jar", false, Ct));
        Assert.Equal(ContentError.HasDependents, remove.Error);
        await Assert.ThrowsAsync<ContentException>(() => _content.SetEnabledAsync(Id, "lib.jar", false, Ct));
    }

    [Theory]
    [InlineData(ContentTxStep.Committed, "null-entry")]
    [InlineData(ContentTxStep.Prepared, "duplicate")]
    public async Task JournalsWithBadContentAreRejected(ContentTxStep step, string kind)
    {
        var (pending, _) = await CrashMidwayAsync(step);
        var bad = kind == "null-entry"
            ? pending with { LockAfter = new ContentLock(1, [null!]) }
            : pending with { Moves = [pending.Moves[0], pending.Moves[0]] };
        await _registry.SaveAsync(Current with { PendingContent = bad }, Ct);

        Assert.Null(_registry.Load(Id)!.Record);
    }

    [Fact]
    public async Task AMalformedJournalMakesTheRecordUnusable()
    {
        var json = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(_paths.RecordFile, Ct))!;
        json["PendingContent"] = System.Text.Json.Nodes.JsonNode.Parse(
            """{"TxId":"6c1b0f0e-8a9a-4c55-9a41-2a7b9c2f9d11","Directory":"../..","Moves":[],"LockAfter":{"SchemaVersion":1,"Entries":[]},"OriginalPolicy":"No","Step":"Prepared"}""");
        await File.WriteAllTextAsync(_paths.RecordFile, json.ToJsonString(), Ct);

        Assert.Null(_registry.Load(Id)!.Record);
    }

    [Fact]
    public async Task RemovalAndDisablingRespectDependents()
    {
        await InstallModAsync();

        var error = await Assert.ThrowsAsync<ContentException>(() => _content.RemoveAsync(Id, "fabric-api.jar", false, Ct));
        Assert.Equal(ContentError.HasDependents, error.Error);
        Assert.Equal(["Title Mod"], error.Subjects);
        await Assert.ThrowsAsync<ContentException>(() => _content.SetEnabledAsync(Id, "fabric-api.jar", false, Ct));

        // Disable the dependent first: then the library may go too.
        await _content.SetEnabledAsync(Id, "mod.jar", false, Ct);
        Assert.Equal(["fabric-api.jar"], Names(Mods));
        Assert.Equal(["mod.jar"], Names(Disabled));
        Assert.True(ContentLockStore.Load(_paths).Entries.Single(e => e.FileName == "mod.jar").Disabled);

        await _content.RemoveAsync(Id, "fabric-api.jar", false, Ct);
        Assert.Empty(Names(Mods));
        Assert.True(NoTxDirs);

        // Enabling the mod now fails: its dependency is gone.
        var enable = await Assert.ThrowsAsync<ContentException>(() => _content.SetEnabledAsync(Id, "mod.jar", true, Ct));
        Assert.Equal(ContentError.MissingDependencies, enable.Error);

        await _content.RemoveAsync(Id, "mod.jar", true, Ct);
        Assert.Empty(Names(Disabled));
        Assert.Empty(ContentLockStore.Load(_paths).Entries);
    }

    [Fact]
    public async Task EnablingBringsTheFileBack()
    {
        await InstallModAsync();
        await _content.SetEnabledAsync(Id, "mod.jar", false, Ct);
        await _content.SetEnabledAsync(Id, "mod.jar", true, Ct);

        Assert.Equal(["fabric-api.jar", "mod.jar"], Names(Mods));
        Assert.Empty(Names(Disabled));
    }

    [Fact]
    public async Task ALinkedModsDirectoryIsRefused()
    {
        var elsewhere = Path.Combine(_temp.Path, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        Directory.CreateSymbolicLink(Mods, elsewhere);

        await Assert.ThrowsAnyAsync<IOException>(() => _content.PlanInstallAsync(Id, "Mod", null, Ct)
            .ContinueWith(t => _content.InstallAsync(Id, t.Result, null, Ct), Ct).Unwrap());

        Assert.Empty(Directory.GetFiles(elsewhere));
    }

    [Fact]
    public async Task UntrackedFilesAreIdentifiedByHashOnly()
    {
        Directory.CreateDirectory(Mods);
        var known = _api.Versions.Single(v => v.Id == "api1");
        await File.WriteAllBytesAsync(Path.Combine(Mods, "renamed.jar"), _api.Bytes[known.Files[0].Url], Ct);
        await File.WriteAllTextAsync(Path.Combine(Mods, "fabric-api.jar"), "a fake with a famous name", Ct);

        var inventory = await _content.InventoryAsync(Id, Ct);

        Assert.Equal("Api", inventory.Find("renamed.jar", false)!.ProjectId);
        Assert.Null(inventory.Find("fabric-api.jar", false)!.ProjectId);
        Assert.True(inventory.HasUnverifiedActive);

        _api.FailHashLookup = true;
        Assert.True((await _content.InventoryAsync(Id, Ct)).IdentificationFailed);
    }

    [Fact]
    public async Task AModifiedTrackedFileLosesItsTrustedIdentity()
    {
        await InstallModAsync();
        await File.WriteAllTextAsync(Path.Combine(Mods, "fabric-api.jar"), "tampered", Ct);

        var item = (await _content.InventoryAsync(Id, Ct)).Find("fabric-api.jar", false)!;

        Assert.Equal(ItemState.Modified, item.State);
        Assert.Null(item.ProjectId);
    }

    private string Jar(string name, params (string Entry, string Content)[] entries)
    {
        var path = Path.Combine(_temp.Path, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entry, content) in entries)
        {
            using var w = new StreamWriter(zip.CreateEntry(entry).Open());
            w.Write(content);
        }

        return path;
    }

    [Fact]
    public async Task LocalFilesAreStagedInspectedAndInstalled()
    {
        var jar = Jar("my-mod.jar", ("fabric.mod.json", """{"id":"mine","name":"Mine","version":"2.0"}"""));

        var candidate = await _content.PrepareLocalAsync(Id, jar, Ct);
        Assert.Equal([LocalWarning.Unverified], candidate.Warnings);
        Assert.Equal("my-mod.jar", candidate.InstallName);
        await _content.InstallLocalAsync(Id, candidate, Ct);

        Assert.Equal(["my-mod.jar"], Names(Mods));
        var entry = Assert.Single(ContentLockStore.Load(_paths).Entries);
        Assert.Equal(ContentProvider.Local, entry.Provider);
        Assert.Equal("Mine", entry.Title);
        Assert.True((await _content.InventoryAsync(Id, Ct)).Find("my-mod.jar", false)!.IsLocal);
        Assert.True(NoTxDirs);
    }

    [Fact]
    public async Task LocalFilesOfTheWrongKindAreRefused()
    {
        var plugin = Jar("plugin.jar", ("plugin.yml", "name: P\n"));

        var error = await Assert.ThrowsAsync<ContentException>(() => _content.PrepareLocalAsync(Id, plugin, Ct));

        Assert.Equal(ContentError.WrongKind, error.Error);
        Assert.True(NoTxDirs);
    }

    [Fact]
    public async Task DiscardedLocalCandidateLeavesNothing()
    {
        var candidate = await _content.PrepareLocalAsync(Id, Jar("x.jar", ("fabric.mod.json", "{}")), Ct);
        await _content.DiscardLocalAsync(Id, candidate);

        Assert.True(NoTxDirs);
        Assert.Empty(Names(Mods));
    }

    [Fact]
    public async Task VanillaServersHaveNoContent()
    {
        var vanilla = ServerRecord.New(ServerTestData.Vanilla()) with { Phase = ProvisioningPhase.Ready };
        await _registry.CreateAsync(vanilla, Ct);

        var error = await Assert.ThrowsAsync<ContentException>(() => _content.InventoryAsync(vanilla.Definition.Id, Ct));
        Assert.Equal(ContentError.NotSupported, error.Error);
    }

    [Fact]
    public async Task LockStoreFallsBackToItsBackupAndRestoreSetsItAside()
    {
        await InstallModAsync();
        await _content.SetEnabledAsync(Id, "mod.jar", false, Ct); // second save → .bak exists
        await File.WriteAllTextAsync(ContentLockStore.PathFor(_paths), "{ broken", Ct);

        Assert.Equal(2, ContentLockStore.Load(_paths).Entries.Count);

        ContentLockStore.ResetAfterRestore(_paths);
        Assert.Empty(ContentLockStore.Load(_paths).Entries);
        Assert.True(File.Exists(Path.Combine(_paths.ServerDir, ContentLockStore.BeforeRestoreFileName)));
    }
}

[Collection(LocalizerCollection.Name)]
public sealed class ServerContentViewModelTests
{
    private sealed class RecordingModrinth : IModrinth
    {
        public List<(string Query, SearchSort Sort, int Offset)> Searches { get; } = [];

        public Task<SearchPage> SearchAsync(string query, ContentProfile profile, SearchSort sort, int offset, CancellationToken cancellationToken)
        {
            Searches.Add((query, sort, offset));
            var hit = new ModrinthProject($"P{offset}", "p", "T", "", null, null, 0, default, null, "required", null, [], []);
            return Task.FromResult(new SearchPage([hit], 10, offset));
        }

        public Task<ModrinthProject> GetProjectAsync(string projectId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<ModrinthVersion>> GetCompatibleVersionsAsync(string projectId, ContentProfile profile, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ModrinthVersion> GetVersionAsync(string versionId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyDictionary<string, ModrinthVersion>> GetVersionsByHashAsync(IReadOnlyList<string> sha512, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<string, ModrinthVersion>>(new Dictionary<string, ModrinthVersion>());
    }

    [Fact]
    public async Task LoadMoreKeepsTheOriginalQueryAndOrder()
    {
        using var temp = new TempDirectory();
        var app = AppPaths.Resolve(k => k == "HOME" ? temp.Path : null);
        Bonfire.Infrastructure.AppDirectories.Ensure(app);
        var registry = new ServerRegistry(app);
        var definition = ServerTestData.Vanilla() with { Type = ServerType.Paper, PaperBuild = 133 };
        await registry.CreateAsync(ServerRecord.New(definition) with { Phase = ProvisioningPhase.Ready }, TestContext.Current.CancellationToken);
        var runtime = new FakeServerRuntime();
        var operations = new ServerOperationsService(registry, runtime, new ServerCreationTests.SafeDocker(), NullLogger<ServerOperationsService>.Instance);
        var api = new RecordingModrinth();
        var content = new ContentService(registry, operations, runtime, new ContentEngine(registry, runtime, NullLogger<ContentEngine>.Instance),
            api, () => new ContentDownloader(), TimeProvider.System, NullLogger<ContentService>.Instance);
        var vm = new Bonfire.App.ViewModels.ServerContentViewModel(definition.Id, content, new TestDetails.Confirm(true), new TestDetails.NoPicker(),
            Bonfire.App.Localization.Localizer.Instance, NullLogger.Instance);

        Assert.Equal("plugins", vm.Profile!.Directory);
        vm.Query = "perms";
        vm.SortIndex = 1;
        await vm.SearchCommand.ExecuteAsync(null);
        vm.Query = "something else";
        vm.SortIndex = 2;
        await vm.MoreCommand.ExecuteAsync(null);

        Assert.Equal([("perms", SearchSort.Downloads, 0), ("perms", SearchSort.Downloads, 1)], api.Searches);
        Assert.Equal(["P0", "P1"], vm.Results.Select(r => r.Project.Id));
        Assert.True(vm.HasMore);
    }
}

[Collection(LocalizerCollection.Name)]
public sealed class ContentUpdateTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly ServerRegistry _registry;
    private readonly FakeServerRuntime _runtime = new();
    private readonly FakeModrinth _api = new();
    private readonly ContentService _content;
    private readonly ServerPaths _paths;
    private readonly Guid _id;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ContentUpdateTests()
    {
        var app = AppPaths.Resolve(k => k == "HOME" ? _temp.Path : null);
        Bonfire.Infrastructure.AppDirectories.Ensure(app);
        _registry = new ServerRegistry(app);
        var definition = ServerTestData.Vanilla() with { Type = ServerType.Fabric, FabricLoaderVersion = "0.19.5", FabricLauncherVersion = "1.1.2" };
        _id = definition.Id;
        _paths = _registry.CreateAsync(ServerRecord.New(definition) with { Phase = ProvisioningPhase.Ready, ContainerId = "c1", NetworkId = "n1" }).GetAwaiter().GetResult();
        _runtime.Containers.Add(FakeServerRuntime.State(_id, "exited"));
        var engine = new ContentEngine(_registry, _runtime, NullLogger<ContentEngine>.Instance);
        var operations = new ServerOperationsService(_registry, _runtime, new ServerCreationTests.SafeDocker(), NullLogger<ServerOperationsService>.Instance, null, engine);
        _content = new ContentService(_registry, operations, _runtime, engine, _api, () => new ContentDownloader(new FakeModrinth.Cdn(_api)),
            TimeProvider.System, NullLogger<ContentService>.Instance);
    }

    public void Dispose() => _temp.Dispose();

    private string Mods => Path.Combine(_paths.DataDir, "mods");

    private string[] Names() => Directory.GetFiles(Mods).Select(f => Path.GetFileName(f)).Order(StringComparer.Ordinal).ToArray();

    private async Task InstallAsync(string project, string? version = null)
    {
        var plan = await _content.PlanInstallAsync(_id, project, version, Ct);
        Assert.True(plan.CanInstall, string.Join(",", plan.Problems));
        await _content.InstallAsync(_id, plan, null, Ct);
    }

    private async Task<UpdateInfo> CheckAsync(string project, bool prereleases = false) =>
        (await _content.CheckUpdatesAsync(_id, prereleases, Ct)).Single(u => u.Item.ProjectId == project);

    [Fact]
    public async Task OnlyNewerReleasesAreOfferedUnlessPrereleasesAreAllowed()
    {
        _api.Add("Mod", "m1", day: 1, fileName: "mod.jar");
        await InstallAsync("Mod");
        Assert.Equal(UpdateStatus.UpToDate, (await CheckAsync("Mod")).Status);

        _api.Add("Mod", "m0", day: 0 + 1, type: "release", fileName: "mod.jar"); // same day: not newer
        _api.Add("Mod", "m2beta", day: 3, type: "beta", fileName: "mod.jar");
        Assert.Equal(UpdateStatus.UpToDate, (await CheckAsync("Mod")).Status);
        Assert.Equal("m2beta", (await CheckAsync("Mod", prereleases: true)).Latest!.Id);

        _api.Add("Mod", "m3", day: 5, fileName: "mod.jar");
        var update = await CheckAsync("Mod");
        Assert.Equal(UpdateStatus.Available, update.Status);
        Assert.Equal("m3", update.Latest!.Id);
    }

    [Fact]
    public async Task VanishedVersionsAndFailedChecksAreNotReportedAsUpToDate()
    {
        _api.Add("Gone", "g1", fileName: "gone.jar");
        _api.Add("Flaky", "f1", fileName: "flaky.jar");
        await InstallAsync("Gone");
        await InstallAsync("Flaky");
        _api.Versions.RemoveAll(v => v.Id == "g1");
        _api.Add("Gone", "g0", day: 9, fileName: "gone.jar"); // only some other version is offered now
        _api.FailVersionsOf.Add("Flaky");

        Assert.Equal(UpdateStatus.CurrentUnavailable, (await CheckAsync("Gone")).Status);
        Assert.Equal(UpdateStatus.CheckFailed, (await CheckAsync("Flaky")).Status);
    }

    [Fact]
    public async Task UpdateReplacesTheOldFileAndAddsNewDependencies()
    {
        _api.Add("Mod", "m1", day: 1, fileName: "mod.jar");
        await InstallAsync("Mod");
        _api.Add("Lib", "l1", fileName: "lib.jar");
        _api.Add("Mod", "m2", day: 5, fileName: "mod.jar", deps: [FakeModrinth.Requires("Lib")]);

        var plan = await _content.PlanUpdateAsync(_id, [await CheckAsync("Mod")], Ct);

        Assert.True(plan.CanInstall, string.Join(",", plan.Problems));
        Assert.Equal(["mod.jar"], plan.ReplacedItems.Select(i => i.FileName));
        Assert.Equal(["Mod-m2.jar", "lib.jar"], plan.Items.Select(i => i.InstallName)); // the old name is never reused
        await _content.InstallAsync(_id, plan, null, Ct);

        Assert.Equal(["Mod-m2.jar", "lib.jar"], Names());
        var entries = ContentLockStore.Load(_paths).Entries;
        Assert.Equal(["m2", "l1"], entries.Select(e => e.VersionId));
        Assert.Equal(UpdateStatus.UpToDate, (await CheckAsync("Mod")).Status);
    }

    [Fact]
    public async Task FilesThatStayKeepTheirExactRequirements()
    {
        _api.Add("Lib", "l1", day: 1, fileName: "lib.jar");
        _api.Add("User", "u1", fileName: "user.jar", deps: [FakeModrinth.Requires("Lib", "l1")]);
        await InstallAsync("User");
        _api.Add("Lib", "l2", day: 5, fileName: "lib.jar");

        var plan = await _content.PlanUpdateAsync(_id, [await CheckAsync("Lib")], Ct);

        Assert.Contains(plan.Problems, p => p.Kind == PlanProblemKind.InstalledVersionConflict);
        Assert.False(plan.CanInstall);
    }

    [Fact]
    public async Task VersionOnlyRequirementsBlockReplacingTheirVersion()
    {
        _api.Add("Lib", "l1", day: 1, fileName: "lib.jar");
        _api.Add("User", "u1", fileName: "user.jar", deps: [new ModrinthDependency(null, "l1", null, DependencyType.Required)]);
        await InstallAsync("User");
        _api.Add("Lib", "l2", day: 5, fileName: "lib.jar");

        var plan = await _content.PlanUpdateAsync(_id, [await CheckAsync("Lib")], Ct);

        Assert.Contains(plan.Problems, p => p.Kind == PlanProblemKind.InstalledVersionConflict);
    }

    [Fact]
    public async Task ViewModelDropsOffersWhenThePrereleaseOptionChangesAndStaysHonest()
    {
        _api.Add("Mod", "m1", day: 1, fileName: "mod.jar");
        _api.Add("Gone", "g1", fileName: "gone.jar");
        await InstallAsync("Mod");
        await InstallAsync("Gone");
        _api.Versions.RemoveAll(v => v.Id == "g1");
        _api.Add("Mod", "m2beta", day: 5, type: "beta", fileName: "mod.jar");
        var vm = new Bonfire.App.ViewModels.ServerContentViewModel(_id, _content, new TestDetails.Confirm(true), new TestDetails.NoPicker(),
            Bonfire.App.Localization.Localizer.Instance, NullLogger.Instance);
        Bonfire.App.Localization.Localizer.Instance.SetLanguage(Bonfire.Core.AppLanguage.English);

        await vm.CheckUpdatesCommand.ExecuteAsync(null);
        Assert.Equal(0, vm.UpdatesAvailable);
        Assert.DoesNotContain("up to date", vm.ResultMessage, StringComparison.OrdinalIgnoreCase); // "Gone" could not be compared

        vm.IncludePrereleases = true;
        await vm.CheckUpdatesCommand.ExecuteAsync(null);
        Assert.Equal(1, vm.UpdatesAvailable);

        var stale = vm.Items.Single(i => i.CanUpdate); // e.g. still on screen while a check was running
        vm.IncludePrereleases = false; // the beta offer must disappear
        Assert.Equal(0, vm.UpdatesAvailable);
        Assert.False(vm.UpdateAllCommand.CanExecute(null) && vm.HasUpdates);
        await vm.UpdateCommand.ExecuteAsync(stale);
        Assert.Equal(["gone.jar", "mod.jar"], Names()); // the stale beta offer was not installed
    }

    [Fact]
    public async Task PinsBlockDirectAndDependencyDrivenUpdates()
    {
        _api.Add("Lib", "l1", day: 1, fileName: "lib.jar");
        _api.Add("Mod", "m1", day: 1, fileName: "mod.jar", deps: [FakeModrinth.Requires("Lib")]);
        await InstallAsync("Mod");
        _runtime.Containers[0] = FakeServerRuntime.State(_id, "running");
        await _content.SetPinnedAsync(_id, "lib.jar", false, true, Ct); // metadata only: allowed while running
        _runtime.Containers[0] = FakeServerRuntime.State(_id, "exited");
        _api.Add("Lib", "l2", day: 5, fileName: "lib.jar");
        _api.Add("Mod", "m2", day: 5, fileName: "mod.jar", deps: [FakeModrinth.Requires("Lib", "l2")]);

        var lib = await CheckAsync("Lib");
        Assert.Equal(UpdateStatus.Pinned, lib.Status);
        var forced = lib with { Latest = _api.Versions.Single(v => v.Id == "l2") };
        var pinned = await Assert.ThrowsAsync<ContentException>(() => _content.PlanUpdateAsync(_id, [forced], Ct));
        Assert.Equal(ContentError.Pinned, pinned.Error);

        // Updating Mod would need Lib l2: the pinned l1 stays, so the plan is blocked.
        var plan = await _content.PlanUpdateAsync(_id, [await CheckAsync("Mod")], Ct);
        Assert.Contains(plan.Problems, p => p.Kind == PlanProblemKind.InstalledVersionConflict);

        await _content.SetPinnedAsync(_id, "lib.jar", false, false, Ct);
        Assert.Equal(UpdateStatus.Available, (await CheckAsync("Lib")).Status);
    }

    [Fact]
    public async Task OnlyIdentifiedFilesCanBePinnedAndDetectedOnesAreAdopted()
    {
        Directory.CreateDirectory(Mods);
        await File.WriteAllTextAsync(Path.Combine(Mods, "mystery.jar"), "?", Ct);
        var known = _api.Add("Lib", "l1", fileName: "lib.jar");
        await File.WriteAllBytesAsync(Path.Combine(Mods, "copied.jar"), _api.Bytes[known.Files[0].Url], Ct);

        var error = await Assert.ThrowsAsync<ContentException>(() => _content.SetPinnedAsync(_id, "mystery.jar", false, true, Ct));
        Assert.Equal(ContentError.Unidentified, error.Error);

        await _content.SetPinnedAsync(_id, "copied.jar", false, true, Ct);
        var entry = Assert.Single(ContentLockStore.Load(_paths).Entries);
        Assert.Equal((ContentOrigin.Detected, true), (entry.Origin, entry.Pinned));
    }

    [Fact]
    public async Task AStaleUpdateCheckIsRefused()
    {
        _api.Add("Mod", "m1", day: 1, fileName: "mod.jar");
        await InstallAsync("Mod");
        _api.Add("Mod", "m2", day: 5, fileName: "mod.jar");
        var update = await CheckAsync("Mod");
        await _content.RemoveAsync(_id, "mod.jar", false, Ct);

        var error = await Assert.ThrowsAsync<ContentException>(() => _content.PlanUpdateAsync(_id, [update], Ct));
        Assert.Equal(ContentError.PlanStale, error.Error);
    }
}

public sealed class ContentHistoryTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly ServerRegistry _registry;
    private readonly FakeServerRuntime _runtime = new();
    private readonly FakeModrinth _api = new();
    private readonly ContentEngine _engine;
    private readonly ServerOperationsService _operations;
    private readonly ContentService _content;
    private readonly ServerPaths _paths;
    private readonly Guid _id;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ContentHistoryTests()
    {
        var app = AppPaths.Resolve(k => k == "HOME" ? _temp.Path : null);
        Bonfire.Infrastructure.AppDirectories.Ensure(app);
        _registry = new ServerRegistry(app);
        var definition = ServerTestData.Vanilla() with { Type = ServerType.Fabric, FabricLoaderVersion = "0.19.5", FabricLauncherVersion = "1.1.2" };
        _id = definition.Id;
        _paths = _registry.CreateAsync(ServerRecord.New(definition) with { Phase = ProvisioningPhase.Ready, ContainerId = "c1", NetworkId = "n1" }).GetAwaiter().GetResult();
        _runtime.Containers.Add(FakeServerRuntime.State(_id, "exited"));
        _engine = new ContentEngine(_registry, _runtime, NullLogger<ContentEngine>.Instance);
        _operations = new ServerOperationsService(_registry, _runtime, new ServerCreationTests.SafeDocker(), NullLogger<ServerOperationsService>.Instance, null, _engine);
        _content = new ContentService(_registry, _operations, _runtime, _engine, _api, () => new ContentDownloader(new FakeModrinth.Cdn(_api)),
            TimeProvider.System, NullLogger<ContentService>.Instance);
        _api.Add("A", "a1", day: 1, fileName: "a.jar");
        _api.Add("B", "b1", day: 1, fileName: "b.jar");
    }

    public void Dispose() => _temp.Dispose();

    private string Mods => Path.Combine(_paths.DataDir, "mods");
    private string Disabled => Path.Combine(_paths.ServerDir, "disabled-content", "mods");
    private ServerRecord Current => _registry.Load(_id)!.Record!;

    private static string[] Names(string dir) =>
        Directory.Exists(dir) ? Directory.GetFiles(dir).Select(f => Path.GetFileName(f)).Order(StringComparer.Ordinal).ToArray() : [];

    private async Task InstallAsync(string project)
    {
        var plan = await _content.PlanInstallAsync(_id, project, null, Ct);
        await _content.InstallAsync(_id, plan, null, Ct);
    }

    private async Task UndoNewestAsync()
    {
        var history = _content.History(_id);
        Assert.True(history.CanUndoNewest, history.NewestBlocker?.ToString());
        await _content.UndoAsync(_id, history.Entries[0].TxId, Ct);
    }

    [Fact]
    public async Task InstallsAreUndoneNewestFirst()
    {
        await InstallAsync("A");
        await InstallAsync("B");
        var history = _content.History(_id);
        Assert.Equal([ContentChangeKind.Install, ContentChangeKind.Install], history.Entries.Select(e => e.Kind));
        Assert.True(history.Entries[0].Sequence > history.Entries[1].Sequence);

        await UndoNewestAsync();
        Assert.Equal(["a.jar"], Names(Mods));
        await UndoNewestAsync(); // the previous entry became the newest and matches the state
        Assert.Empty(Names(Mods));
        Assert.Empty(ContentLockStore.Load(_paths).Entries);
        Assert.Empty(_content.History(_id).Entries);
        Assert.Null(Current.PendingContent);
        Assert.False(Directory.EnumerateDirectories(_paths.ServerDir, "content-tx-*").Any());
    }

    [Fact]
    public async Task RemovalUpdateAndDisableAreUndone()
    {
        await InstallAsync("A");
        var originalSha = Convert.ToHexStringLower(SHA512.HashData(await File.ReadAllBytesAsync(Path.Combine(Mods, "a.jar"), Ct)));

        await _content.RemoveAsync(_id, "a.jar", false, Ct);
        Assert.Empty(Names(Mods));
        await UndoNewestAsync();
        Assert.Equal(["a.jar"], Names(Mods)); // back from the archive, byte for byte
        Assert.Equal(originalSha, Convert.ToHexStringLower(SHA512.HashData(await File.ReadAllBytesAsync(Path.Combine(Mods, "a.jar"), Ct))));

        _api.Add("A", "a2", day: 5, fileName: "a.jar");
        var update = (await _content.CheckUpdatesAsync(_id, false, Ct)).Single();
        await _content.InstallAsync(_id, await _content.PlanUpdateAsync(_id, [update], Ct), null, Ct);
        Assert.Equal(["A-a2.jar"], Names(Mods));
        Assert.Equal(ContentChangeKind.Update, _content.History(_id).Entries[0].Kind);
        await UndoNewestAsync();
        Assert.Equal(["a.jar"], Names(Mods));
        Assert.Equal("a1", ContentLockStore.Load(_paths).Entries.Single().VersionId);

        await _content.SetEnabledAsync(_id, "a.jar", false, Ct);
        await UndoNewestAsync();
        Assert.Equal(["a.jar"], Names(Mods));
        Assert.Empty(Names(Disabled));
    }

    [Fact]
    public async Task AnyLaterChangeBlocksUndoWithoutSkippingToOlderEntries()
    {
        await InstallAsync("A");
        await InstallAsync("B");
        await _content.SetPinnedAsync(_id, "a.jar", false, true, Ct);

        var history = _content.History(_id);
        Assert.Equal(UndoBlocker.StateChanged, history.NewestBlocker);
        var error = await Assert.ThrowsAsync<ContentException>(() => _content.UndoAsync(_id, history.Entries[0].TxId, Ct));
        Assert.Equal(ContentError.UndoUnavailable, error.Error);
        var older = await Assert.ThrowsAsync<ContentException>(() => _content.UndoAsync(_id, history.Entries[1].TxId, Ct));
        Assert.Equal(ContentError.PlanStale, older.Error);

        await _content.SetPinnedAsync(_id, "a.jar", false, false, Ct);
        Assert.True(_content.History(_id).CanUndoNewest);
        await File.WriteAllTextAsync(Path.Combine(Mods, "extra.jar"), "x", Ct); // an unexpected file
        Assert.Equal(UndoBlocker.StateChanged, _content.History(_id).NewestBlocker);
    }

    [Fact]
    public async Task CrashBeforePublishingIsFinishedByRepair()
    {
        _engine.BeforePublish = () => throw new IOException("power loss");
        await Assert.ThrowsAsync<IOException>(() => InstallAsync("A"));
        Assert.Equal(ContentTxStep.Committed, Current.PendingContent!.Step);
        await Assert.ThrowsAsync<ServerOperationException>(() => _operations.StartAsync(_id, Ct));

        _engine.BeforePublish = null;
        await _operations.RepairAsync(_id, Ct);

        Assert.Null(Current.PendingContent);
        Assert.True(_content.History(_id).CanUndoNewest);
    }

    [Fact]
    public async Task CrashAfterArchivingTheRemovedFileResumesPublication()
    {
        await InstallAsync("A");
        _engine.BeforePublish = () => throw new IOException("power loss");
        await Assert.ThrowsAsync<IOException>(() => _content.RemoveAsync(_id, "a.jar", false, Ct));
        var tx = Current.PendingContent!.TxId;
        var txDir = Path.Combine(_paths.ServerDir, "content-tx-" + tx.ToString("N"));
        Directory.CreateDirectory(Path.Combine(txDir, "hist"));
        Directory.Move(Path.Combine(txDir, "out"), Path.Combine(txDir, "hist", "files")); // the step that ran before the crash

        _engine.BeforePublish = null;
        await _operations.RepairAsync(_id, Ct);

        Assert.True(_content.History(_id).CanUndoNewest);
        await UndoNewestAsync();
        Assert.Equal(["a.jar"], Names(Mods));
    }

    [Fact]
    public async Task ChangesAfterACrashDoNotBecomeTheUndoBaseline()
    {
        _engine.BeforePublish = () => throw new IOException("power loss");
        await Assert.ThrowsAsync<IOException>(() => InstallAsync("A"));
        await File.WriteAllTextAsync(Path.Combine(Mods, "extra.jar"), "added later", Ct);

        _engine.BeforePublish = null;
        await _operations.RepairAsync(_id, Ct);

        Assert.Equal(UndoBlocker.StateChanged, _content.History(_id).NewestBlocker);
    }

    [Fact]
    public async Task APublishedEntryThatDoesNotMatchItsJournalIsRefused()
    {
        await InstallAsync("A");
        _engine.BeforePublish = () => throw new IOException("power loss");
        await Assert.ThrowsAsync<IOException>(() => _content.RemoveAsync(_id, "a.jar", false, Ct));
        var pending = Current.PendingContent!;

        // A structurally valid entry with this id but different undo instructions.
        var forged = new HistoryEntry(pending.TxId, pending.Sequence, DateTimeOffset.UtcNow, ContentChangeKind.Disable, pending.Summary!, "mods",
            [new ContentMove(ContentLocation.Live, ContentLocation.Disabled, "a.jar", pending.Moves[0].Sha512)], ContentLock.Empty, pending.StateAfter!);
        var dir = ContentHistoryStore.EntryDir(_paths, pending.TxId);
        Directory.CreateDirectory(Path.Combine(dir, "files"));
        // The real preimage is there, so only the metadata comparison can reject the entry.
        File.Copy(Path.Combine(_paths.ServerDir, "content-tx-" + pending.TxId.ToString("N"), "out", "a.jar"), Path.Combine(dir, "files", "a.jar"));
        await File.WriteAllTextAsync(Path.Combine(dir, "entry.json"), System.Text.Json.JsonSerializer.Serialize(forged,
            new System.Text.Json.JsonSerializerOptions { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } }), Ct);

        _engine.BeforePublish = null;
        await Assert.ThrowsAsync<IOException>(() => _operations.RepairAsync(_id, Ct));
        Assert.NotNull(Current.PendingContent);
    }

    [Fact]
    public async Task AJournalWithANullMoveOnlyMakesThatServerUnavailable()
    {
        await InstallAsync("A");
        var json = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(_paths.RecordFile, Ct))!;
        json["PendingContent"] = System.Text.Json.Nodes.JsonNode.Parse(
            """{"TxId":"6c1b0f0e-8a9a-4c55-9a41-2a7b9c2f9d11","Directory":"mods","Moves":[null],"LockAfter":{"SchemaVersion":1,"Entries":[]},"OriginalPolicy":"No","Step":"Prepared"}""");
        await File.WriteAllTextAsync(_paths.RecordFile, json.ToJsonString(), Ct);

        var all = _registry.LoadAll();
        Assert.Null(Assert.Single(all).Record);
    }

    [Fact]
    public async Task ATamperedPreimageIsNeverPublished()
    {
        await InstallAsync("A");
        _engine.BeforePublish = () => throw new IOException("power loss");
        await Assert.ThrowsAsync<IOException>(() => _content.RemoveAsync(_id, "a.jar", false, Ct));
        var tx = Current.PendingContent!.TxId;
        await File.WriteAllTextAsync(Path.Combine(_paths.ServerDir, "content-tx-" + tx.ToString("N"), "out", "a.jar"), "tampered", Ct);

        _engine.BeforePublish = null;
        await Assert.ThrowsAsync<IOException>(() => _operations.RepairAsync(_id, Ct));

        Assert.NotNull(Current.PendingContent); // kept: start stays blocked
        Assert.Equal(ContentChangeKind.Install, _content.History(_id).Entries[0].Kind); // the remove was not published
    }

    [Fact]
    public async Task AFailedUndoRollsBackAndStaysUndoable()
    {
        await InstallAsync("A");
        await _content.RemoveAsync(_id, "a.jar", false, Ct);
        _engine.AfterMove = _ => throw new IOException("disk on fire");

        await Assert.ThrowsAsync<IOException>(() => UndoNewestAsync());

        _engine.AfterMove = null;
        Assert.Empty(Names(Mods));
        Assert.Null(Current.PendingContent);
        Assert.True(_content.History(_id).CanUndoNewest); // the archive was put back
        await UndoNewestAsync();
        Assert.Equal(["a.jar"], Names(Mods));
    }

    [Fact]
    public async Task RetentionKeepsTheNewestTen()
    {
        for (var i = 0; i < 6; i++)
        {
            await InstallAsync("A");
            await _content.RemoveAsync(_id, "a.jar", false, Ct);
        }

        var history = _content.History(_id);
        Assert.Equal(ContentHistoryStore.KeepEntries, history.Entries.Count);
        Assert.Equal(ContentChangeKind.Remove, history.Entries[0].Kind);
        Assert.Equal(ContentHistoryStore.KeepEntries, Directory.GetDirectories(ContentHistoryStore.Root(_paths)).Length);
    }

    [Fact]
    public async Task DamagedEntriesAreIgnored()
    {
        await InstallAsync("A");
        var dir = ContentHistoryStore.EntryDir(_paths, _content.History(_id).Entries[0].TxId);
        await File.WriteAllTextAsync(Path.Combine(dir, "entry.json"), "{ broken", Ct);

        Assert.Empty(_content.History(_id).Entries);
    }
}
