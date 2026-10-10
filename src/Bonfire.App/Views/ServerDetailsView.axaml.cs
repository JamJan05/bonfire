using Avalonia.Controls;
using Avalonia.Input;
using Bonfire.App.ViewModels;

namespace Bonfire.App.Views;

public sealed partial class ServerDetailsView : UserControl
{
    private ServerLogViewModel? _logs;

    public ServerDetailsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            Attach((DataContext as ServerDetailsViewModel)?.Logs);
            if (DataContext is ServerDetailsViewModel details)
            {
                details.Console.Entries.CollectionChanged += (_, _) => ConsoleScroll.ScrollToEnd();
            }
        };
        DetachedFromVisualTree += (_, _) => Attach(null);
        ConsoleInput.KeyDown += OnConsoleKeyDown;
    }

    private void OnConsoleKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not ServerDetailsViewModel details)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Enter when details.Console.SendCommand.CanExecute(null):
                details.Console.SendCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Up:
                details.Console.HistoryPrevious();
                ConsoleInput.CaretIndex = ConsoleInput.Text?.Length ?? 0;
                e.Handled = true;
                break;
            case Key.Down:
                details.Console.HistoryNext();
                ConsoleInput.CaretIndex = ConsoleInput.Text?.Length ?? 0;
                e.Handled = true;
                break;
        }

        ConsoleScroll.ScrollToEnd();
    }

    private void Attach(ServerLogViewModel? logs)
    {
        if (_logs is not null)
        {
            _logs.ScrollToEndRequested -= OnScrollToEnd;
        }

        _logs = logs;
        if (_logs is not null)
        {
            _logs.ScrollToEndRequested += OnScrollToEnd;
        }
    }

    private void OnScrollToEnd(object? sender, EventArgs e)
    {
        if (_logs?.Visible.Count is > 0 and var count)
        {
            LogList.ScrollIntoView(count - 1);
        }
    }
}
