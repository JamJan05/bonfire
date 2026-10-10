using System.ComponentModel;
using System.Globalization;
using System.Resources;
using Bonfire.Core;

namespace Bonfire.App.Localization;

/// <summary>
/// Single source of UI strings. XAML binds to its indexer (<c>{Binding [Key], Source={x:Static loc:Localizer.Instance}}</c>);
/// changing the language raises <c>Item[]</c>, so every bound text updates live.
/// The same instance is registered in DI for view models.
/// </summary>
public sealed class Localizer : INotifyPropertyChanged
{
    private static readonly ResourceManager Resources =
        new("Bonfire.App.Localization.Strings", typeof(Localizer).Assembly);

    public static Localizer Instance { get; } = new();

    private CultureInfo _culture = CultureInfo.GetCultureInfo(AppLanguage.Default);

    private Localizer() { }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised after the language changed; view models re-raise their computed texts.</summary>
    public event EventHandler? LanguageChanged;

    public string Language => _culture.TwoLetterISOLanguageName;

    /// <summary>Missing keys render as <c>[Key]</c> so they are visible instead of silently blank.</summary>
    public string this[string key] => Resources.GetString(key, _culture) ?? $"[{key}]";

    public string Format(string key, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, this[key], args);

    /// <summary>
    /// Switches the UI language. Only the UI culture changes; number and date formatting keep
    /// following the system locale. Must be called on the UI thread.
    /// </summary>
    public void SetLanguage(string code)
    {
        var culture = CultureInfo.GetCultureInfo(AppLanguage.Normalize(code));
        if (culture.Equals(_culture))
        {
            return;
        }

        _culture = culture;
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }
}
