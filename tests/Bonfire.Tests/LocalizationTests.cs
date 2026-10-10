using System.Text.RegularExpressions;
using System.Xml.Linq;
using Bonfire.App.Localization;
using Bonfire.Core;

namespace Bonfire.Tests;

[Collection(LocalizerCollection.Name)]
public sealed partial class LocalizationTests
{
    private static string LocalizationDir
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Bonfire.slnx")))
            {
                dir = dir.Parent;
            }

            Assert.NotNull(dir);
            return Path.Combine(dir.FullName, "src", "Bonfire.App", "Localization");
        }
    }

    // Raw .resx comparison: ResourceManager fallback would hide a missing Polish entry.
    private static Dictionary<string, string> ReadResx(string fileName) =>
        XDocument.Load(Path.Combine(LocalizationDir, fileName))
            .Root!.Elements("data")
            .ToDictionary(e => (string)e.Attribute("name")!, e => (string?)e.Element("value") ?? "");

    [GeneratedRegex(@"\{(\d+)(?:[,:][^}]*)?\}")]
    private static partial Regex Placeholder();

    private static int[] Placeholders(string value) =>
        Placeholder().Matches(value).Select(m => int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture))
            .Distinct().Order().ToArray();

    [Fact]
    public void EnglishAndPolishHaveTheSameKeys()
    {
        var en = ReadResx("Strings.resx");
        var pl = ReadResx("Strings.pl.resx");

        Assert.Empty(en.Keys.Except(pl.Keys));
        Assert.Empty(pl.Keys.Except(en.Keys));
    }

    [Fact]
    public void NoValueIsBlank()
    {
        foreach (var file in new[] { "Strings.resx", "Strings.pl.resx" })
        {
            foreach (var (key, value) in ReadResx(file))
            {
                Assert.False(string.IsNullOrWhiteSpace(value), $"{file}: {key} is blank");
            }
        }
    }

    [Fact]
    public void FormatPlaceholdersMatch()
    {
        var en = ReadResx("Strings.resx");
        var pl = ReadResx("Strings.pl.resx");

        foreach (var (key, value) in en)
        {
            Assert.True(Placeholders(value).SequenceEqual(Placeholders(pl[key])), $"Placeholders differ for {key}");
        }
    }

    [Fact]
    public void SwitchingLanguageChangesTextAndNotifiesBindings()
    {
        var localizer = Localizer.Instance;
        var raised = new List<string?>();
        localizer.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        try
        {
            localizer.SetLanguage(AppLanguage.Polish);
            Assert.Equal("Serwery", localizer["Nav_Servers"]);
            Assert.Contains("Item[]", raised);

            localizer.SetLanguage(AppLanguage.English);
            Assert.Equal("Servers", localizer["Nav_Servers"]);
        }
        finally
        {
            localizer.SetLanguage(AppLanguage.Default);
        }
    }

    [Fact]
    public void UnsupportedLanguageFallsBackToEnglish()
    {
        Localizer.Instance.SetLanguage("de");

        Assert.Equal(AppLanguage.English, Localizer.Instance.Language);
    }

    [Fact]
    public void MissingKeyIsVisible()
    {
        Assert.Equal("[No_Such_Key]", Localizer.Instance["No_Such_Key"]);
    }

    [Fact]
    public void PolishSatelliteAssemblyIsDeployed()
    {
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "pl", "bonfire.resources.dll")));
    }
}
