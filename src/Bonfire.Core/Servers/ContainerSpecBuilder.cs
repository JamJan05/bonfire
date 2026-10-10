using System.Globalization;

namespace Bonfire.Core.Servers;

/// <summary>Turns a validated definition into the hardened container specification.</summary>
public static class ContainerSpecBuilder
{
    public const int MinecraftPort = 25565;
    public const string DataMountPath = "/data";
    public const string RconSecretPath = "/run/secrets/rcon";
    public const long PidsLimit = 512;
    public const string TmpfsOptions = "rw,noexec,nosuid,nodev,size=256m,mode=1777";

    public static ContainerSpec Build(ServerDefinition definition, ServerPaths paths, int hostUid, int hostGid, Guid installationId)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(paths);
        if (hostUid <= 0 || hostGid <= 0)
        {
            throw new ArgumentException("Containers never run as root; a regular host user is required.");
        }

        var errors = ServerDefinitionValidator.Validate(definition);
        if (errors.Count > 0)
        {
            throw new ArgumentException($"Invalid server definition: {string.Join(", ", errors)}", nameof(definition));
        }

        var memory = definition.Resources.ContainerMemoryBytes;
        return new ContainerSpec(
            Name: definition.ResourceName,
            NetworkName: definition.ResourceName,
            Image: definition.Image.PinnedReference,
            User: $"{hostUid}:{hostGid}",
            Environment: BuildEnvironment(definition),
            Labels: BuildLabels(definition, installationId),
            Binds:
            [
                new BindMount(paths.DataDir, DataMountPath, ReadOnly: false),
                new BindMount(paths.RconPasswordFile, RconSecretPath, ReadOnly: true),
            ],
            Tmpfs: new Dictionary<string, string> { ["/tmp"] = TmpfsOptions },
            Port: new PortBinding(
                MinecraftPort,
                "tcp",
                definition.Exposure == PortExposure.Localhost ? "127.0.0.1" : "0.0.0.0",
                definition.Port),
            MemoryBytes: memory,
            MemorySwapBytes: memory, // equal to memory: no swap
            NanoCpus: (long)Math.Round(definition.Resources.CpuLimit * 1_000_000_000),
            PidsLimit: PidsLimit,
            ReadOnlyRootfs: true,
            CapDrop: ["ALL"],
            SecurityOptions: ["no-new-privileges"],
            RestartPolicy: definition.RestartPolicy,
            RestartMaxRetries: definition.RestartPolicy == RestartPolicyKind.OnFailure ? 3 : 0,
            LogMaxSize: "10m",
            LogMaxFile: "3");
    }

    public static IReadOnlyDictionary<string, string> BuildLabels(ServerDefinition definition, Guid installationId) =>
        new Dictionary<string, string>
        {
            [ServerLabels.App] = ServerLabels.AppValue,
            [ServerLabels.Installation] = installationId.ToString("N"),
            [ServerLabels.ServerId] = definition.Id.ToString("N"),
            [ServerLabels.Schema] = ServerLabels.SpecVersion,
            [ServerLabels.HardeningProfile] = ServerLabels.HardeningProfileVersion,
        };

    private static Dictionary<string, string> BuildEnvironment(ServerDefinition d)
    {
        var game = d.Game;
        var env = new Dictionary<string, string>
        {
            ["EULA"] = "TRUE",
            ["TYPE"] = d.Type.ToString().ToUpperInvariant(),
            ["VERSION"] = d.MinecraftVersion,
            ["INIT_MEMORY"] = $"{d.Resources.HeapMinMb}M",
            ["MAX_MEMORY"] = $"{d.Resources.HeapMaxMb}M",
            ["ENABLE_RCON"] = "true",
            ["RCON_PASSWORD_FILE"] = RconSecretPath,
            // 0 disables the runner's kill timer: a slow world save is never cut short.
            ["STOP_DURATION"] = "0",
            ["ONLINE_MODE"] = game.OnlineMode ? "TRUE" : "FALSE",
            ["DIFFICULTY"] = game.Difficulty.ToString().ToLowerInvariant(),
            ["MAX_PLAYERS"] = game.MaxPlayers.ToString(CultureInfo.InvariantCulture),
            ["ENABLE_WHITELIST"] = game.WhitelistEnabled ? "TRUE" : "FALSE",
            // WHITELIST only seeds a missing whitelist.json; removals made in the game stay removed.
            ["EXISTING_WHITELIST_FILE"] = "SKIP",
        };

        void Optional(string name, string? value)
        {
            if (value is not null)
            {
                env[name] = value;
            }
        }

        static string? Bool(bool? value) => value is null ? null : value.Value ? "TRUE" : "FALSE";
        static string? Int(int? value) => value?.ToString(CultureInfo.InvariantCulture);

        Optional("MOTD", game.Motd);
        Optional("MODE", game.GameMode?.ToString().ToLowerInvariant());
        Optional("PVP", Bool(game.Pvp));
        Optional("VIEW_DISTANCE", Int(game.ViewDistance));
        Optional("SPAWN_PROTECTION", Int(game.SpawnProtection));
        Optional("ALLOW_FLIGHT", Bool(game.AllowFlight));
        Optional("ENABLE_COMMAND_BLOCK", Bool(game.EnableCommandBlock));
        Optional("ENFORCE_WHITELIST", Bool(game.EnforceWhitelist));

        if (game.WhitelistEnabled && game.Whitelist.Count > 0)
        {
            env["WHITELIST"] = string.Join(',', game.Whitelist);
        }

        if (!string.IsNullOrEmpty(game.Seed))
        {
            env["SEED"] = game.Seed;
        }

        switch (d.Type)
        {
            case ServerType.Fabric:
                env["FABRIC_LOADER_VERSION"] = d.FabricLoaderVersion!;
                env["FABRIC_LAUNCHER_VERSION"] = d.FabricLauncherVersion!;
                break;
            case ServerType.Paper:
                env["PAPER_BUILD"] = d.PaperBuild!.Value.ToString(CultureInfo.InvariantCulture);
                if (d.PaperExperimentalBuild)
                {
                    env["PAPER_CHANNEL"] = "experimental";
                }

                break;
        }

        return env;
    }
}
