using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Bonfire.App.Views;

namespace Bonfire.App.Services;

public sealed class AvaloniaDialogService : IDialogService
{
    public async Task<bool> ConfirmAsync(string title, string message, string confirmText, bool dangerous)
    {
        var owner = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (owner is null)
        {
            return false;
        }

        var dialog = new ConfirmDialog(title, message, confirmText, dangerous);
        return await dialog.ShowDialog<bool>(owner);
    }
}
