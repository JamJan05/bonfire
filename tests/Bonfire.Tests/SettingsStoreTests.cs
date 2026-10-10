using Bonfire.Core;
using Bonfire.Infrastructure;

namespace Bonfire.Tests;

public sealed class SettingsStoreTests
{
    private static (TempDirectory Temp, SettingsStore Store) Create()
    {
        var temp = new TempDirectory();
        return (temp, new SettingsStore(Path.Combine(temp.Path, "settings.json")));
    }

    [Fact]
    public void MissingFileGivesDefaultsAndAllowsSaving()
    {
        var (temp, store) = Create();
        using (temp)
        {
            var result = store.Load();

            Assert.Equal(SettingsLoadStatus.Missing, result.Status);
            Assert.Equal(AppLanguage.English, result.Settings.Language);
            Assert.True(result.CanSave);
            Assert.False(File.Exists(store.FilePath), "Loading must not write defaults.");
        }
    }

    [Fact]
    public async Task SaveThenLoadRoundTrips()
    {
        var (temp, store) = Create();
        using (temp)
        {
            await store.SaveAsync(new AppSettings { Language = AppLanguage.Polish }, TestContext.Current.CancellationToken);

            var result = store.Load();
            Assert.Equal(SettingsLoadStatus.Loaded, result.Status);
            Assert.Equal(AppLanguage.Polish, result.Settings.Language);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(store.FilePath));
            Assert.Single(Directory.GetFiles(temp.Path));
        }
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"SchemaVersion":"1"}""")]
    [InlineData("""{"SchemaVersion":0}""")]
    public void InvalidContentIsCorruptAndKept(string content)
    {
        var (temp, store) = Create();
        using (temp)
        {
            File.WriteAllText(store.FilePath, content);

            var result = store.Load();

            Assert.Equal(SettingsLoadStatus.Corrupt, result.Status);
            Assert.False(result.CanSave);
            Assert.Equal(AppLanguage.Default, result.Settings.Language);
            Assert.Equal(content, File.ReadAllText(store.FilePath));
        }
    }

    [Fact]
    public void NewerSchemaKeepsRecognisedLanguageButForbidsSaving()
    {
        var (temp, store) = Create();
        using (temp)
        {
            File.WriteAllText(store.FilePath, """{"SchemaVersion":99,"Language":"pl","Future":true}""");

            var result = store.Load();

            Assert.Equal(SettingsLoadStatus.NewerSchema, result.Status);
            Assert.Equal(AppLanguage.Polish, result.Settings.Language);
            Assert.False(result.CanSave);
        }
    }

    [Fact]
    public void UnsupportedLanguageResolvesToEnglishWithoutRewriting()
    {
        var (temp, store) = Create();
        using (temp)
        {
            const string content = """{"SchemaVersion":1,"Language":"de"}""";
            File.WriteAllText(store.FilePath, content);

            var result = store.Load();

            Assert.Equal(AppLanguage.English, result.Settings.Language);
            Assert.Equal(content, File.ReadAllText(store.FilePath));
        }
    }

    [Fact]
    public void UnreadableFileIsReportedNotThrown()
    {
        var (temp, store) = Create();
        using (temp)
        {
            Directory.CreateDirectory(store.FilePath); // a directory where the file should be

            var result = store.Load();

            Assert.Equal(SettingsLoadStatus.Unreadable, result.Status);
            Assert.False(result.CanSave);
        }
    }
}
