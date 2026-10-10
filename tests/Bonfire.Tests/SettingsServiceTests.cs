using Bonfire.App.Services;
using Bonfire.Core;
using Bonfire.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bonfire.Tests;

public sealed class SettingsServiceTests
{
    private static SettingsService Create(SettingsStore store) =>
        new(store, store.Load(), NullLogger<SettingsService>.Instance);

    [Fact]
    public async Task RapidUnawaitedChangesLeaveTheLastValueOnDisk()
    {
        using var temp = new TempDirectory();
        var store = new SettingsStore(Path.Combine(temp.Path, "settings.json"));
        var service = Create(store);

        for (var i = 0; i < 20; i++)
        {
            var language = i % 2 == 0 ? AppLanguage.Polish : AppLanguage.English;
            _ = service.UpdateAsync(s => s with { Language = language });
        }

        _ = service.UpdateAsync(s => s with { Language = AppLanguage.Polish });
        await service.FlushAsync();

        Assert.Equal(AppLanguage.Polish, store.Load().Settings.Language);
    }

    [Fact]
    public async Task CorruptFileIsNotOverwrittenByNormalChanges()
    {
        using var temp = new TempDirectory();
        var store = new SettingsStore(Path.Combine(temp.Path, "settings.json"));
        File.WriteAllText(store.FilePath, "garbage");
        var service = Create(store);

        Assert.True(await service.UpdateAsync(s => s with { Language = AppLanguage.Polish }));
        await service.FlushAsync();

        Assert.Equal("garbage", File.ReadAllText(store.FilePath));
        Assert.Equal(AppLanguage.Polish, service.Current.Language);
    }

    [Fact]
    public async Task ChangeMadeDuringReplacementIsPersisted()
    {
        using var temp = new TempDirectory();
        var store = new SettingsStore(Path.Combine(temp.Path, "settings.json"));
        File.WriteAllText(store.FilePath, "garbage");
        var service = Create(store);

        var replace = service.ReplaceFileAsync();
        var change = service.UpdateAsync(s => s with { Language = AppLanguage.Polish });

        Assert.True(await replace);
        Assert.True(await change);
        await service.FlushAsync();

        var result = store.Load();
        Assert.Equal(SettingsLoadStatus.Loaded, result.Status);
        Assert.Equal(AppLanguage.Polish, result.Settings.Language);
    }

    [Fact]
    public async Task FailedReplacementRestoresPreviousStatus()
    {
        using var temp = new TempDirectory();
        var store = new SettingsStore(Path.Combine(temp.Path, "settings.json"));
        Directory.CreateDirectory(store.FilePath); // makes the file unreadable and unwritable
        var service = Create(store);

        Assert.False(await service.ReplaceFileAsync());

        Assert.Equal(SettingsLoadStatus.Unreadable, service.LoadStatus);
        Assert.False(service.CanSave);
    }
}
