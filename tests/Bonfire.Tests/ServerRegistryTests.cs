using Bonfire.Core;
using Bonfire.Core.Servers;
using Bonfire.Infrastructure.Servers;

namespace Bonfire.Tests;

public sealed class ServerRegistryTests
{
    private static (TempDirectory Temp, ServerRegistry Registry) Create()
    {
        var temp = new TempDirectory();
        var paths = AppPaths.Resolve(k => k == "HOME" ? temp.Path : null);
        Directory.CreateDirectory(paths.DataDir);
        return (temp, new ServerRegistry(paths));
    }

    [Fact]
    public async Task CreateMakesOwnerOnlyDirectoriesAndSecretAndRoundTrips()
    {
        var (temp, registry) = Create();
        using (temp)
        {
            var record = ServerRecord.New(ServerTestData.Vanilla());

            var paths = await registry.CreateAsync(record, TestContext.Current.CancellationToken);

            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(paths.ServerDir));
            Assert.True(Directory.Exists(paths.DataDir));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(paths.RconPasswordFile));
            Assert.True(File.ReadAllText(paths.RconPasswordFile).Length >= 40);
            Assert.DoesNotContain(File.ReadAllText(paths.RconPasswordFile), File.ReadAllText(paths.RecordFile));

            var loaded = Assert.Single(registry.LoadAll());
            Assert.Null(loaded.Error);
            Assert.Equal(record, loaded.Record);
        }
    }

    [Fact]
    public async Task PasswordsAreUniquePerServer()
    {
        var (temp, registry) = Create();
        using (temp)
        {
            var a = await registry.CreateAsync(ServerRecord.New(ServerTestData.Vanilla()), TestContext.Current.CancellationToken);
            var b = await registry.CreateAsync(ServerRecord.New(ServerTestData.Vanilla()), TestContext.Current.CancellationToken);

            Assert.NotEqual(File.ReadAllText(a.RconPasswordFile), File.ReadAllText(b.RconPasswordFile));
        }
    }

    [Fact]
    public async Task CorruptRecordIsReportedNotDropped()
    {
        var (temp, registry) = Create();
        using (temp)
        {
            var paths = await registry.CreateAsync(ServerRecord.New(ServerTestData.Vanilla()), TestContext.Current.CancellationToken);
            File.WriteAllText(paths.RecordFile, "{ broken");

            var entry = Assert.Single(registry.LoadAll());

            Assert.Null(entry.Record);
            Assert.NotNull(entry.Error);
        }
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"SchemaVersion":1,"Definition":null}""")]
    [InlineData("""{"SchemaVersion":1,"Definition":{"Id":"00000000-0000-0000-0000-000000000000"}}""")]
    [InlineData("whitelist-null")]
    public async Task StructurallyIncompleteRecordsAreReportedWithoutAbortingTheRest(string json)
    {
        var (temp, registry) = Create();
        using (temp)
        {
            var broken = await registry.CreateAsync(ServerRecord.New(ServerTestData.Vanilla()), TestContext.Current.CancellationToken);
            await registry.CreateAsync(ServerRecord.New(ServerTestData.Vanilla()), TestContext.Current.CancellationToken);
            if (json == "whitelist-null")
            {
                // Otherwise valid record with a null whitelist element.
                json = File.ReadAllText(broken.RecordFile).Replace("\"Whitelist\": []", "\"Whitelist\": [null]");
                Assert.Contains("[null]", json);
            }

            File.WriteAllText(broken.RecordFile, json);

            var entries = registry.LoadAll();

            Assert.Equal(2, entries.Count);
            Assert.Single(entries, e => e.Record is null && e.Error is not null);
            Assert.Single(entries, e => e.Record is not null);
        }
    }

    [Fact]
    public async Task ExistingServerDirectoryIsNotReused()
    {
        var (temp, registry) = Create();
        using (temp)
        {
            var record = ServerRecord.New(ServerTestData.Vanilla());
            await registry.CreateAsync(record, TestContext.Current.CancellationToken);

            await Assert.ThrowsAnyAsync<IOException>(() => registry.CreateAsync(record, TestContext.Current.CancellationToken));
        }
    }
}
