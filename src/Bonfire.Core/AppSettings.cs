namespace Bonfire.Core;

/// <summary>User preferences persisted in the XDG config directory.</summary>
public sealed record AppSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public string Language { get; init; } = AppLanguage.Default;

    public const int DefaultBackupsToKeep = 5;
    public const int MaxBackupsToKeep = 100;

    /// <summary>How many manual backups are kept per server (the newest usable one is never deleted).</summary>
    public int BackupsToKeep { get; init; } = DefaultBackupsToKeep;
}

public enum SettingsLoadStatus
{
    /// <summary>No settings file yet; defaults are used and saving is allowed.</summary>
    Missing,

    /// <summary>File read successfully; saving is allowed.</summary>
    Loaded,

    /// <summary>File exists but is not valid; defaults are used and the file is kept until the user confirms replacing it.</summary>
    Corrupt,

    /// <summary>File was written by a newer version; recognised values are used for this session only.</summary>
    NewerSchema,

    /// <summary>File could not be read (permissions, I/O); defaults are used for this session only.</summary>
    Unreadable,
}

public sealed record SettingsLoadResult(AppSettings Settings, SettingsLoadStatus Status, string? Detail = null)
{
    /// <summary>Whether a normal save may overwrite the file on disk.</summary>
    public bool CanSave => Status is SettingsLoadStatus.Missing or SettingsLoadStatus.Loaded;
}
