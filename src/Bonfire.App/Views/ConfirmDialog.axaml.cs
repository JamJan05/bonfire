using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Bonfire.App.Views;

public sealed partial class ConfirmDialog : Window
{
    // Required by the XAML loader and the designer.
    public ConfirmDialog() => InitializeComponent();

    public ConfirmDialog(string title, string message, string confirmText, bool dangerous)
        : this()
    {
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        ConfirmButton.Content = confirmText;
        ConfirmButton.Classes.Set("danger", dangerous);
        // Cancel is focused, so Enter never confirms a destructive action by accident.
        Opened += (_, _) => CancelButton.Focus();
    }

    private void OnConfirm(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
