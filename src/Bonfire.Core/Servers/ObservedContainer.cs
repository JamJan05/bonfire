namespace Bonfire.Core.Servers;

/// <summary>Execution defaults baked into the pinned image; a container may not deviate from them.</summary>
public sealed record ImageDefaults(
    IReadOnlyDictionary<string, string> Environment,
    IReadOnlyList<string> Entrypoint,
    IReadOnlyList<string> Cmd,
    string? StopSignal,
    string? WorkingDir,
    string? Healthcheck);

/// <summary>Normalized view of a container's actual configuration, read back from Docker.</summary>
public sealed record ObservedContainer(
    string Image,
    string User,
    IReadOnlyList<KeyValuePair<string, string>> Environment,
    IReadOnlyList<string> Entrypoint,
    IReadOnlyList<string> Cmd,
    string? StopSignal,
    string? WorkingDir,
    string? Healthcheck, // canonical "test|interval|timeout|start|retries", null when not set on the container
    IReadOnlyDictionary<string, string> Labels,
    IReadOnlyList<BindMount> Mounts,
    IReadOnlyDictionary<string, string> Tmpfs,
    IReadOnlyList<PortBinding> Ports,
    bool PublishAllPorts,
    IReadOnlyDictionary<string, string> Networks, // network name → network id ("" until first start)
    string? NetworkMode,
    long MemoryBytes,
    long MemorySwapBytes,
    long NanoCpus,
    long PidsLimit,
    bool ReadOnlyRootfs,
    bool Privileged,
    IReadOnlyList<string> CapAdd,
    IReadOnlyList<string> CapDrop,
    IReadOnlyList<string> SecurityOptions,
    string? PidMode,
    string? IpcMode,
    string? UtsMode,
    string? UsernsMode,
    string? CgroupnsMode,
    int DeviceCount,
    int DeviceRequestCount,
    int DeviceCgroupRuleCount,
    int SysctlCount,
    int VolumesFromCount,
    int LinkCount,
    string? Runtime,
    bool OomKillDisabled,
    string RestartPolicy,
    int RestartMaxRetries,
    string LogDriver,
    IReadOnlyDictionary<string, string> LogOptions);

/// <summary>Normalized view of a Docker network.</summary>
public sealed record ObservedNetwork(
    string Id,
    string Name,
    string Driver,
    bool Internal,
    bool EnableIpv6,
    bool Attachable,
    bool Ingress,
    bool ConfigOnly,
    IReadOnlyDictionary<string, string> Options,
    IReadOnlyDictionary<string, string> Labels);
