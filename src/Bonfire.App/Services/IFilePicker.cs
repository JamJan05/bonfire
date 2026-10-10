using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;

namespace Bonfire.App.Services;

public interface IFilePicker
{
    /// <summary>Lets the user pick one local .jar file; null when cancelled or not a local file.</summary>
    Task<string?> PickJarAsync(string title);
}

public sealed class AvaloniaFilePicker : IFilePicker
{
    public async Task<string?> PickJarAsync(string title)
    {
        var window = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        var storage = window is null ? null : TopLevel.GetTopLevel(window)?.StorageProvider;
        if (storage is null)
        {
            return null;
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Java archive (*.jar)") { Patterns = ["*.jar"] }],
        });
        return files.Count == 1 ? files[0].TryGetLocalPath() : null;
    }
}
