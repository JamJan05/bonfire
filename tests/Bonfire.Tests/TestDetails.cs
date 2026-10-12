using Bonfire.App.Localization;
using Bonfire.App.Services;
using Bonfire.App.ViewModels;
using Bonfire.Core;
using Bonfire.Core.Servers;
using Bonfire.Infrastructure;
using Bonfire.Infrastructure.Backups;
using Bonfire.Infrastructure.Servers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bonfire.Tests;

internal static class TestDetails
{
    public static ServerBackupsViewModel Backups(
        Guid serverId, ServerRegistry registry, IServerRuntime runtime, ServerOperationsService operations, IDialogService dialogs, IFolderLauncher launcher)
    {
        var dataDir = Path.GetDirectoryName(registry.ServersDir)!;
        var app = new AppPaths(dataDir, dataDir, dataDir, dataDir);
        var settingsStore = new SettingsStore(Path.Combine(dataDir, "settings.json"));
        var backups = new BackupService(
            registry, runtime, operations, new BackupStore(app),
            new RestoreEngine(registry, runtime, TimeProvider.System, NullLogger<RestoreEngine>.Instance),
            new SettingsService(settingsStore, settingsStore.Load(), NullLogger<SettingsService>.Instance),
            TimeProvider.System);
        return new ServerBackupsViewModel(serverId, backups, operations, runtime, registry, dialogs, launcher, Localizer.Instance, NullLogger.Instance);
    }

    public static ServerContentViewModel Content(Guid serverId, ServerRegistry registry, IServerRuntime runtime, ServerOperationsService operations, FakeModrinth? modrinth = null)
    {
        var api = modrinth ?? new FakeModrinth();
        var content = new ContentService(registry, operations, runtime,
            new ContentEngine(registry, runtime, NullLogger<ContentEngine>.Instance), api,
            () => new Bonfire.Infrastructure.Content.ContentDownloader(new FakeModrinth.Cdn(api)), TimeProvider.System, NullLogger<ContentService>.Instance);
        return new ServerContentViewModel(serverId, content, new Confirm(true), new NoPicker(), Localizer.Instance, NullLogger.Instance);
    }

    public static ServerSettingsViewModel Settings(
        Guid serverId, ServerRegistry registry, ServerOperationsService operations, IDialogService dialogs,
        IHostAddresses? hosts = null, IPortAvailability? ports = null, Func<IReadOnlyList<Bonfire.Core.Backups.BackupInfo>>? backups = null) =>
        new(serverId, registry, operations, dialogs, hosts ?? FakeHostAddresses.Typical(), ports ?? new ServerCreationTests.FixedPorts(),
            backups ?? (() => []), Localizer.Instance, NullLogger.Instance);

    public static ConnectionInfoViewModel Connection(IHostAddresses? hosts = null, FakeClipboard? clipboard = null) =>
        new(Localizer.Instance, hosts ?? FakeHostAddresses.Typical(), clipboard ?? new FakeClipboard(), NullLogger.Instance);

    internal sealed class FakeClipboard : IClipboardService
    {
        public List<string> Texts { get; } = [];

        public bool Fail { get; set; }

        public Task<bool> SetTextAsync(string text)
        {
            Texts.Add(text);
            return Task.FromResult(!Fail);
        }
    }

    internal sealed class Confirm(bool yes) : IDialogService
    {
        public List<string> Messages { get; } = [];

        public Task<bool> ConfirmAsync(string title, string message, string confirmText, bool dangerous)
        {
            Messages.Add(title + "\n" + message);
            return Task.FromResult(yes);
        }
    }

    internal sealed class NoPicker(string? path = null) : IFilePicker
    {
        public Task<string?> PickJarAsync(string title) => Task.FromResult(path);
    }
}
