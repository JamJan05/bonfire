namespace Bonfire.App.Services;

/// <summary>Opens a directory in the system file manager.</summary>
public interface IFolderLauncher
{
    Task<bool> OpenAsync(string path);

    /// <summary>Opens an https link in the default browser.</summary>
    Task<bool> OpenUriAsync(Uri uri);
}
