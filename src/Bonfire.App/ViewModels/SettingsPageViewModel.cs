using Bonfire.App.Localization;
using Bonfire.App.Services;
using Bonfire.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bonfire.App.ViewModels;

/// <summary>A language as shown in the picker: always in its own language, never translated.</summary>
public sealed record LanguageOption(string Code, string NativeName);

public sealed partial class SettingsPageViewModel : PageViewModel
{
    private readonly SettingsService _settings;

    public SettingsPageViewModel(Localizer localizer, SettingsService settings)
        : base(localizer, "Nav_Settings")
    {
        _settings = settings;
        _selectedLanguage = Languages.First(l => l.Code == settings.Current.Language);
        _backupsToKeep = settings.Current.BackupsToKeep;
    }

    [ObservableProperty]
    private decimal? _backupsToKeep;

    partial void OnBackupsToKeepChanged(decimal? value)
    {
        if (value is { } v)
        {
            var keep = (int)Math.Clamp(v, 1, AppSettings.MaxBackupsToKeep);
            _ = SaveBackupsToKeepAsync(keep);
        }
    }

    private async Task SaveBackupsToKeepAsync(int keep)
    {
        var saved = await _settings.UpdateAsync(s => s with { BackupsToKeep = keep });
        StatusKey = !saved ? "Settings_SaveFailed" : _settings.CanSave ? "Settings_Saved" : null;
    }

    public IReadOnlyList<LanguageOption> Languages { get; } =
    [
        new(AppLanguage.English, "English"),
        new(AppLanguage.Polish, "Polski"),
    ];

    [ObservableProperty]
    private LanguageOption _selectedLanguage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusMessage))]
    private string? _statusKey;

    public string? StatusMessage => StatusKey is null ? null : Localizer[StatusKey];

    /// <summary>Explains why changes are kept for this session only, if they are.</summary>
    public string? FileWarning => _settings.LoadStatus switch
    {
        SettingsLoadStatus.Corrupt => Localizer["Settings_Corrupt"],
        SettingsLoadStatus.NewerSchema => Localizer["Settings_NewerSchema"],
        SettingsLoadStatus.Unreadable => Localizer["Settings_Unreadable"],
        _ => null,
    };

    /// <summary>Only a damaged file may be replaced; a newer one belongs to a newer Bonfire.</summary>
    public bool CanReplaceFile => _settings.LoadStatus is SettingsLoadStatus.Corrupt or SettingsLoadStatus.Unreadable;

    protected override void OnLanguageChanged()
    {
        base.OnLanguageChanged();
        OnPropertyChanged(nameof(StatusMessage));
        OnPropertyChanged(nameof(FileWarning));
    }

    partial void OnSelectedLanguageChanged(LanguageOption value) => _ = ApplyLanguageAsync(value);

    private async Task ApplyLanguageAsync(LanguageOption value)
    {
        Localizer.SetLanguage(value.Code);
        StatusKey = null;
        var saved = await _settings.UpdateAsync(s => s with { Language = value.Code });
        StatusKey = !saved ? "Settings_SaveFailed" : _settings.CanSave ? "Settings_Saved" : null;
    }

    [RelayCommand]
    private async Task ReplaceFileAsync()
    {
        var replaced = await _settings.ReplaceFileAsync();
        StatusKey = replaced ? "Settings_Saved" : "Settings_SaveFailed";
        OnPropertyChanged(nameof(FileWarning));
        OnPropertyChanged(nameof(CanReplaceFile));
    }
}
