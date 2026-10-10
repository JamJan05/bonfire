using Bonfire.App.Localization;

namespace Bonfire.App.ViewModels;

/// <summary>A page reachable from the sidebar.</summary>
public abstract class PageViewModel : ViewModelBase
{
    protected PageViewModel(Localizer localizer, string titleKey)
    {
        Localizer = localizer;
        _titleKey = titleKey;
        _languageChanged = (_, _) => OnLanguageChanged();
        localizer.LanguageChanged += _languageChanged;
    }

    private readonly EventHandler _languageChanged;

    /// <summary>
    /// Short-lived pages (wizard, details) must call this when discarded, otherwise the singleton
    /// localizer keeps them — and everything they hold — alive.
    /// </summary>
    protected void DetachFromLocalizer() => Localizer.LanguageChanged -= _languageChanged;

    private readonly string _titleKey;

    protected Localizer Localizer { get; }

    public string Title => Localizer[_titleKey];

    /// <summary>Re-raise every computed, localized property here.</summary>
    protected virtual void OnLanguageChanged() => OnPropertyChanged(nameof(Title));
}
