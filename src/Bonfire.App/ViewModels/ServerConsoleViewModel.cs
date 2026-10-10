using System.Collections.ObjectModel;
using Bonfire.App.Localization;
using Bonfire.App.Services;
using Bonfire.Core.Rcon;
using Bonfire.Core.Servers;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Bonfire.App.ViewModels;

public sealed record ConsoleEntry(string Command, string Output, bool IsProblem);

/// <summary>RCON console. History lives in memory only and is never written to disk or logs.</summary>
public sealed partial class ServerConsoleViewModel : ViewModelBase
{
    public const int MaxEntries = 300;
    public const int MaxHistory = 100;

    private readonly Guid _serverId;
    private readonly ServerOperationsService _operations;
    private readonly Localizer _localizer;
    private readonly ILogger _logger;
    private readonly List<string> _history = [];
    private int _historyIndex;

    public ServerConsoleViewModel(Guid serverId, ServerOperationsService operations, Localizer localizer, ILogger logger)
    {
        _serverId = serverId;
        _operations = operations;
        _localizer = localizer;
        _logger = logger;
    }

    public ObservableCollection<ConsoleEntry> Entries { get; } = [];

    /// <summary>
    /// Set by the details page: stops the server through the card, so the stopping indicator and
    /// the delayed force-stop offer work exactly as for the Stop button.
    /// </summary>
    public Func<Task<bool>>? RequestStop { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string _input = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private bool _isSending;

    private bool CanSend() => !IsSending && !string.IsNullOrWhiteSpace(Input);

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var command = Input.Trim();
        if (RconCommands.Validate(command) is { } invalid)
        {
            Add(command, _localizer[invalid], problem: true);
            return;
        }

        Remember(command);
        Input = "";

        if (RconCommands.IsStopCommand(command) && RequestStop is { } requestStop)
        {
            Add(command, _localizer[await requestStop() ? "Console_StopRouted" : "Error_NotRunning"], problem: false);
            return;
        }

        IsSending = true;
        try
        {
            var result = await _operations.SendCommandAsync(_serverId, command, CancellationToken.None);
            if (result is null)
            {
                Add(command, _localizer["Console_StopRouted"], problem: false);
            }
            else if (result.Outcome == RconOutcome.Unknown)
            {
                Add(command, _localizer["Console_OutcomeUnknown"] + (result.Output.Length > 0 ? "\n" + result.Output : ""), problem: true);
            }
            else
            {
                var output = result.Output.Length > 0 ? result.Output : _localizer["Console_NoOutput"];
                if (result.OutputTruncated)
                {
                    output += "\n" + _localizer["Console_Truncated"];
                }

                // rcon-cli's exit code reports connection problems, not whether the command was valid.
                Add(command, result.ExitCode is null or 0 ? output : _localizer.Format("Console_ConnectionFailed", result.ExitCode) + "\n" + output,
                    problem: result.ExitCode is not (null or 0));
            }
        }
        catch (ServerOperationException ex)
        {
            Add(command, _localizer[$"Error_{ex.Error}"], problem: true);
        }
        catch (Exception ex)
        {
            // The command text is deliberately not logged.
            _logger.LogError("Console command failed: {Type}", ex.GetType().Name);
            Add(command, _localizer["Error_Unexpected"], problem: true);
        }
        finally
        {
            IsSending = false;
        }
    }

    /// <summary>↑ in the input box.</summary>
    public void HistoryPrevious()
    {
        if (_history.Count == 0)
        {
            return;
        }

        _historyIndex = Math.Max(0, _historyIndex - 1);
        Input = _history[_historyIndex];
    }

    /// <summary>↓ in the input box.</summary>
    public void HistoryNext()
    {
        if (_history.Count == 0)
        {
            return;
        }

        _historyIndex = Math.Min(_history.Count, _historyIndex + 1);
        Input = _historyIndex == _history.Count ? "" : _history[_historyIndex];
    }

    [RelayCommand]
    private void Clear() => Entries.Clear();

    private void Remember(string command)
    {
        if (_history.Count == 0 || _history[^1] != command)
        {
            _history.Add(command);
            if (_history.Count > MaxHistory)
            {
                _history.RemoveAt(0);
            }
        }

        _historyIndex = _history.Count;
    }

    private void Add(string command, string output, bool problem)
    {
        Entries.Add(new ConsoleEntry("> " + command, output, problem));
        while (Entries.Count > MaxEntries)
        {
            Entries.RemoveAt(0);
        }
    }
}
