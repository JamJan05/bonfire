using System.Globalization;
using Bonfire.App.Localization;
using Bonfire.App.Services;
using Bonfire.Core.Docker;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Bonfire.App.ViewModels;

public sealed record DetailRow(string Label, string Value);

/// <summary>
/// Shared Docker connection state, shown both in the sidebar and on the Servers page.
/// One probe runs at a time (the command refuses concurrent execution) and the probe is
/// cancelled when the application shuts down.
/// </summary>
public sealed partial class DockerStatusViewModel : ViewModelBase, IDockerStatusSource, IDisposable
{
    private readonly IDockerEngine _engine;
    private readonly Localizer _localizer;
    private readonly ILogger<DockerStatusViewModel> _logger;
    private readonly CancellationTokenSource _lifetime = new();

    public DockerStatusViewModel(IDockerEngine engine, Localizer localizer, ILogger<DockerStatusViewModel> logger)
    {
        _engine = engine;
        _localizer = localizer;
        _logger = logger;
        // Application-lifetime singleton: the subscription never needs removing.
        localizer.LanguageChanged += (_, _) => RaiseTexts();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConnected), nameof(IsProblem), nameof(HasWarnings))]
    private DockerProbeResult? _result;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProblem))]
    private bool _isChecking;

    public bool IsConnected => Result?.IsConnected == true;

    private bool _invalidated;

    DockerProbeResult? IDockerStatusSource.Current => Result;

    bool IDockerStatusSource.CapabilitiesTrusted => !_invalidated;

    /// <summary>A Docker call failed: stop trusting the last probe until the next one completes.</summary>
    public void Invalidate() => _invalidated = true;

    public bool IsProblem => !IsChecking && Result is { IsConnected: false };

    public bool HasWarnings => Warnings.Count > 0;

    public string ShortText =>
        IsChecking ? _localizer["Docker_Checking"]
        : Result is { IsConnected: true, Daemon: { } daemon } ? _localizer.Format("Docker_Short_Connected", daemon.ServerVersion)
        : Result is null ? _localizer["Docker_Checking"]
        : _localizer["Docker_Short_Unavailable"];

    public string Title =>
        Result is null ? _localizer["Docker_Checking"] : _localizer[$"Docker_Title_{Result.Status}"];

    public string? Message
    {
        get
        {
            if (Result is null)
            {
                return null;
            }

            var socket = Result.Endpoint?.SocketPath ?? "?";
            return Result.Status == DockerConnectionStatus.ApiIncompatible && Result.Daemon is { } d
                ? _localizer.Format(
                    "Docker_Message_ApiIncompatible",
                    d.ServerVersion, d.MinimumApiVersion, d.MaximumApiVersion,
                    DockerApiVersion.ClientMinimum, DockerApiVersion.ClientMaximum)
                : _localizer.Format($"Docker_Message_{Result.Status}", socket);
        }
    }

    public IReadOnlyList<string> Warnings
    {
        get
        {
            var warnings = new List<string>();
            if (Result?.RootlessUnsupported == true)
            {
                warnings.Add(_localizer["Docker_Warn_Rootless"]);
            }

            if (Result?.SeccompMissing == true)
            {
                warnings.Add(_localizer["Docker_Warn_NoSeccomp"]);
            }

            if (Result?.CapabilitiesUnknown == true)
            {
                warnings.Add(_localizer["Docker_Warn_InfoUnknown"]);
            }

            return warnings;
        }
    }

    public IReadOnlyList<DetailRow> Details
    {
        get
        {
            var rows = new List<DetailRow>();
            if (Result?.Endpoint is { } endpoint)
            {
                rows.Add(new(_localizer["Docker_Detail_Endpoint"], endpoint.SocketPath));
            }

            if (Result?.Daemon is { } daemon)
            {
                rows.Add(new(_localizer["Docker_Detail_Version"], daemon.ServerVersion));
                rows.Add(new(_localizer["Docker_Detail_Api"], _localizer.Format(
                    "Docker_Detail_ApiValue",
                    daemon.NegotiatedApiVersion?.ToString() ?? "—",
                    daemon.MinimumApiVersion,
                    daemon.MaximumApiVersion)));
                rows.Add(new(_localizer["Docker_Detail_System"], string.Join(" · ",
                    new[] { daemon.OperatingSystem, daemon.Architecture, daemon.KernelVersion }.Where(s => !string.IsNullOrEmpty(s)))));
            }

            if (Result?.IsConnected == true)
            {
                var unknown = _localizer["Common_Unknown"];
                var caps = Result.Capabilities;
                rows.Add(new(_localizer["Docker_Detail_Seccomp"], caps?.Seccomp switch
                {
                    true => _localizer.Format("Docker_Detail_SeccompOn", caps.SeccompProfile ?? unknown),
                    false => _localizer["Docker_Detail_SeccompOff"],
                    null => unknown,
                }));
                rows.Add(new(_localizer["Docker_Detail_Cgroup"], caps?.CgroupVersion is null
                    ? unknown
                    : _localizer.Format("Docker_Detail_CgroupValue", caps.CgroupVersion, caps.CgroupDriver ?? unknown)));
            }

            return rows;
        }
    }

    public string? LastCheckedText => Result is null
        ? null
        : _localizer.Format("Docker_LastChecked", Result.CheckedAt.ToLocalTime().ToString("T", CultureInfo.CurrentCulture));

    public string? TechnicalDetail => Result?.TechnicalDetail;

    [RelayCommand]
    private async Task RefreshAsync()
    {
        // The command disables the button while running, but Execute() itself does not check
        // CanExecute, so guard against overlapping probes here too (UI thread only).
        if (IsChecking)
        {
            return;
        }

        IsChecking = true;
        RaiseTexts();
        try
        {
            Result = await _engine.ProbeAsync(_lifetime.Token);
            _invalidated = false;
        }
        catch (OperationCanceledException)
        {
            return; // shutting down
        }
        catch (Exception ex)
        {
            // ProbeAsync reports connection problems in its result; anything else is a bug.
            _logger.LogError(ex, "Docker probe failed unexpectedly");
            Result = new DockerProbeResult(DockerConnectionStatus.Failed, null, DateTimeOffset.UtcNow, TechnicalDetail: ex.GetType().Name);
        }
        finally
        {
            IsChecking = false;
        }

        RaiseTexts();
    }

    private void RaiseTexts()
    {
        foreach (var name in new[]
                 {
                     nameof(ShortText), nameof(Title), nameof(Message), nameof(Warnings), nameof(HasWarnings),
                     nameof(Details), nameof(LastCheckedText), nameof(TechnicalDetail),
                 })
        {
            OnPropertyChanged(name);
        }
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
