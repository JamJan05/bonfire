using System.Formats.Tar;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bonfire.App.Services;
using Bonfire.Core;
using Bonfire.Core.Backups;
using Bonfire.Core.Servers;
using Bonfire.Infrastructure.Backups;
using Bonfire.Infrastructure.Servers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bonfire.Tests;

public sealed class BackupTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly AppPaths _app;
    private readonly ServerRegistry _registry;
    private readonly BackupStore _store;
    private readonly FakeServerRuntime _runtime = new();
    private readonly RestoreEngine _restore;
    private readonly ServerRecord _record;
    private readonly ServerPaths _paths;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public BackupTests()
    {
        _app = AppPaths.Resolve(k => k == "HOME" ? _temp.Path : null);
        Bonfire.Infrastructure.AppDirectories.Ensure(_app);
        _registry = new ServerRegistry(_app);
        _store = new BackupStore(_app);
        _record = ServerRecord.New(ServerTestData.Vanilla()) with { Phase = ProvisioningPhase.Ready, ContainerId = "c1", NetworkId = "n1" };
        _paths = _registry.CreateAsync(_record).GetAwaiter().GetResult();
        _restore = new RestoreEngine(_registry, _runtime, TimeProvider.System, NullLogger<RestoreEngine>.Instance);

        Directory.CreateDirectory(Path.Combine(_paths.DataDir, "world", "region"));
        File.WriteAllText(Path.Combine(_paths.DataDir, "server.properties"), "motd=original\n");
        File.WriteAllBytes(Path.Combine(_paths.DataDir, "world", "level.dat"), [1, 2, 3, 4]);
        File.WriteAllBytes(Path.Combine(_paths.DataDir, "world", "region", "r.0.0.mca"), new byte[200_000]);
        File.WriteAllText(Path.Combine(_paths.DataDir, "world", "empty.txt"), "");
        File.WriteAllText(Path.Combine(_paths.DataDir, "zażółć.json"), "{}");
        Directory.CreateDirectory(Path.Combine(_paths.DataDir, "mods")); // empty directory
    }

    public void Dispose() => _temp.Dispose();

    private Task<BackupInfo> BackupAsync(BackupKind kind = BackupKind.Manual) =>
        _store.CreateAsync(_record.Definition, _paths, kind, TimeProvider.System, null, Ct);

    private static string Snapshot(string dir) => string.Join("\n",
        Directory.EnumerateFileSystemEntries(dir, "*", SearchOption.AllDirectories)
            .Select(p => (Path.GetRelativePath(dir, p), File.Exists(p) ? Convert.ToHexString(File.ReadAllBytes(p)) : "<dir>"))
            .OrderBy(x => x.Item1, StringComparer.Ordinal)
            .Select(x => $"{x.Item1}={x.Item2}"));

    [Fact]
    public async Task BackupRoundTripsExactly()
    {
        var before = Snapshot(_paths.DataDir);
        var backup = await BackupAsync();

        Assert.True(backup.IsUsable);
        Assert.Equal(5, backup.FileCount);
        Assert.Single(_store.List(_record.Definition.Id));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(backup.Directory, BackupStore.ArchiveName)));

        var target = Path.Combine(_temp.Path, "extracted");
        await BackupStore.ExtractVerifiedAsync(backup, target, null, Ct);

        Assert.Equal(before, Snapshot(target));
    }

    [Fact]
    public async Task SymlinkInDataFailsTheBackupInsteadOfSkipping()
    {
        File.CreateSymbolicLink(Path.Combine(_paths.DataDir, "world", "evil"), "/etc/passwd");

        await Assert.ThrowsAsync<Bonfire.Infrastructure.Linux.UnsupportedEntryException>(() => BackupAsync());
        Assert.Empty(_store.List(_record.Definition.Id));
        Assert.Empty(Directory.EnumerateDirectories(_store.BackupsDirFor(_record.Definition.Id))); // no leftovers
    }

    [Fact]
    public async Task FifoInDataFailsTheBackup()
    {
        using (var p = System.Diagnostics.Process.Start("mkfifo", Path.Combine(_paths.DataDir, "pipe"))!)
        {
            await p.WaitForExitAsync(Ct);
        }

        await Assert.ThrowsAsync<Bonfire.Infrastructure.Linux.UnsupportedEntryException>(() => BackupAsync());
    }

    [Fact]
    public async Task FileTypeComesFromTheOpenedDescriptor()
    {
        using var temp = new TempDirectory();
        var fifo = Path.Combine(temp.Path, "fifo");
        using (var p = System.Diagnostics.Process.Start("mkfifo", fifo)!)
        {
            await p.WaitForExitAsync(Ct);
        }

        uint TypeOf(string path)
        {
            var fd = Bonfire.Infrastructure.Linux.LibC.Open(path,
                Bonfire.Infrastructure.Linux.LibC.O_RDONLY | Bonfire.Infrastructure.Linux.LibC.O_NONBLOCK | Bonfire.Infrastructure.Linux.LibC.O_CLOEXEC, 0);
            Assert.True(fd >= 0, path);
            using var handle = new Microsoft.Win32.SafeHandles.SafeFileHandle(fd, true);
            return Bonfire.Infrastructure.Linux.LibC.FileType(fd);
        }

        Assert.Equal(0x8000u, TypeOf(Path.Combine(_paths.DataDir, "server.properties"))); // regular
        Assert.Equal(0x2000u, TypeOf("/dev/null"));                                    // character device
        Assert.Equal(0x1000u, TypeOf(fifo));                                           // FIFO
        Assert.Equal(0x4000u, TypeOf(temp.Path));                                      // directory
        Assert.Throws<Bonfire.Infrastructure.Linux.UnsupportedEntryException>(() =>
            Bonfire.Infrastructure.Linux.SafeFiles.OpenRegularFile("/dev/null", "dev"));
    }

    [Fact]
    public async Task MalformedSidecarDoesNotBreakTheList()
    {
        var good = await BackupAsync();
        var broken = Path.Combine(_store.BackupsDirFor(_record.Definition.Id), "20000101-000000-manual-broken");
        Directory.CreateDirectory(broken);
        System.IO.File.WriteAllText(Path.Combine(broken, BackupStore.RecordName), "{}");
        // A complete-looking record with a null directory and a bad file hash is unusable too.
        var json = System.Text.Json.Nodes.JsonNode.Parse(System.IO.File.ReadAllText(Path.Combine(good.Directory, BackupStore.RecordName)))!;
        json["Manifest"]!["Directories"]!.AsArray().Add(null);
        json["Manifest"]!["Files"]![0]!["Sha256"] = "not-a-hash";
        var subtle = Path.Combine(_store.BackupsDirFor(_record.Definition.Id), "20000101-000001-manual-subtle");
        Directory.CreateDirectory(subtle);
        System.IO.File.Copy(Path.Combine(good.Directory, BackupStore.ArchiveName), Path.Combine(subtle, BackupStore.ArchiveName));
        System.IO.File.WriteAllText(Path.Combine(subtle, BackupStore.RecordName), json.ToJsonString());

        var list = _store.List(_record.Definition.Id);

        Assert.Equal(3, list.Count);
        Assert.Single(list, b => b.IsUsable);
        Assert.Contains(list, b => b.BackupId == good.BackupId && b.IsUsable);
        Assert.NotNull(_store.Find(_record.Definition.Id, good.BackupId));
    }

    [Fact]
    public async Task RecoveryStopsARunningContainerBeforeTouchingData()
    {
        var backup = await BackupAsync();
        var pending = new PendingRestore(Guid.NewGuid(), backup.BackupId, backup.Record!.ArchiveSha256,
            "restore-staging-x", RestoreEngine.RollbackPrefix + "x", RestartPolicyKind.UnlessStopped, RestoreStep.Swapped);
        var record = _registry.Load(_record.Definition.Id)!.Record! with { PendingRestore = pending };
        await _registry.SaveAsync(record, Ct);
        _runtime.Containers.Add(FakeServerRuntime.State(_record.Definition.Id, "running")); // e.g. restarted by the daemon

        await _restore.RecoverAsync(record);

        Assert.Equal(["policy:No", "stop", "policy:UnlessStopped"], _runtime.Calls);
    }

    // ------------------------------------------------------------------ add-on state (format 2)

    private string LockPath => Bonfire.Infrastructure.Content.ContentLockStore.PathFor(_paths);
    private string DisabledMods => Path.Combine(_paths.ServerDir, "disabled-content", "mods");

    private static Bonfire.Core.Content.ContentLock LockWith(string fileName) =>
        new(1, [new Bonfire.Core.Content.LockEntry(Bonfire.Core.Content.ContentProvider.Local, null, null, fileName, null, fileName,
            new string('a', 128), 1, "26.3", "fabric", [], [], DateTimeOffset.UnixEpoch, true, Bonfire.Core.Content.ContentOrigin.Installed, true)]);

    private Task RestoreAsync(BackupInfo backup) => _restore.RestoreAsync(_registry.Load(_record.Definition.Id)!.Record!, backup, null, Ct);

    private ServerRecord Current => _registry.Load(_record.Definition.Id)!.Record!;

    [Fact]
    public async Task AddOnStateIsBackedUpAndRestoredWithTheData()
    {
        await Bonfire.Infrastructure.Content.ContentLockStore.SaveAsync(_paths, LockWith("old.jar"));
        Directory.CreateDirectory(DisabledMods);
        await System.IO.File.WriteAllTextAsync(Path.Combine(DisabledMods, "old.jar"), "disabled then", Ct);
        var backup = await BackupAsync();
        Assert.True(backup.Record!.Manifest.HasContent);

        // Later: other disabled files and another lock (with a .bak).
        System.IO.File.Delete(Path.Combine(DisabledMods, "old.jar"));
        await System.IO.File.WriteAllTextAsync(Path.Combine(DisabledMods, "new.jar"), "disabled now", Ct);
        await Bonfire.Infrastructure.Content.ContentLockStore.SaveAsync(_paths, LockWith("new.jar"));
        await Bonfire.Infrastructure.Content.ContentLockStore.SaveAsync(_paths, LockWith("newer.jar"));

        await RestoreAsync(backup);

        Assert.Equal("old.jar", Bonfire.Infrastructure.Content.ContentLockStore.Load(_paths).Entries.Single().FileName);
        Assert.False(System.IO.File.Exists(LockPath + ".bak")); // a later .bak can never resurrect newer metadata
        Assert.Equal(["old.jar"], Directory.GetFiles(DisabledMods).Select(f => Path.GetFileName(f)));
        Assert.Equal("disabled then", await System.IO.File.ReadAllTextAsync(Path.Combine(DisabledMods, "old.jar"), Ct));
        var aside = Directory.GetDirectories(_paths.ServerDir, RestoreEngine.RollbackPrefix + "*.content").Single();
        Assert.True(System.IO.File.Exists(Path.Combine(aside, "disabled-content", "mods", "new.jar")));
        Assert.Contains("newer.jar", await System.IO.File.ReadAllTextAsync(Path.Combine(aside, "content-lock.json"), Ct));
        Assert.True(System.IO.File.Exists(Path.Combine(aside, "content-lock.json.bak")));
        Assert.Empty(Directory.GetDirectories(_paths.ServerDir, RestoreEngine.ExtrasPrefix + "*"));
        Assert.Null(Current.PendingRestore);
    }

    [Fact]
    public async Task AnEmptyDisabledSnapshotEmptiesTheDisabledFiles()
    {
        var backup = await BackupAsync(); // no lock, no disabled-content at all
        Directory.CreateDirectory(DisabledMods);
        await System.IO.File.WriteAllTextAsync(Path.Combine(DisabledMods, "later.jar"), "x", Ct);
        await Bonfire.Infrastructure.Content.ContentLockStore.SaveAsync(_paths, LockWith("later.jar"));

        await RestoreAsync(backup);

        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_paths.ServerDir, "disabled-content")));
        Assert.False(System.IO.File.Exists(LockPath));
    }

    [Theory]
    [InlineData(RestoreStep.ContentAside)]
    [InlineData(RestoreStep.ContentApplied)]
    public async Task ACrashDuringTheAddOnStepsIsFinishedByRecovery(RestoreStep crashAfter)
    {
        await Bonfire.Infrastructure.Content.ContentLockStore.SaveAsync(_paths, LockWith("old.jar"));
        Directory.CreateDirectory(DisabledMods);
        await System.IO.File.WriteAllTextAsync(Path.Combine(DisabledMods, "old.jar"), "then", Ct);
        var backup = await BackupAsync();
        System.IO.File.Delete(Path.Combine(DisabledMods, "old.jar"));
        await System.IO.File.WriteAllTextAsync(Path.Combine(DisabledMods, "new.jar"), "now", Ct);
        _restore.AfterContentStep = step =>
        {
            if (step == crashAfter)
            {
                throw new IOException("power loss");
            }
        };

        await Assert.ThrowsAsync<IOException>(() => RestoreAsync(backup));
        Assert.Equal(crashAfter, Current.PendingRestore!.Step);
        Assert.True(Current.HasPendingOperation); // starting stays blocked until recovery

        _restore.AfterContentStep = null;
        await _restore.RecoverAsync(Current);

        Assert.Null(Current.PendingRestore);
        Assert.Equal(["old.jar"], Directory.GetFiles(DisabledMods).Select(f => Path.GetFileName(f)));
        Assert.Equal("old.jar", Bonfire.Infrastructure.Content.ContentLockStore.Load(_paths).Entries.Single().FileName);
        var aside = Directory.GetDirectories(_paths.ServerDir, RestoreEngine.RollbackPrefix + "*.content").Single();
        Assert.True(System.IO.File.Exists(Path.Combine(aside, "disabled-content", "mods", "new.jar"))); // nothing lost
    }

    [Fact]
    public async Task ADamagedLockFailsTheBackup()
    {
        await System.IO.File.WriteAllTextAsync(LockPath, "{ broken", Ct);

        await Assert.ThrowsAnyAsync<IOException>(() => BackupAsync());
        Assert.Empty(_store.List(_record.Definition.Id));
    }

    [Fact]
    public async Task UndeclaredAddOnEntriesMakeTheBackupCorrupt()
    {
        await Bonfire.Infrastructure.Content.ContentLockStore.SaveAsync(_paths, LockWith("old.jar"));
        var backup = await BackupAsync();
        var recordPath = Path.Combine(backup.Directory, BackupStore.RecordName);
        var json = System.Text.Json.Nodes.JsonNode.Parse(await System.IO.File.ReadAllTextAsync(recordPath, Ct))!;
        json["Manifest"]!["Extras"] = new System.Text.Json.Nodes.JsonArray(); // the archive still contains the lock
        await System.IO.File.WriteAllTextAsync(recordPath, json.ToJsonString(), Ct);

        var error = await Assert.ThrowsAsync<BackupException>(() => RestoreAsync(_store.Find(_record.Definition.Id, backup.BackupId)!));
        Assert.Equal(BackupError.Corrupt, error.Error);
        Assert.Contains("old.jar", await System.IO.File.ReadAllTextAsync(LockPath, Ct)); // nothing changed
    }

    [Fact]
    public async Task AnInvalidLockInsideABackupIsRejectedBeforeAnythingChanges()
    {
        await Bonfire.Infrastructure.Content.ContentLockStore.SaveAsync(_paths, LockWith("old.jar"));
        var backup = await BackupAsync();
        // Rewrite the archive so that its lock is invalid JSON but every recorded hash matches.
        var archive = Path.Combine(backup.Directory, BackupStore.ArchiveName);
        var bad = System.Text.Encoding.UTF8.GetBytes("{\"SchemaVersion\":1,\"Entries\":[null]}");
        var manifest = backup.Record!.Manifest;
        var entries = new List<(string Name, byte[]? Data)>();
        await using (var read = System.IO.File.OpenRead(archive))
        await using (var gz = new GZipStream(read, CompressionMode.Decompress))
        await using (var tar = new TarReader(gz))
        {
            while (await tar.GetNextEntryAsync(copyData: true, Ct) is { } e)
            {
                byte[]? data = null;
                if (e.DataStream is { } stream)
                {
                    using var ms = new MemoryStream();
                    await stream.CopyToAsync(ms, Ct);
                    data = ms.ToArray();
                }

                entries.Add((e.Name, e.Name == "bonfire/content-lock.json" ? bad : data));
            }
        }

        manifest = manifest with
        {
            Extras = manifest.Extras!.Select(f => f.Path == "content-lock.json"
                ? f with { Size = bad.Length, Sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bad)) } : f).ToList(),
        };
        await using (var file = System.IO.File.Create(archive))
        await using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
        await using (var tar = new TarWriter(gzip, TarEntryFormat.Pax))
        {
            foreach (var (name, data) in entries)
            {
                var entry = name.EndsWith('/') ? new PaxTarEntry(TarEntryType.Directory, name) : new PaxTarEntry(TarEntryType.RegularFile, name);
                if (data is not null && !name.EndsWith('/'))
                {
                    entry.DataStream = new MemoryStream(data);
                }

                await tar.WriteEntryAsync(entry, Ct);
            }
        }

        var bytes = await System.IO.File.ReadAllBytesAsync(archive, Ct);
        await System.IO.File.WriteAllTextAsync(Path.Combine(backup.Directory, BackupStore.RecordName), JsonSerializer.Serialize(
            new BackupRecord(manifest, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)), bytes.Length),
            new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } }), Ct);
        var dataBefore = Snapshot(_paths.DataDir);

        var error = await Assert.ThrowsAsync<BackupException>(() => RestoreAsync(_store.Find(_record.Definition.Id, backup.BackupId)!));

        Assert.Equal(BackupError.Corrupt, error.Error);
        Assert.Null(Current.PendingRestore);
        Assert.Equal(dataBefore, Snapshot(_paths.DataDir));
        Assert.Contains("old.jar", await System.IO.File.ReadAllTextAsync(LockPath, Ct));
        Assert.Empty(Directory.GetDirectories(_paths.ServerDir, RestoreEngine.ExtrasPrefix + "*"));
    }

    [Fact]
    public async Task OverflowingExtraSizesMakeABackupUnusableNotTheListing()
    {
        await Bonfire.Infrastructure.Content.ContentLockStore.SaveAsync(_paths, LockWith("old.jar"));
        Directory.CreateDirectory(DisabledMods);
        await System.IO.File.WriteAllTextAsync(Path.Combine(DisabledMods, "a.jar"), "a", Ct);
        var backup = await BackupAsync();
        var recordPath = Path.Combine(backup.Directory, BackupStore.RecordName);
        var json = System.Text.Json.Nodes.JsonNode.Parse(await System.IO.File.ReadAllTextAsync(recordPath, Ct))!;
        foreach (var extra in json["Manifest"]!["Extras"]!.AsArray())
        {
            extra!["Size"] = long.MaxValue;
        }

        await System.IO.File.WriteAllTextAsync(recordPath, json.ToJsonString(), Ct);

        var listed = Assert.Single(_store.List(_record.Definition.Id));
        Assert.False(listed.IsUsable);
    }

    [Fact]
    public async Task AFormatOneBackupMovesTheAddOnStateAsideInsteadOfMixingSnapshots()
    {
        // A backup as written before format 2: data only.
        var backup = await BackupAsync();
        var dir = backup.Directory;
        var archive = Path.Combine(dir, BackupStore.ArchiveName);
        var manifest = backup.Record!.Manifest with { FormatVersion = 1, Extras = null, ExtraDirectories = null, DisabledContentIncluded = false };
        await using (var file = System.IO.File.Create(archive))
        await using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
        await using (var tar = new TarWriter(gzip, TarEntryFormat.Pax))
        {
            foreach (var d in manifest.Directories)
            {
                await tar.WriteEntryAsync(new PaxTarEntry(TarEntryType.Directory, "data/" + d + "/"), Ct);
            }

            foreach (var f in manifest.Files)
            {
                await tar.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, "data/" + f.Path)
                {
                    DataStream = new MemoryStream(await System.IO.File.ReadAllBytesAsync(Path.Combine(_paths.DataDir, f.Path), Ct)),
                }, Ct);
            }

            await tar.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, "metadata/manifest.json")
            {
                DataStream = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(manifest)),
            }, Ct);
        }

        var bytes = await System.IO.File.ReadAllBytesAsync(archive, Ct);
        var record = new BackupRecord(manifest, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)), bytes.Length);
        await System.IO.File.WriteAllTextAsync(Path.Combine(dir, BackupStore.RecordName),
            JsonSerializer.Serialize(record, new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } }), Ct);
        var v1 = _store.Find(_record.Definition.Id, backup.BackupId)!;
        Assert.True(v1.IsUsable);
        Assert.False(v1.Record!.Manifest.HasContent);

        await Bonfire.Infrastructure.Content.ContentLockStore.SaveAsync(_paths, LockWith("now.jar"));
        Directory.CreateDirectory(DisabledMods);
        await System.IO.File.WriteAllTextAsync(Path.Combine(DisabledMods, "now.jar"), "x", Ct);

        await RestoreAsync(v1);

        Assert.False(System.IO.File.Exists(LockPath));
        Assert.False(Directory.Exists(Path.Combine(_paths.ServerDir, "disabled-content")));
        var aside = Directory.GetDirectories(_paths.ServerDir, RestoreEngine.RollbackPrefix + "*.content").Single();
        Assert.True(System.IO.File.Exists(Path.Combine(aside, "disabled-content", "mods", "now.jar")));
    }

    [Fact]
    public async Task RecoveryWithoutDockerKeepsThePendingRestore()
    {
        var pending = new PendingRestore(Guid.NewGuid(), Guid.NewGuid(), new string('a', 64),
            "restore-staging-x", RestoreEngine.RollbackPrefix + "x", RestartPolicyKind.No, RestoreStep.Extracted);
        var record = _registry.Load(_record.Definition.Id)!.Record! with { PendingRestore = pending };
        await _registry.SaveAsync(record, Ct);
        _runtime.List = _ => throw new HttpRequestException("docker down");

        await Assert.ThrowsAsync<HttpRequestException>(() => _restore.RecoverAsync(record));

        Assert.NotNull(_registry.Load(_record.Definition.Id)!.Record!.PendingRestore);
        Assert.True(Directory.Exists(_paths.DataDir));
    }

    [Fact]
    public async Task RepairNeverProvisionsOverAPendingRestore()
    {
        var pending = new PendingRestore(Guid.NewGuid(), Guid.NewGuid(), new string('a', 64),
            "restore-staging-x", RestoreEngine.RollbackPrefix + "x", RestartPolicyKind.No, RestoreStep.Extracted);
        await _registry.SaveAsync(_registry.Load(_record.Definition.Id)!.Record! with { PendingRestore = pending }, Ct);
        var withoutEngine = new ServerOperationsService(_registry, _runtime, new ServerCreationTests.SafeDocker(), NullLogger<ServerOperationsService>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => withoutEngine.RepairAsync(_record.Definition.Id, Ct));

        Assert.DoesNotContain("provision", _runtime.Calls);
    }

    [Fact]
    public async Task CancelledBackupLeavesNothingVisible()
    {
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress(_ => cts.Cancel());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _store.CreateAsync(_record.Definition, _paths, BackupKind.Manual, TimeProvider.System, progress, cts.Token));

        Assert.Empty(_store.List(_record.Definition.Id));
        Assert.Empty(Directory.EnumerateDirectories(_store.BackupsDirFor(_record.Definition.Id)));
    }

    [Fact]
    public async Task FlippedByteIsDetected()
    {
        var backup = await BackupAsync();
        var archive = Path.Combine(backup.Directory, BackupStore.ArchiveName);
        var bytes = File.ReadAllBytes(archive);
        bytes[bytes.Length / 2] ^= 0xFF;
        File.WriteAllBytes(archive, bytes);

        await Assert.ThrowsAsync<BackupException>(() => BackupStore.ExtractVerifiedAsync(backup, Path.Combine(_temp.Path, "x"), null, Ct));
    }

    [Fact]
    public async Task TruncatedArchiveIsNotUsable()
    {
        var backup = await BackupAsync();
        var archive = Path.Combine(backup.Directory, BackupStore.ArchiveName);
        File.WriteAllBytes(archive, File.ReadAllBytes(archive)[..100]);

        Assert.False(Assert.Single(_store.List(_record.Definition.Id)).IsUsable);
    }

    public static TheoryData<string> MaliciousEntries => new() { "../escape", "/abs", "data/../../escape", "data/link", "data/hard", "data/dup", "data/big", "data/extra" };

    [Theory]
    [MemberData(nameof(MaliciousEntries))]
    public async Task CraftedArchivesAreRejectedAndNothingLeaksOut(string kind)
    {
        var manifest = new BackupManifest(1, Guid.NewGuid(), _record.Definition.Id, "x", BackupKind.Manual, DateTimeOffset.UtcNow,
            ServerType.Vanilla, "26.3", null, null, null, [], [new BackupFile("ok.txt", 2, Sha("hi"))]);
        var archive = Path.Combine(_temp.Path, "evil.tar.gz");
        await using (var file = File.Create(archive))
        await using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
        await using (var tar = new TarWriter(gzip, TarEntryFormat.Pax))
        {
            await tar.WriteEntryAsync(Entry(("data/ok.txt"), "hi"), Ct);
            switch (kind)
            {
                case "data/link":
                    await tar.WriteEntryAsync(new PaxTarEntry(TarEntryType.SymbolicLink, "data/link") { LinkName = "/etc/passwd" }, Ct);
                    break;
                case "data/hard":
                    await tar.WriteEntryAsync(new PaxTarEntry(TarEntryType.HardLink, "data/hard") { LinkName = "data/ok.txt" }, Ct);
                    break;
                case "data/dup":
                    await tar.WriteEntryAsync(Entry("data/ok.txt", "hi"), Ct);
                    break;
                case "data/big":
                    break; // replaced below
                default:
                    await tar.WriteEntryAsync(Entry(kind, "pwned"), Ct);
                    break;
            }

            await tar.WriteEntryAsync(Entry("metadata/manifest.json", "{}"), Ct);
        }

        if (kind == "data/big")
        {
            manifest = manifest with { Files = [new BackupFile("ok.txt", 1, Sha("h"))] }; // declared shorter than the data
        }

        var target = Path.Combine(_temp.Path, "out", "staging");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        await Assert.ThrowsAsync<BackupException>(() => BackupStore.VerifyAsync(archive, manifest, target, null, Ct));
        Assert.False(System.IO.File.Exists(Path.Combine(_temp.Path, "escape")));
        Assert.False(System.IO.File.Exists("/abs"));

        static PaxTarEntry Entry(string name, string text) => new(TarEntryType.RegularFile, name)
        {
            DataStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text)),
        };
    }

    private static string Sha(string text) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));

    [Fact]
    public async Task RetentionKeepsTheNewestAndNeverTheLastGood()
    {
        for (var i = 0; i < 4; i++)
        {
            await BackupAsync();
            await Task.Delay(1100, Ct); // distinct timestamps in names
        }

        var removed = _store.ApplyRetention(_record.Definition.Id, BackupKind.Manual, keep: 2);

        Assert.Equal(2, removed.Count);
        Assert.Equal(2, _store.List(_record.Definition.Id).Count);
        _store.ApplyRetention(_record.Definition.Id, BackupKind.Manual, keep: 0);
        Assert.Single(_store.List(_record.Definition.Id)); // at least one usable backup always stays
    }

    [Fact]
    public async Task RestoreSwapsDataKeepsRollbackAndRestoresPolicy()
    {
        var before = Snapshot(_paths.DataDir);
        var backup = await BackupAsync();
        File.WriteAllText(Path.Combine(_paths.DataDir, "server.properties"), "motd=changed\n");
        File.Delete(Path.Combine(_paths.DataDir, "world", "level.dat"));
        var older = Path.Combine(_paths.ServerDir, RestoreEngine.RollbackPrefix + "old");
        Directory.CreateDirectory(older);
        var lockFile = Bonfire.Infrastructure.Content.ContentLockStore.PathFor(_paths);
        System.IO.File.WriteAllText(lockFile, "{}");
        var history = Bonfire.Infrastructure.Content.ContentHistoryStore.Root(_paths);
        Directory.CreateDirectory(Path.Combine(history, Guid.NewGuid().ToString("N")));

        await _restore.RestoreAsync(_registry.Load(_record.Definition.Id)!.Record!, backup, null, Ct);
        Assert.False(Directory.Exists(history)); // the history described the files before the restore

        // The backup had no lock: the current one went into the rollback set, next to the previous data.
        Assert.False(System.IO.File.Exists(lockFile));

        Assert.Equal(before, Snapshot(_paths.DataDir));
        var rollbackSet = Directory.EnumerateDirectories(_paths.ServerDir, RestoreEngine.RollbackPrefix + "*").Order(StringComparer.Ordinal).ToList();
        Assert.Equal(2, rollbackSet.Count); // previous data + previous add-on state
        var rollback = rollbackSet[0];
        Assert.Equal(rollback + ".content", rollbackSet[1]);
        Assert.Equal("{}", System.IO.File.ReadAllText(Path.Combine(rollbackSet[1], "content-lock.json")));
        Assert.Contains("changed", System.IO.File.ReadAllText(Path.Combine(rollback, "server.properties")));
        Assert.Equal("policy:No", _runtime.Calls[0]); // disabled before anything else
        Assert.Equal($"policy:{_record.Definition.RestartPolicy}", _runtime.Calls[^1]);
        Assert.Null(_registry.Load(_record.Definition.Id)!.Record!.PendingRestore);
    }

    [Fact]
    public async Task FailedRestoreChangesNothing()
    {
        var backup = await BackupAsync();
        var archive = Path.Combine(backup.Directory, BackupStore.ArchiveName);
        var bytes = System.IO.File.ReadAllBytes(archive);
        bytes[bytes.Length - 40] ^= 0xFF;
        System.IO.File.WriteAllBytes(archive, bytes);
        System.IO.File.WriteAllText(Path.Combine(_paths.DataDir, "marker"), "current");
        var current = Snapshot(_paths.DataDir);
        var lockFile = Bonfire.Infrastructure.Content.ContentLockStore.PathFor(_paths);
        System.IO.File.WriteAllText(lockFile, "{}");

        await Assert.ThrowsAsync<BackupException>(() => _restore.RestoreAsync(_registry.Load(_record.Definition.Id)!.Record!, backup, null, Ct));

        Assert.Equal(current, Snapshot(_paths.DataDir));
        Assert.Empty(Directory.EnumerateDirectories(_paths.ServerDir, "restore-staging-*"));
        Assert.True(System.IO.File.Exists(lockFile)); // nothing restored: the lock still describes the files
        Assert.Null(_registry.Load(_record.Definition.Id)!.Record!.PendingRestore);
        Assert.Equal($"policy:{_record.Definition.RestartPolicy}", _runtime.Calls[^1]);
    }

    [Fact]
    public async Task DifferentVersionIsRefused()
    {
        var backup = await BackupAsync();
        var newer = _registry.Load(_record.Definition.Id)!.Record! with { Definition = _record.Definition with { MinecraftVersion = "26.4" } };

        var error = await Assert.ThrowsAsync<BackupException>(() => _restore.RestoreAsync(newer, backup, null, Ct));

        Assert.Equal(BackupError.VersionMismatch, error.Error);
    }

    public enum Crash { BeforeSwap, BetweenRenames, RollbackOnly, AfterSwap }

    [Theory]
    [InlineData(Crash.BeforeSwap)]
    [InlineData(Crash.BetweenRenames)]
    [InlineData(Crash.RollbackOnly)]
    [InlineData(Crash.AfterSwap)]
    public async Task RecoveryJudgesByWhatIsOnDisk(Crash crash)
    {
        var backup = await BackupAsync();
        System.IO.File.WriteAllText(Path.Combine(_paths.DataDir, "marker"), "current");
        var current = Snapshot(_paths.DataDir);
        var lockFile = Bonfire.Infrastructure.Content.ContentLockStore.PathFor(_paths);
        System.IO.File.WriteAllText(lockFile, "{}");
        var pending = new PendingRestore(Guid.NewGuid(), backup.BackupId, backup.Record!.ArchiveSha256,
            "restore-staging-x", RestoreEngine.RollbackPrefix + "x", RestartPolicyKind.UnlessStopped, RestoreStep.Extracted);
        var staging = Path.Combine(_paths.ServerDir, pending.StagingDirName);
        var rollback = Path.Combine(_paths.ServerDir, pending.RollbackDirName);
        await BackupStore.ExtractVerifiedAsync(backup, staging, null, Ct);
        var restored = Snapshot(staging);

        switch (crash)
        {
            case Crash.BetweenRenames:
                Directory.Move(_paths.DataDir, rollback);
                pending = pending with { Step = RestoreStep.DataMovedAside };
                break;
            case Crash.RollbackOnly:
                Directory.Move(_paths.DataDir, rollback);
                Bonfire.Infrastructure.Linux.SafeFiles.DeleteTree(staging, _paths.ServerDir);
                pending = pending with { Step = RestoreStep.DataMovedAside };
                break;
            case Crash.AfterSwap:
                Directory.Move(_paths.DataDir, rollback);
                Directory.Move(staging, _paths.DataDir);
                pending = pending with { Step = RestoreStep.DataMovedAside }; // step not yet updated
                break;
        }

        var record = _registry.Load(_record.Definition.Id)!.Record! with { PendingRestore = pending };
        await _registry.SaveAsync(record, Ct);

        await _restore.RecoverAsync(record);

        var expected = crash is Crash.BeforeSwap or Crash.RollbackOnly ? current : restored;
        Assert.Equal(expected, Snapshot(_paths.DataDir));
        Assert.Null(_registry.Load(_record.Definition.Id)!.Record!.PendingRestore);
        Assert.False(Directory.Exists(staging));
        Assert.Equal("policy:UnlessStopped", _runtime.Calls[^1]);
        Assert.DoesNotContain("start", _runtime.Calls);
    }

    private sealed class SyncProgress(Action<BackupProgress> report) : IProgress<BackupProgress>
    {
        public void Report(BackupProgress value) => report(value);
    }
}
