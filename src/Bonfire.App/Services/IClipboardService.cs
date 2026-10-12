using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;

namespace Bonfire.App.Services;

public interface IClipboardService
{
    /// <summary>Puts text on the system clipboard; false when no clipboard is available or the copy failed.</summary>
    Task<bool> SetTextAsync(string text);
}

/// <summary>The main window's clipboard, resolved at the time of the copy (the window may not exist at startup).</summary>
public sealed class AvaloniaClipboardService : IClipboardService
{
    public async Task<bool> SetTextAsync(string text)
    {
        var window = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        var clipboard = window is null ? null : TopLevel.GetTopLevel(window)?.Clipboard;
        if (clipboard is null)
        {
            return false;
        }

        try
        {
            await clipboard.SetTextAsync(text);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException or TimeoutException or IOException)
        {
            return false;
        }
    }
}
