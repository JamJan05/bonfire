namespace Bonfire.Core.Servers;

/// <summary>
/// Compares a container's (and its network's) real configuration with what Bonfire asked for.
/// The comparison is an allowlist: anything not explicitly expected — an extra environment
/// variable, mount, port, security option, device or namespace — is a mismatch, and a
/// mismatching container must not be started.
/// </summary>
public static class HardeningVerifier
{
    public static IReadOnlyList<string> Verify(
        ContainerSpec spec,
        ImageDefaults image,
        ObservedContainer observed,
        string expectedNetworkId)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(observed);
        var problems = new List<string>();

        void Check(bool ok, string problem)
        {
            if (!ok)
            {
                problems.Add(problem);
            }
        }

        // What runs, and as whom.
        Check(observed.Image == spec.Image, "image differs from the pinned digest");
        Check(observed.User == spec.User, "container user differs");
        Check(observed.Entrypoint.SequenceEqual(image.Entrypoint), "entrypoint is overridden");
        Check(observed.Cmd.SequenceEqual(image.Cmd), "command is overridden");
        Check((observed.StopSignal ?? image.StopSignal) == image.StopSignal, "stop signal is overridden");
        Check((observed.WorkingDir ?? image.WorkingDir) == image.WorkingDir, "working directory is overridden");
        Check(observed.Healthcheck is null || observed.Healthcheck == image.Healthcheck, "healthcheck is overridden");
        Check(EnvironmentMatches(spec, image, observed.Environment), "environment differs (unexpected, missing or duplicate variables)");

        // Privileges.
        Check(!observed.Privileged, "container is privileged");
        Check(observed.ReadOnlyRootfs == spec.ReadOnlyRootfs, "root filesystem is not read-only");
        Check(observed.CapAdd.Count == 0, "capabilities were added");
        Check(observed.CapDrop.Count == 1 && string.Equals(observed.CapDrop[0], "ALL", StringComparison.OrdinalIgnoreCase), "capabilities are not all dropped");
        Check(observed.SecurityOptions.Count == 1 && IsNoNewPrivileges(observed.SecurityOptions[0]),
            "security options differ (custom seccomp/AppArmor/SELinux profile or missing no-new-privileges)");
        Check(observed.Runtime is null or "" or "runc", "a non-default container runtime is used");

        // Namespaces and host access.
        Check(observed.PidMode is null or "", "PID namespace is shared");
        Check(observed.IpcMode is null or "" or "private" or "shareable" or "none", "IPC namespace is shared");
        Check(observed.UtsMode is null or "", "UTS namespace is shared");
        Check(observed.UsernsMode is null or "", "user namespace mode is overridden");
        Check(observed.CgroupnsMode is null or "" or "private", "cgroup namespace is shared");
        Check(observed.DeviceCount == 0 && observed.DeviceRequestCount == 0 && observed.DeviceCgroupRuleCount == 0, "host devices are accessible");
        Check(observed.SysctlCount == 0, "kernel parameters (sysctls) are set");
        Check(observed.VolumesFromCount == 0 && observed.LinkCount == 0, "volumes or links from other containers");

        // Resources.
        Check(observed.MemoryBytes == spec.MemoryBytes, "memory limit differs");
        Check(observed.MemorySwapBytes == spec.MemorySwapBytes, "swap limit differs");
        Check(observed.NanoCpus == spec.NanoCpus, "CPU limit differs");
        Check(observed.PidsLimit == spec.PidsLimit, "process limit differs");
        Check(!observed.OomKillDisabled, "OOM killer is disabled");
        Check(observed.LogDriver == "json-file"
              && observed.LogOptions.Count == 2
              && observed.LogOptions.GetValueOrDefault("max-size") == spec.LogMaxSize
              && observed.LogOptions.GetValueOrDefault("max-file") == spec.LogMaxFile,
            "log rotation differs");

        // Filesystem and network exposure.
        Check(SameSet(observed.Mounts, spec.Binds), "mounts differ");
        Check(SameDictionary(observed.Tmpfs, spec.Tmpfs), "tmpfs mounts differ");
        Check(observed.Ports.Count == 1 && observed.Ports[0] == spec.Port, "published ports differ");
        Check(!observed.PublishAllPorts, "all exposed ports are published automatically");
        // Docker fills the endpoint's network id only once the container has run; before that the
        // network is resolved by name at start, which the caller checks maps to expectedNetworkId.
        Check(observed.NetworkMode == spec.NetworkName
              && observed.Networks.Count == 1
              && observed.Networks.TryGetValue(spec.NetworkName, out var networkId)
              && (networkId == expectedNetworkId || networkId.Length == 0),
            "container is not attached only to its own network");

        Check(spec.Labels.All(l => observed.Labels.TryGetValue(l.Key, out var v) && v == l.Value), "labels differ");
        Check(observed.RestartPolicy == DockerRestartPolicyName(spec.RestartPolicy)
              && observed.RestartMaxRetries == spec.RestartMaxRetries,
            "restart policy differs");

        return problems;
    }

    /// <summary>The network must be exactly the plain bridge Bonfire creates.</summary>
    public static IReadOnlyList<string> VerifyNetwork(ContainerSpec spec, ObservedNetwork network)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(network);
        var problems = new List<string>();
        if (network.Name != spec.NetworkName)
        {
            problems.Add("network name differs");
        }

        if (network.Driver != "bridge" || network.Internal || network.EnableIpv6 || network.Attachable || network.Ingress || network.ConfigOnly)
        {
            problems.Add("network driver or mode differs");
        }

        if (network.Options.Count != 0)
        {
            problems.Add("network has driver options (e.g. a changed gateway mode)");
        }

        if (!spec.Labels.All(l => network.Labels.TryGetValue(l.Key, out var v) && v == l.Value))
        {
            problems.Add("network labels differ");
        }

        return problems;
    }

    /// <summary>
    /// After a start: what Docker really published must be exactly the requested binding — same
    /// container port and protocol, the same host address (compared as an address) and host port,
    /// nothing more (a wildcard or second family would widen the exposure) and nothing less.
    /// Returns the problem, or null when the publication matches.
    /// </summary>
    public static string? VerifyPublishedPort(ContainerSpec spec, IReadOnlyList<PortBinding> effective)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(effective);
        if (effective.Count == 0)
        {
            return "the game port is not published";
        }

        var expected = spec.Port;
        // "All interfaces" is 0.0.0.0 explicitly, which Docker 29.8 publishes over IPv4 only; a daemon
        // that adds the IPv6 wildcard for the same port still honours "every interface", so that one
        // extra binding is tolerated. Any other mode gets exactly one binding.
        var tolerated = expected.HostIp == "0.0.0.0" && effective.Count == 2
            ? effective.Where(b => !(b.HostIp is "::" or "[::]" && b.HostPort == expected.HostPort && b.ContainerPort == expected.ContainerPort && b.Protocol == expected.Protocol)).ToList()
            : effective;
        if (tolerated.Count != 1)
        {
            return $"{effective.Count} ports are published instead of one";
        }

        var p = tolerated[0];
        return p.ContainerPort == expected.ContainerPort
               && p.Protocol == expected.Protocol
               && p.HostPort == expected.HostPort
               && !string.IsNullOrEmpty(p.HostIp)
               && HostAddressRules.AreEqual(p.HostIp, expected.HostIp)
            ? null
            : $"published {p.HostIp}:{p.HostPort}->{p.ContainerPort}/{p.Protocol} instead of {expected.HostIp}:{expected.HostPort}->{expected.ContainerPort}/{expected.Protocol}";
    }

    public static string DockerRestartPolicyName(RestartPolicyKind kind) => kind switch
    {
        RestartPolicyKind.No => "no",
        RestartPolicyKind.OnFailure => "on-failure",
        RestartPolicyKind.UnlessStopped => "unless-stopped",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>Observed environment must equal the image defaults overlaid with the spec — nothing more.</summary>
    private static bool EnvironmentMatches(ContainerSpec spec, ImageDefaults image, IReadOnlyList<KeyValuePair<string, string>> observed)
    {
        var expected = new Dictionary<string, string>(image.Environment);
        foreach (var (key, value) in spec.Environment)
        {
            expected[key] = value;
        }

        if (observed.Count != expected.Count || observed.Select(kv => kv.Key).Distinct().Count() != observed.Count)
        {
            return false;
        }

        return observed.All(kv => expected.TryGetValue(kv.Key, out var v) && v == kv.Value);
    }

    // Docker reports "no-new-privileges" or "no-new-privileges:true".
    private static bool IsNoNewPrivileges(string option) =>
        option is "no-new-privileges" or "no-new-privileges:true" or "no-new-privileges=true";

    private static bool SameSet<T>(IReadOnlyList<T> a, IReadOnlyList<T> b) =>
        a.Count == b.Count && a.All(b.Contains);

    private static bool SameDictionary(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);
}
