using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Bonfire.App.Views;

/// <summary>Shown instead of the main window when startup checks fail.</summary>
public sealed partial class StartupErrorWindow : Window
{
    // Required by the XAML loader and the designer.
    public StartupErrorWindow() => InitializeComponent();

    public StartupErrorWindow(string title, string message, string? detail)
        : this()
    {
        TitleText.Text = title;
        MessageText.Text = message;
        DetailText.Text = detail;
        DetailText.IsVisible = detail is not null;
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
