using Bonfire.App.Localization;
using Bonfire.App.Services;
using Bonfire.Core;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Bonfire.App.ViewModels;

public sealed partial class MainWindowViewModel : ViewModelBase, IDisposable
{
    private readonly Localizer _localizer;

    public MainWindowViewModel(
        Localizer localizer,
        DockerStatusViewModel docker,
        ServersPageViewModel servers,
        SettingsPageViewModel settings,
        AboutPageViewModel about,
        Navigator navigator,
        Func<NewServerViewModel> newServer,
        Func<ServerItemViewModel, ServerDetailsViewModel> details)
    {
        _localizer = localizer;
        navigator.NewServerRequested += (_, _) =>
        {
            var wizard = newServer();
            CurrentPage = wizard;
            _ = wizard.InitializeAsync();
        };
        navigator.ServerDetailsRequested += (_, id) =>
        {
            if (servers.Find(id) is not { Snapshot.Record: not null } item)
            {
                return; // damaged or unknown server: nothing to show
            }

            var page = details(item);
            CurrentPage = page;
            page.Start();
        };
        navigator.ServersRequested += (_, _) =>
        {
            CurrentPage = servers;
            _ = servers.RefreshAsync();
        };
        Docker = docker;
        Pages = [servers, settings, about];
        _currentPage = servers;
        _selectedNavPage = servers;
        _languageChanged = (_, _) => OnPropertyChanged(nameof(WindowTitle));
        localizer.LanguageChanged += _languageChanged;
    }

    private readonly EventHandler _languageChanged;

    /// <summary>Detaches this view model and its pages from the (longer-lived) localizer.</summary>
    public void Dispose()
    {
        _localizer.LanguageChanged -= _languageChanged;
        foreach (var page in Pages)
        {
            page.DetachFromLocalizer();
        }

        CurrentPage.DetachFromLocalizer();
    }

    public string WindowTitle => _localizer.Format("Window_Title", AppIdentity.DisplayName);

    public IReadOnlyList<PageViewModel> Pages { get; }

    public DockerStatusViewModel Docker { get; }

    [ObservableProperty]
    private PageViewModel _currentPage;

    /// <summary>
    /// The sidebar selection, kept separate from <see cref="CurrentPage"/>: pages outside the
    /// sidebar (wizard, server details) make the list select nothing, and that "nothing" must never
    /// flow back and close the page that was just opened.
    /// </summary>
    [ObservableProperty]
    private PageViewModel? _selectedNavPage;

    partial void OnSelectedNavPageChanged(PageViewModel? value)
    {
        if (value is not null)
        {
            CurrentPage = value;
        }
    }

    /// <summary>A short-lived page is discarded (its loops cancelled) when another page is shown.</summary>
    partial void OnCurrentPageChanged(PageViewModel? oldValue, PageViewModel newValue)
    {
        if (oldValue is IDisposable disposable and (NewServerViewModel or ServerDetailsViewModel) && !ReferenceEquals(oldValue, newValue))
        {
            disposable.Dispose();
        }

        SelectedNavPage = Pages.Contains(newValue) ? newValue : null;
    }
}
