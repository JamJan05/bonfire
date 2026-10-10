using System.Text.Json;
using Bonfire.Core;
using Bonfire.Infrastructure.Linux;

namespace Bonfire.Infrastructure;

/// <summary>Reads and writes <see cref="AppSettings"/> as JSON.</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public SettingsStore(string path) => FilePath = path;

    public string FilePath { get; }

    /// <summary>Never throws for I/O or format problems; they are reported in the status.</summary>
    public SettingsLoadResult Load()
    {
        string json;
        try
        {
            // Path.Exists, not File.Exists: anything occupying the path (e.g. a directory)
            // must be reported as unreadable rather than silently treated as missing.
            if (!Path.Exists(FilePath))
            {
                return new SettingsLoadResult(new AppSettings(), SettingsLoadStatus.Missing);
            }

            json = File.ReadAllText(FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new SettingsLoadResult(new AppSettings(), SettingsLoadStatus.Unreadable, ex.Message);
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty(nameof(AppSettings.SchemaVersion), out var versionElement)
                || versionElement.ValueKind != JsonValueKind.Number
                || !versionElement.TryGetInt32(out var schemaVersion)
                || schemaVersion < 1)
            {
                return new SettingsLoadResult(new AppSettings(), SettingsLoadStatus.Corrupt, "Missing or invalid SchemaVersion.");
            }

            // For a newer schema only extract values we can positively recognise.
            var language = root.TryGetProperty(nameof(AppSettings.Language), out var languageElement)
                           && languageElement.ValueKind == JsonValueKind.String
                ? AppLanguage.Normalize(languageElement.GetString())
                : AppLanguage.Default;

            var keep = root.TryGetProperty(nameof(AppSettings.BackupsToKeep), out var keepElement)
                       && keepElement.ValueKind == JsonValueKind.Number && keepElement.TryGetInt32(out var k)
                ? Math.Clamp(k, 1, AppSettings.MaxBackupsToKeep)
                : AppSettings.DefaultBackupsToKeep;
            var settings = new AppSettings { SchemaVersion = AppSettings.CurrentSchemaVersion, Language = language, BackupsToKeep = keep };
            var status = schemaVersion > AppSettings.CurrentSchemaVersion
                ? SettingsLoadStatus.NewerSchema
                : SettingsLoadStatus.Loaded;
            return new SettingsLoadResult(settings, status);
        }
        catch (JsonException ex)
        {
            return new SettingsLoadResult(new AppSettings(), SettingsLoadStatus.Corrupt, ex.Message);
        }
    }

    public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var normalized = settings with
        {
            SchemaVersion = AppSettings.CurrentSchemaVersion,
            Language = AppLanguage.Normalize(settings.Language),
            BackupsToKeep = Math.Clamp(settings.BackupsToKeep, 1, AppSettings.MaxBackupsToKeep),
        };
        return AtomicFile.WriteAllTextAsync(FilePath, JsonSerializer.Serialize(normalized, WriteOptions), cancellationToken);
    }
}
