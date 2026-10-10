using Bonfire.Core.Servers;
using Docker.DotNet.Models;
using ContainerSpec = Bonfire.Core.Servers.ContainerSpec;
using DockerPortBinding = Docker.DotNet.Models.PortBinding;
using DockerRestartPolicy = Docker.DotNet.Models.RestartPolicy;
using DockerRestartPolicyKind = Docker.DotNet.Models.RestartPolicyKind;

namespace Bonfire.Infrastructure.Docker;

/// <summary>Maps Bonfire's <see cref="ContainerSpec"/> to Docker.DotNet models and back.</summary>
internal static class ContainerMapping
{
    private static string PortKey(int port) => $"{port}/tcp";

    public static CreateContainerParameters ToCreateParameters(ContainerSpec spec) => new()
    {
        Name = spec.Name,
        Image = spec.Image,
        User = spec.User,
        Env = spec.Environment.Select(kv => $"{kv.Key}={kv.Value}").ToList(),
        Labels = new Dictionary<string, string>(spec.Labels),
        ExposedPorts = new Dictionary<string, EmptyStruct> { [PortKey(spec.Port.ContainerPort)] = default },
        HostConfig = new HostConfig
        {
            // Mounts (not Binds): a missing source is an error instead of being created as root.
            Mounts = spec.Binds.Select(b => new Mount { Type = "bind", Source = b.Source, Target = b.Target, ReadOnly = b.ReadOnly }).ToList(),
            Tmpfs = new Dictionary<string, string>(spec.Tmpfs),
            PortBindings = new Dictionary<string, IList<DockerPortBinding>>
            {
                [PortKey(spec.Port.ContainerPort)] =
                [
                    new DockerPortBinding { HostIP = spec.Port.HostIp, HostPort = spec.Port.HostPort.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                ],
            },
            Memory = spec.MemoryBytes,
            MemorySwap = spec.MemorySwapBytes,
            NanoCPUs = spec.NanoCpus,
            PidsLimit = spec.PidsLimit,
            ReadonlyRootfs = spec.ReadOnlyRootfs,
            CapDrop = spec.CapDrop.ToList(),
            SecurityOpt = spec.SecurityOptions.ToList(),
            NetworkMode = spec.NetworkName,
            RestartPolicy = new DockerRestartPolicy
            {
                Name = ToDockerPolicy(spec.RestartPolicy),
                MaximumRetryCount = spec.RestartMaxRetries,
            },
            LogConfig = new LogConfig
            {
                Type = "json-file",
                Config = new Dictionary<string, string> { ["max-size"] = spec.LogMaxSize, ["max-file"] = spec.LogMaxFile },
            },
        },
    };

    public static DockerRestartPolicyKind ToDockerPolicy(Core.Servers.RestartPolicyKind policy) => policy switch
    {
        Core.Servers.RestartPolicyKind.No => DockerRestartPolicyKind.No,
        Core.Servers.RestartPolicyKind.OnFailure => DockerRestartPolicyKind.OnFailure,
        Core.Servers.RestartPolicyKind.UnlessStopped => DockerRestartPolicyKind.UnlessStopped,
        _ => throw new ArgumentOutOfRangeException(nameof(policy)),
    };

    public static ObservedContainer ToObserved(ContainerInspectResponse inspect)
    {
        var host = inspect.HostConfig ?? new HostConfig();
        var config = inspect.Config ?? new ContainerConfig();

        // Kept as a list: duplicate keys must be detectable, not silently merged.
        var env = (config.Env ?? [])
            .Select(entry => entry.Split('=', 2))
            .Select(p => new KeyValuePair<string, string>(p[0], p.Length > 1 ? p[1] : ""))
            .ToList();

        var ports = (host.PortBindings ?? new Dictionary<string, IList<DockerPortBinding>>())
            .SelectMany(kv =>
            {
                var key = kv.Key.Split('/');
                var containerPort = int.TryParse(key[0], System.Globalization.CultureInfo.InvariantCulture, out var cp) ? cp : 0;
                var protocol = key.Length > 1 ? key[1] : "tcp";
                return (kv.Value ?? []).Select(b => new Core.Servers.PortBinding(
                    containerPort,
                    protocol,
                    string.IsNullOrEmpty(b.HostIP) ? "0.0.0.0" : b.HostIP,
                    int.TryParse(b.HostPort, System.Globalization.CultureInfo.InvariantCulture, out var hp) ? hp : 0));
            })
            .ToList();

        return new ObservedContainer(
            Image: config.Image,
            User: config.User,
            Environment: env,
            Entrypoint: config.Entrypoint?.ToList() ?? [],
            Cmd: config.Cmd?.ToList() ?? [],
            StopSignal: string.IsNullOrEmpty(config.StopSignal) ? null : config.StopSignal,
            WorkingDir: string.IsNullOrEmpty(config.WorkingDir) ? null : config.WorkingDir,
            Healthcheck: Canonical(config.Healthcheck),
            Labels: config.Labels is null ? new Dictionary<string, string>() : new Dictionary<string, string>(config.Labels),
            Mounts: (inspect.Mounts ?? []).Select(m => new BindMount(m.Source, m.Destination, !m.RW)).ToList(),
            Tmpfs: host.Tmpfs is null ? new Dictionary<string, string>() : new Dictionary<string, string>(host.Tmpfs),
            Ports: ports,
            PublishAllPorts: host.PublishAllPorts,
            Networks: (inspect.NetworkSettings?.Networks ?? new Dictionary<string, EndpointSettings>())
                .ToDictionary(kv => kv.Key, kv => kv.Value?.NetworkID ?? ""),
            NetworkMode: host.NetworkMode,
            MemoryBytes: host.Memory,
            MemorySwapBytes: host.MemorySwap,
            NanoCpus: host.NanoCPUs,
            PidsLimit: host.PidsLimit ?? 0,
            ReadOnlyRootfs: host.ReadonlyRootfs,
            Privileged: host.Privileged,
            CapAdd: host.CapAdd?.ToList() ?? [],
            CapDrop: host.CapDrop?.ToList() ?? [],
            SecurityOptions: host.SecurityOpt?.ToList() ?? [],
            PidMode: host.PidMode,
            IpcMode: host.IpcMode,
            UtsMode: host.UTSMode,
            UsernsMode: host.UsernsMode,
            CgroupnsMode: host.CgroupnsMode,
            DeviceCount: host.Devices?.Count ?? 0,
            DeviceRequestCount: host.DeviceRequests?.Count ?? 0,
            DeviceCgroupRuleCount: host.DeviceCgroupRules?.Count ?? 0,
            SysctlCount: host.Sysctls?.Count ?? 0,
            VolumesFromCount: host.VolumesFrom?.Count ?? 0,
            LinkCount: host.Links?.Count ?? 0,
            Runtime: host.Runtime,
            OomKillDisabled: host.OomKillDisable == true,
            RestartPolicy: host.RestartPolicy?.Name switch
            {
                DockerRestartPolicyKind.No or DockerRestartPolicyKind.Undefined or null => "no",
                DockerRestartPolicyKind.OnFailure => "on-failure",
                DockerRestartPolicyKind.UnlessStopped => "unless-stopped",
                DockerRestartPolicyKind.Always => "always",
                _ => "unknown",
            },
            RestartMaxRetries: (int)(host.RestartPolicy?.MaximumRetryCount ?? 0),
            LogDriver: host.LogConfig?.Type ?? "",
            LogOptions: host.LogConfig?.Config is null
                ? new Dictionary<string, string>()
                : new Dictionary<string, string>(host.LogConfig.Config));
    }

    public static ImageDefaults ToImageDefaults(ImageInspectResponse image)
    {
        var config = image.Config ?? new DockerOCIImageConfig();
        var env = new Dictionary<string, string>();
        foreach (var entry in config.Env ?? [])
        {
            var parts = entry.Split('=', 2);
            env[parts[0]] = parts.Length > 1 ? parts[1] : "";
        }

        return new ImageDefaults(
            env,
            config.Entrypoint?.ToList() ?? [],
            config.Cmd?.ToList() ?? [],
            string.IsNullOrEmpty(config.StopSignal) ? null : config.StopSignal,
            string.IsNullOrEmpty(config.WorkingDir) ? null : config.WorkingDir,
            Canonical(config.Healthcheck));
    }

    /// <summary>Whole healthcheck as one comparable string, so timing/retry overrides are visible too.</summary>
    private static string? Canonical(HealthcheckConfig? h) =>
        h is null ? null : string.Join('|',
            string.Join(' ', h.Test ?? []), h.Interval, h.Timeout, h.StartPeriod, h.StartInterval, h.Retries);

    public static ObservedNetwork ToObservedNetwork(NetworkResponse network) => new(
        network.ID,
        network.Name,
        network.Driver,
        network.Internal,
        network.EnableIPv6,
        network.Attachable,
        network.Ingress,
        network.ConfigOnly,
        network.Options is null ? new Dictionary<string, string>() : new Dictionary<string, string>(network.Options),
        network.Labels is null ? new Dictionary<string, string>() : new Dictionary<string, string>(network.Labels));

    public static ContainerHealth ToHealth(string? status) => status switch
    {
        "starting" => ContainerHealth.Starting,
        "healthy" => ContainerHealth.Healthy,
        "unhealthy" => ContainerHealth.Unhealthy,
        _ => ContainerHealth.None,
    };
}
