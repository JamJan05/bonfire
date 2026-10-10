namespace Bonfire.Core.Servers;

public sealed record BindMount(string Source, string Target, bool ReadOnly);

public sealed record PortBinding(int ContainerPort, string Protocol, string HostIp, int HostPort);

/// <summary>
/// Docker-agnostic description of the container Bonfire creates for a server. Built by
/// <see cref="ContainerSpecBuilder"/>; the Docker adapter maps it 1:1 and verifies the result.
/// </summary>
public sealed record ContainerSpec(
    string Name,
    string NetworkName,
    string Image,
    string User,
    IReadOnlyDictionary<string, string> Environment,
    IReadOnlyDictionary<string, string> Labels,
    IReadOnlyList<BindMount> Binds,
    IReadOnlyDictionary<string, string> Tmpfs,
    PortBinding Port,
    long MemoryBytes,
    long MemorySwapBytes,
    long NanoCpus,
    long PidsLimit,
    bool ReadOnlyRootfs,
    IReadOnlyList<string> CapDrop,
    IReadOnlyList<string> SecurityOptions,
    RestartPolicyKind RestartPolicy,
    int RestartMaxRetries,
    string LogMaxSize,
    string LogMaxFile);
