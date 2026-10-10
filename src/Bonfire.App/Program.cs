using Avalonia;
using Avalonia.Media;
using Bonfire.Core;
using Serilog;

namespace Bonfire.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args is ["--version"])
        {
            Console.WriteLine($"{AppIdentity.DisplayName} {AppIdentity.Version}");
            return 0;
        }

        // Packaging test: start normally, verify the main window and pages, then exit (0 = OK).
        App.SmokeTest = args is ["--smoke-test"];

        if (Environment.IsPrivilegedProcess)
        {
            Console.Error.WriteLine("Bonfire must not run as root. Start it as a regular user.");
            Console.Error.WriteLine("Bonfire nie może działać jako root. Uruchom aplikację jako zwykły użytkownik.");
            return 1;
        }

        using var startup = Startup.Run();
        if (startup.Succeeded)
        {
            ConfigureLogging(startup.Paths!);
        }

        try
        {
            Log.Information("Starting {App} {Version}", AppIdentity.DisplayName, AppIdentity.Version);
            if (startup.Settings is { Status: not (SettingsLoadStatus.Missing or SettingsLoadStatus.Loaded) } settings)
            {
                Log.Warning("Settings file status {Status}: {Detail}", settings.Status, settings.Detail);
            }

            return BuildAvaloniaApp(startup).StartWithClassicDesktopLifetime(App.SmokeTest ? [] : args);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Unhandled exception, shutting down");
            throw;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    // Used by the Avalonia designer; it must stay free of locks and file-system effects.
    public static AppBuilder BuildAvaloniaApp() => BuildAvaloniaApp(startup: null);

    private static AppBuilder BuildAvaloniaApp(Startup? startup) =>
        AppBuilder.Configure(() => new App(startup))
            .UsePlatformDetect()
            .WithInterFont()
            // The bundled Inter is the default font: a system without any fonts must not crash the
            // UI (minimal Arch installs have fontconfig but no font), and it looks the same everywhere.
            .With(new FontManagerOptions { DefaultFamilyName = "fonts:Inter#Inter" });

    private static void ConfigureLogging(AppPaths paths)
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                Path.Combine(paths.LogDir, "bonfire-.log"),
                rollingInterval: RollingInterval.Day,
                fileSizeLimitBytes: 10 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                retainedFileCountLimit: 14,
                formatProvider: System.Globalization.CultureInfo.InvariantCulture)
            .CreateLogger();
    }
}
