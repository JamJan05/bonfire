using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;

namespace Bonfire.App.Services;

/// <summary>Uses the platform launcher (xdg-open/portal) — no shell is involved.</summary>
public sealed class AvaloniaFolderLauncher : IFolderLauncher
{
    public async Task<bool> OpenAsync(string path)
    {
        var window = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        var launcher = window is null ? null : TopLevel.GetTopLevel(window)?.Launcher;
        if (launcher is null || !Directory.Exists(path))
        {
            return false;
        }

        return await launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(path));
    }

    public async Task<bool> OpenUriAsync(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        var window = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        var launcher = window is null ? null : TopLevel.GetTopLevel(window)?.Launcher;
        return launcher is not null && await launcher.LaunchUriAsync(uri);
    }
}
