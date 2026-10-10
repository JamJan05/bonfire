using Bonfire.Core;
using Bonfire.Infrastructure;
using Bonfire.Infrastructure.Linux;

namespace Bonfire.App;

/// <summary>
/// Outcome of the non-UI bootstrap performed before Avalonia starts. Either the app is
/// ready to run, or there is an error (resource keys plus technical detail) to display.
/// </summary>
public sealed class Startup : IDisposable
{
    private InstanceLock? _instanceLock;

    private Startup() { }

    public AppPaths? Paths { get; private init; }

    public SettingsStore? SettingsStore { get; private init; }

    public SettingsLoadResult? Settings { get; private init; }

    public Guid InstallationId { get; private init; }

    public string? ErrorTitleKey { get; private init; }

    public string? ErrorMessageKey { get; private init; }

    public string? ErrorDetail { get; private init; }

    public bool Succeeded => ErrorTitleKey is null;

    public static Startup Run()
    {
        // Must happen before anything creates files, so logs and state are owner-only.
        ProcessUmask.RestrictToOwner();

        AppPaths paths;
        try
        {
            paths = AppPaths.Resolve(Environment.GetEnvironmentVariable);
            AppDirectories.Ensure(paths);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return Fail("Startup_DirsTitle", "Startup_DirsMessage", ex.Message);
        }

        InstanceLock? instanceLock;
        try
        {
            if (InstanceLock.TryAcquire(paths.InstanceLockFile, out instanceLock) == InstanceLockStatus.AlreadyHeld)
            {
                return Fail("Startup_AlreadyRunningTitle", "Startup_AlreadyRunningMessage", detail: null);
            }
        }
        catch (Exception ex) when (ex is IOException or PlatformNotSupportedException)
        {
            return Fail("Startup_LockTitle", "Startup_LockMessage", ex.Message);
        }

        Guid installationId;
        try
        {
            installationId = Infrastructure.Servers.InstallationIdentity.LoadOrCreateAsync(paths).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            instanceLock?.Dispose();
            return Fail("Startup_DirsTitle", "Startup_DirsMessage", ex.Message);
        }

        // Settings are read only after the lock, so no other instance can be writing them.
        var store = new SettingsStore(paths.SettingsFile);
        return new Startup
        {
            Paths = paths,
            SettingsStore = store,
            Settings = store.Load(),
            InstallationId = installationId,
            _instanceLock = instanceLock,
        };
    }

    private static Startup Fail(string titleKey, string messageKey, string? detail) =>
        new() { ErrorTitleKey = titleKey, ErrorMessageKey = messageKey, ErrorDetail = detail };

    public void Dispose()
    {
        _instanceLock?.Dispose();
        _instanceLock = null;
    }
}
