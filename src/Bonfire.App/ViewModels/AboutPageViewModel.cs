using Bonfire.App.Localization;
using Bonfire.App.Services;
using Bonfire.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Bonfire.App.ViewModels;

public sealed partial class AboutPageViewModel : PageViewModel
{
    private readonly AppPaths _paths;
    private readonly IFolderLauncher _folderLauncher;
    private readonly ILogger<AboutPageViewModel> _logger;

    public AboutPageViewModel(
        Localizer localizer,
        AppPaths paths,
        IFolderLauncher folderLauncher,
        ILogger<AboutPageViewModel> logger)
        : base(localizer, "Nav_About")
    {
        _paths = paths;
        _folderLauncher = folderLauncher;
        _logger = logger;
    }

    public string AppName => AppIdentity.DisplayName;

    public string VersionText => Localizer.Format("About_Version", AppIdentity.Version);

    public string ConfigDir => _paths.ConfigDir;

    public string DataDir => _paths.DataDir;

    public string LogDir => _paths.LogDir;

    /// <summary>Stored as a resource key so a visible message follows language changes.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusMessage))]
    private string? _statusKey;

    public string? StatusMessage => StatusKey is null ? null : Localizer[StatusKey];

    protected override void OnLanguageChanged()
    {
        base.OnLanguageChanged();
        OnPropertyChanged(nameof(VersionText));
        OnPropertyChanged(nameof(StatusMessage));
    }

    [RelayCommand]
    private async Task OpenLogDirAsync()
    {
        StatusKey = null;
        try
        {
            if (!await _folderLauncher.OpenAsync(_paths.LogDir))
            {
                StatusKey = "Error_OpenFolderFailed";
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Opening log directory failed");
            StatusKey = "Error_OpenFolderFailed";
        }
    }
}
