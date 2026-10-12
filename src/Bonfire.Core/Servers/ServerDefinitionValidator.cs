using System.Text.RegularExpressions;

namespace Bonfire.Core.Servers;

/// <summary>
/// Validates a definition before it is persisted or turned into a container. Errors are
/// resource keys (<c>Validation_*</c>) so the UI can show them in the current language.
/// </summary>
public static partial class ServerDefinitionValidator
{
    public const int MinPort = 1024;
    public const int MaxPort = 65535;
    public const int MinHeapMb = 512;
    public const int MaxHeapMb = 64 * 1024;
    public const int MaxDisplayNameLength = 64;

    /// <summary>Java runtimes for which an itzg image tag exists.</summary>
    public static IReadOnlySet<int> SupportedJavaMajors { get; } = new HashSet<int> { 8, 11, 16, 17, 21, 25 };

    [GeneratedRegex(@"^[0-9A-Za-z][0-9A-Za-z.\-_+]{0,63}\z")]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"^sha256:[0-9a-f]{64}\z")]
    private static partial Regex DigestPattern();

    [GeneratedRegex(@"^java(8|11|16|17|21|25)\z")]
    private static partial Regex TagPattern();

    [GeneratedRegex(@"^[A-Za-z0-9_]{3,16}\z")]
    private static partial Regex PlayerNamePattern();

    public static IReadOnlyList<string> Validate(ServerDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var errors = new List<string>();

        var name = definition.DisplayName;
        if (string.IsNullOrWhiteSpace(name))
        {
            errors.Add("Validation_NameEmpty");
        }
        else if (name.Length > MaxDisplayNameLength || name.Any(char.IsControl) || name != name.Trim())
        {
            errors.Add("Validation_NameInvalid");
        }

        if (!IsVersion(definition.MinecraftVersion))
        {
            errors.Add("Validation_VersionInvalid");
        }

        switch (definition.Type)
        {
            case ServerType.Fabric when !IsVersion(definition.FabricLoaderVersion) || !IsVersion(definition.FabricLauncherVersion):
                errors.Add("Validation_LoaderMissing");
                break;
            case ServerType.Paper when definition.PaperBuild is not > 0:
                errors.Add("Validation_LoaderMissing");
                break;
        }

        if (!SupportedJavaMajors.Contains(definition.JavaMajor)
            || definition.Image.Repository != ImageReference.ItzgRepository
            || !TagPattern().IsMatch(definition.Image.Tag)
            || definition.Image.Tag != $"java{definition.JavaMajor}"
            || !DigestPattern().IsMatch(definition.Image.Digest))
        {
            errors.Add("Validation_ImageInvalid");
        }

        var resources = definition.Resources;
        if (resources.HeapMinMb < MinHeapMb || resources.HeapMaxMb > MaxHeapMb || resources.HeapMinMb > resources.HeapMaxMb)
        {
            errors.Add("Validation_MemoryInvalid");
        }

        if (!double.IsFinite(resources.CpuLimit) || resources.CpuLimit < 0.5 || resources.CpuLimit > Environment.ProcessorCount)
        {
            errors.Add("Validation_CpuInvalid");
        }

        if (definition.Port is < MinPort or > MaxPort)
        {
            errors.Add("Validation_PortInvalid");
        }

        switch (definition.Exposure)
        {
            case PortExposure.Localhost or PortExposure.AllInterfaces when definition.HostAddress is not null:
                errors.Add("Validation_ExposureInvalid");
                break;
            case PortExposure.SpecificAddress
                when !HostAddressRules.TryCanonicalize(definition.HostAddress, out var canonical)
                     || canonical != definition.HostAddress
                     || !HostAddressRules.IsBindable(canonical):
                errors.Add("Validation_HostAddressInvalid");
                break;
            case PortExposure.SpecificAddress when definition.RestartPolicy != RestartPolicyKind.No:
                // A daemon restart before the VPN is up would fail the start — and Docker then runs the
                // container without publishing the port at all (verified on Docker 29.8).
                errors.Add("Validation_SpecificAddressRestartPolicy");
                break;
            case PortExposure.Localhost or PortExposure.AllInterfaces or PortExposure.SpecificAddress:
                break;
            default:
                errors.Add("Validation_ExposureInvalid"); // an undefined enum value from a damaged record
                break;
        }

        if (!Enum.IsDefined(definition.RestartPolicy))
        {
            errors.Add("Validation_RestartPolicyInvalid");
        }

        var game = definition.Game;
        if (game.MaxPlayers is < 1 or > 1000)
        {
            errors.Add("Validation_MaxPlayersInvalid");
        }

        if (game.Whitelist.Any(entry => entry is null || (!PlayerNamePattern().IsMatch(entry) && !Guid.TryParse(entry, out _))))
        {
            errors.Add("Validation_WhitelistInvalid");
        }

        if (game.Seed is { } seed && (seed.Length > 64 || seed.Any(char.IsControl)))
        {
            errors.Add("Validation_SeedInvalid");
        }

        if (game.Motd is { } motd && !IsSafeMotd(motd))
        {
            errors.Add("Validation_MotdInvalid");
        }

        if (game.ViewDistance is < 2 or > 32)
        {
            errors.Add("Validation_ViewDistanceInvalid");
        }

        if (game.SpawnProtection is < 0 or > 1000)
        {
            errors.Add("Validation_SpawnProtectionInvalid");
        }

        if (definition.EulaAcceptedAt == default)
        {
            errors.Add("Validation_EulaRequired");
        }

        return errors;
    }

    /// <summary>Floating aliases would let the image pick a different version on a later start.</summary>
    private static readonly HashSet<string> FloatingAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        "latest", "snapshot", "release", "stable", "recommended", "experimental",
    };

    public const int MaxMotdLength = 120;

    /// <summary>
    /// MOTD is passed to the image, which expands %VAR%, %env:VAR% and %date:…% placeholders — so
    /// "%env:RCON_PASSWORD%" would publish the RCON password. '%', '$', '\' and control characters
    /// (property injection, escapes) are therefore refused; § formatting codes are allowed.
    /// </summary>
    public static bool IsSafeMotd(string motd) =>
        motd.Length <= MaxMotdLength && !motd.Any(c => char.IsControl(c) || c is '%' or '$' or '\\');

    /// <summary>A concrete version string (no aliases, safe characters) — also used for values from APIs.</summary>
    public static bool IsConcreteVersion(string? value) => IsVersion(value);

    private static bool IsVersion(string? value) =>
        value is not null && VersionPattern().IsMatch(value) && !FloatingAliases.Contains(value);
}
