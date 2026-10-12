using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using Bonfire.Core.Logs;
using Bonfire.Core.Rcon;
using Bonfire.Core.Servers;
using Docker.DotNet;
using Docker.DotNet.Models;
using ContainerSpec = Bonfire.Core.Servers.ContainerSpec;
using DockerRestartPolicyType = Docker.DotNet.Models.RestartPolicy;
using Microsoft.Extensions.Logging;

namespace Bonfire.Infrastructure.Docker;

/// <summary>
/// Runs servers as hardened containers. Every mutation of one server is serialized, every
/// container is verified against its spec before it starts, and resources are found by the
/// pair (installation id, server id) — never by name or a generic label alone.
/// </summary>
public sealed class DockerServerRuntime : IServerRuntime
{
    private readonly DockerConnection _connection;
    private readonly Func<ServerDefinition, ServerPaths> _pathsFor;
    private readonly Guid _installationId;
    private readonly int _uid;
    private readonly int _gid;
    private readonly IHostAddresses _hostAddresses;
    private readonly ILogger<DockerServerRuntime> _logger;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();

    /// <summary>Stopping a container that started without its port is bounded and independent of the caller's cancellation.</summary>
    private static readonly TimeSpan ContainmentTimeout = TimeSpan.FromSeconds(30);

    /// <summary>rcon-cli inside the pinned image (verified: /usr/local/bin/rcon-cli).</summary>
    public const string RconCliPath = "/usr/local/bin/rcon-cli";

    /// <summary>
    /// Exec instances whose end was not confirmed (timeout, cancellation, transport error), with
    /// their container. A new command waits until Docker confirms they finished or forgot them.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, (string ContainerId, string ExecId)> _unfinishedExecs = new();

    /// <summary>Stops in progress; a kill may escalate them (see <see cref="StopInProgress"/>).</summary>
    private readonly ConcurrentDictionary<Guid, StopInProgress> _activeStops = new();

    /// <summary>
    /// A running stop and the kills escalating it. The stop keeps the server's mutation lock until
    /// every escalation registered while it was active has finished, so a delayed kill can never
    /// hit a server that was started again after the stop.
    /// </summary>
    private sealed class StopInProgress
    {
        public required Task<string> VerifiedContainerId { get; init; }

        public bool Active { get; set; } = true;

        public List<Task> Escalations { get; } = [];
    }

    public DockerServerRuntime(
        DockerConnection connection,
        Func<ServerDefinition, ServerPaths> pathsFor,
        Guid installationId,
        int uid,
        int gid,
        IHostAddresses hostAddresses,
        ILogger<DockerServerRuntime> logger)
    {
        _connection = connection;
        _pathsFor = pathsFor;
        _installationId = installationId;
        _uid = uid;
        _gid = gid;
        _hostAddresses = hostAddresses;
        _logger = logger;
    }

    public async Task<ResolvedImage> AcquireImageAsync(string tag, IProgress<ImagePullProgress>? progress, CancellationToken cancellationToken)
    {
        var client = await _connection.GetClientAsync(cancellationToken);
        var repository = ImageReference.ItzgRepository;
        try
        {
            await client.Images.CreateImageAsync(
                new ImagesCreateParameters { FromImage = repository, Tag = tag },
                authConfig: null,
                new Progress<JSONMessage>(m =>
                {
                    if (m.Error is not null)
                    {
                        _logger.LogWarning("Image pull reported an error: {Error}", m.Error.Message);
                    }

                    progress?.Report(new ImagePullProgress(m.ID, m.Status ?? "", m.Progress?.Current, m.Progress?.Total));
                }),
                cancellationToken);

            var image = await client.Images.InspectImageAsync($"{repository}:{tag}", cancellationToken);

            // Only accept a digest for the exact repository we pulled (Docker Hub short form).
            var digest = image.RepoDigests?
                .Select(d => d.Split('@'))
                .Where(p => p.Length == 2 && (p[0] == repository || p[0] == $"docker.io/{repository}"))
                .Select(p => p[1])
                .FirstOrDefault(d => d.StartsWith("sha256:", StringComparison.Ordinal));
            if (digest is null)
            {
                throw new ServerOperationException(ServerOperationError.ImageUnavailable, $"No digest for {repository}:{tag}");
            }

            return new ResolvedImage(new ImageReference(repository, tag, digest), image.ID);
        }
        catch (DockerApiException ex)
        {
            throw new ServerOperationException(ServerOperationError.ImageUnavailable, $"HTTP {(int)ex.StatusCode}", ex);
        }
    }

    public Task<ServerRecord> ProvisionAsync(ServerRecord record, Func<ServerRecord, Task> persist, CancellationToken cancellationToken) =>
        Locked(record, async () =>
        {
            var client = await _connection.GetClientAsync(cancellationToken);
            var spec = BuildSpec(record.Definition);

            // Phase is persisted before Docker is touched, so an interruption is always resumable.
            if (record.Phase != ProvisioningPhase.Provisioning)
            {
                record = record with { Phase = ProvisioningPhase.Provisioning };
                await persist(record);
            }

            var networkId = await FindNetworkAsync(client, record.Definition.Id, cancellationToken);
            if (networkId is not null)
            {
                await VerifyNetworkAsync(client, spec, networkId, cancellationToken);
            }
            else
            {
                var created = await client.Networks.CreateNetworkAsync(
                    new NetworksCreateParameters
                    {
                        Name = spec.NetworkName,
                        Driver = "bridge",
                        Internal = false, // Mojang authentication and server downloads need the internet
                        Labels = new Dictionary<string, string>(spec.Labels),
                    },
                    cancellationToken);
                networkId = created.ID;
            }

            record = record with { NetworkId = networkId };
            await persist(record);

            var existing = await FindContainerAsync(client, record.Definition.Id, cancellationToken);
            string containerId;
            if (existing is not null)
            {
                // Adopt a container from an interrupted attempt only if it is exactly what we asked for.
                var problems = await CheckContainerAsync(client, spec, existing.ID, networkId, cancellationToken);
                if (problems.Count == 0)
                {
                    containerId = existing.ID;
                }
                else
                {
                    _logger.LogWarning("Replacing half-created container {Id}: {Problems}", existing.ID, string.Join("; ", problems));
                    await client.Containers.RemoveContainerAsync(existing.ID, new ContainerRemoveParameters { Force = false }, cancellationToken);
                    containerId = await CreateContainerAsync(client, spec, cancellationToken);
                }
            }
            else
            {
                containerId = await CreateContainerAsync(client, spec, cancellationToken);
            }

            record = record with { ContainerId = containerId };
            await persist(record);

            await VerifyAsync(client, record, spec, cancellationToken);

            record = record with { Phase = ProvisioningPhase.Ready };
            await persist(record);
            _logger.LogInformation("Provisioned server {ServerId} as container {ContainerId}", record.Definition.Id, containerId);
            return record;
        });

    public Task StartAsync(ServerRecord record, CancellationToken cancellationToken) =>
        Locked(record, async () =>
        {
            var client = await _connection.GetClientAsync(cancellationToken);
            var containerId = RequireContainer(record);
            var spec = BuildSpec(record.Definition);
            await VerifyAsync(client, record, spec, cancellationToken);
            await RequireHostAddressAsync(record.Definition, cancellationToken); // enumerated afresh, right before the start
            try
            {
                await client.Containers.StartContainerAsync(containerId, new ContainerStartParameters(), cancellationToken);
            }
            catch (DockerApiException ex) when (IsPortConflict(ex))
            {
                throw new ServerOperationException(ServerOperationError.PortInUse, $"Port {record.Definition.Port} is already in use", ex);
            }
            catch (DockerApiException ex) when (IsBindFailure(ex))
            {
                // The address went away between the check and Docker's bind: the message is "VPN down", not "Docker error".
                if (await HostAddressVanishedAsync(record.Definition))
                {
                    throw new ServerOperationException(ServerOperationError.AddressNotPresent, record.Definition.HostAddress!, ex);
                }

                throw;
            }
            catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or IOException)
            {
                // The outcome is unknown: Docker may have started the container although the answer
                // was lost. A running container must still have its publication verified (or be contained).
                if (await MayBeRunningAsync(client, containerId))
                {
                    await RequirePublishedAsync(client, containerId, spec);
                }

                throw;
            }

            await RequirePublishedAsync(client, containerId, spec);
        });

    /// <summary>
    /// Bounded, independent of the caller's token. Only a confirmed "not running" skips the
    /// publication check; an unknown state goes through it (and thus through containment).
    /// </summary>
    private static async Task<bool> MayBeRunningAsync(DockerClient client, string containerId)
    {
        try
        {
            using var timeout = new CancellationTokenSource(ContainmentTimeout);
            var inspect = await client.Containers.InspectContainerAsync(containerId, timeout.Token);
            return inspect.State?.Running != false;
        }
        catch (DockerContainerNotFoundException)
        {
            return false;
        }
        catch (Exception ex) when (ex is DockerApiException or OperationCanceledException or IOException or HttpRequestException)
        {
            return true;
        }
    }

    private async Task RequireHostAddressAsync(ServerDefinition definition, CancellationToken cancellationToken)
    {
        if (definition.Exposure != PortExposure.SpecificAddress)
        {
            return;
        }

        IReadOnlyList<HostInterface> interfaces;
        try
        {
            interfaces = await Task.Run(_hostAddresses.Read, cancellationToken);
        }
        catch (HostAddressException ex)
        {
            throw new ServerOperationException(ServerOperationError.AddressCheckFailed, ex.Message, ex);
        }

        if (!HostAddressRules.IsPresent(interfaces, definition.HostAddress!))
        {
            throw new ServerOperationException(ServerOperationError.AddressNotPresent, definition.HostAddress!);
        }
    }

    /// <summary>True only when a fresh enumeration shows the specific address absent; any doubt keeps Docker's own error.</summary>
    private async Task<bool> HostAddressVanishedAsync(ServerDefinition definition)
    {
        if (definition.Exposure != PortExposure.SpecificAddress)
        {
            return false;
        }

        try
        {
            return !HostAddressRules.IsPresent(await Task.Run(_hostAddresses.Read), definition.HostAddress!);
        }
        catch (HostAddressException)
        {
            return false;
        }
    }

    /// <summary>
    /// Docker 29.8 starts a container whose earlier start failed to bind the host address with
    /// nothing published at all (reproduced here three times; the configuration still shows the
    /// binding). A server that runs unpublished — or published more widely than asked — is stopped again.
    /// </summary>
    private async Task RequirePublishedAsync(DockerClient client, string containerId, ContainerSpec spec)
    {
        // The container is running from here on: the check and the containment use their own
        // bounded time, not the caller's token — a cancelled start must still end with a verified
        // publication or a stopped container.
        string? problem;
        try
        {
            using var inspectTimeout = new CancellationTokenSource(ContainmentTimeout);
            var inspect = await client.Containers.InspectContainerAsync(containerId, inspectTimeout.Token);
            problem = HardeningVerifier.VerifyPublishedPort(spec, ContainerMapping.ToEffectivePorts(inspect));
        }
        catch (Exception ex) when (ex is DockerApiException or OperationCanceledException or IOException or HttpRequestException)
        {
            problem = $"the published ports could not be read ({ex.GetType().Name})";
        }

        if (problem is null)
        {
            return;
        }

        _logger.LogError("Container {ContainerId} started but {Problem}; stopping it", containerId, problem);
        var failures = new List<string>();
        Exception? first = null;

        // The restart policy first: it would otherwise start the unpublished container again after
        // the kill. Each step gets its own time, and the kill is attempted whatever happened before.
        try
        {
            using var updateTimeout = new CancellationTokenSource(ContainmentTimeout);
            await client.Containers.UpdateContainerAsync(
                containerId,
                new ContainerUpdateParameters
                {
                    RestartPolicy = new DockerRestartPolicyType { Name = ContainerMapping.ToDockerPolicy(Core.Servers.RestartPolicyKind.No), MaximumRetryCount = 0 },
                },
                updateTimeout.Token);
        }
        catch (Exception ex) when (ex is DockerApiException or OperationCanceledException or IOException or HttpRequestException)
        {
            _logger.LogError(ex, "Could not disable the restart policy of container {ContainerId}", containerId);
            failures.Add("disabling the restart policy failed");
            first ??= ex;
        }

        try
        {
            using var killTimeout = new CancellationTokenSource(ContainmentTimeout);
            await client.Containers.KillContainerAsync(containerId, new ContainerKillParameters(), killTimeout.Token);
        }
        catch (DockerApiException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
        {
            // Not running any more: nothing left to stop.
        }
        catch (Exception ex) when (ex is DockerApiException or OperationCanceledException or IOException or HttpRequestException)
        {
            _logger.LogError(ex, "Could not stop container {ContainerId} after an unpublished start", containerId);
            failures.Add("stopping the container failed as well");
            first ??= ex;
        }

        throw failures.Count == 0
            ? new ServerOperationException(ServerOperationError.PortNotPublished, problem)
            : new ServerOperationException(ServerOperationError.ContainmentFailed, $"{problem}; {string.Join("; ", failures)}", first);
    }

    public Task StopAsync(ServerRecord record, CancellationToken cancellationToken) =>
        Locked(record, async () =>
        {
            // Registered before the ownership check, so a force stop issued right after the stop
            // waits for the verified container id instead of failing as Busy.
            var verified = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var stop = new StopInProgress { VerifiedContainerId = verified.Task };
            _activeStops[record.Definition.Id] = stop;
            try
            {
                var raw = await _connection.GetRawClientAsync(cancellationToken);
                string containerId;
                try
                {
                    containerId = await RequireOwnedContainerAsync(record, cancellationToken);
                    verified.SetResult(containerId);
                }
                catch (Exception ex)
                {
                    verified.SetException(ex);
                    throw;
                }

                // t=-1: wait as long as the server needs; Docker.DotNet cannot express this (uint).
                using var response = await raw.PostAsync(
                    $"containers/{Uri.EscapeDataString(containerId)}/stop?t=-1", content: null, cancellationToken);
                switch (response.StatusCode)
                {
                    case HttpStatusCode.NoContent or HttpStatusCode.NotModified:
                        return;
                    case HttpStatusCode.NotFound:
                        throw new ServerOperationException(ServerOperationError.ContainerMissing, containerId);
                    default:
                        throw new ServerOperationException(ServerOperationError.Docker, $"Stop failed: HTTP {(int)response.StatusCode}");
                }
            }
            finally
            {
                // Never leave an escalation waiting on a stop that failed before verification.
                verified.TrySetCanceled();
                Task[] escalations;
                lock (stop)
                {
                    stop.Active = false;
                    escalations = [.. stop.Escalations];
                }

                // Keep the lock until escalations settle (their errors are reported to their callers).
                try
                {
                    await Task.WhenAll(escalations);
                }
                catch (Exception)
                {
                }

                _activeStops.TryRemove(record.Definition.Id, out _);
            }
        });

    public Task SetRestartPolicyAsync(ServerRecord record, Core.Servers.RestartPolicyKind policy, CancellationToken cancellationToken) =>
        Locked(record, async () =>
        {
            var client = await _connection.GetClientAsync(cancellationToken);
            var containerId = await RequireOwnedContainerAsync(record, cancellationToken);
            // Only the restart policy is sent; zero resource values mean "unchanged" to Docker.
            await client.Containers.UpdateContainerAsync(
                containerId,
                new ContainerUpdateParameters
                {
                    RestartPolicy = new DockerRestartPolicyType
                    {
                        Name = ContainerMapping.ToDockerPolicy(policy),
                        MaximumRetryCount = policy == Core.Servers.RestartPolicyKind.OnFailure ? 3 : 0,
                    },
                },
                cancellationToken);
        });

    public async Task KillAsync(ServerRecord record, CancellationToken cancellationToken)
    {
        var gate = _locks.GetOrAdd(record.Definition.Id, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, cancellationToken))
        {
            // Escalation of a stop in progress: kill the very container that stop verified.
            // Any other concurrent operation keeps the server Busy.
            if (!_activeStops.TryGetValue(record.Definition.Id, out var stop))
            {
                throw new ServerOperationException(ServerOperationError.Busy, record.Definition.Id.ToString("N"));
            }

            Task escalation;
            lock (stop)
            {
                if (!stop.Active)
                {
                    return; // the stop already finished gracefully: nothing left to force
                }

                escalation = EscalateAsync(stop.VerifiedContainerId, cancellationToken);
                stop.Escalations.Add(escalation);
            }

            await escalation;
            return;
        }

        try
        {
            await KillContainerAsync(await RequireOwnedContainerAsync(record, cancellationToken), cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task EscalateAsync(Task<string> verifiedContainerId, CancellationToken cancellationToken) =>
        await KillContainerAsync(await verifiedContainerId.WaitAsync(cancellationToken), cancellationToken);

    private async Task KillContainerAsync(string containerId, CancellationToken cancellationToken)
    {
        var client = await _connection.GetClientAsync(cancellationToken);
        try
        {
            await client.Containers.KillContainerAsync(containerId, new ContainerKillParameters(), cancellationToken);
        }
        catch (DockerApiException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
        {
            // Not running any more: nothing to kill.
        }
        catch (DockerApiException ex)
        {
            throw new ServerOperationException(ServerOperationError.Docker, $"HTTP {(int)ex.StatusCode}", ex);
        }
    }

    public Task<ServerRecord> RemoveContainerAsync(ServerRecord record, Func<ServerRecord, Task> persist, CancellationToken cancellationToken) =>
        Locked(record, async () =>
        {
            var client = await _connection.GetClientAsync(cancellationToken);

            var container = await FindContainerAsync(client, record.Definition.Id, cancellationToken);
            if (container is not null && container.State is "running" or "restarting" or "paused")
            {
                throw new ServerOperationException(ServerOperationError.StillRunning, container.ID);
            }

            record = record with { Phase = ProvisioningPhase.Removing };
            await persist(record);

            if (container is not null)
            {
                await IgnoreNotFound(() => client.Containers.RemoveContainerAsync(
                    container.ID, new ContainerRemoveParameters { Force = false, RemoveVolumes = false }, cancellationToken));
            }

            var networkId = await FindNetworkAsync(client, record.Definition.Id, cancellationToken);
            if (networkId is not null)
            {
                var network = await client.Networks.InspectNetworkAsync(networkId, cancellationToken);
                if (network.Containers is { Count: > 0 })
                {
                    throw new ServerOperationException(ServerOperationError.Conflict, $"Network {networkId} still has containers attached");
                }

                await IgnoreNotFound(() => client.Networks.DeleteNetworkAsync(networkId, cancellationToken));
            }

            // The record (and all data) stays: the server can be recreated later.
            record = record with { Phase = ProvisioningPhase.Ready, ContainerId = null, NetworkId = null };
            await persist(record);
            return record;
        });

    public async Task<IReadOnlyList<ServerContainerState>> ListAsync(CancellationToken cancellationToken)
    {
        var client = await _connection.GetClientAsync(cancellationToken);
        var containers = await client.Containers.ListContainersAsync(
            new ContainersListParameters { All = true, Filters = LabelFilter(serverId: null) }, cancellationToken);

        var states = new List<ServerContainerState>();
        foreach (var c in containers)
        {
            if (!c.Labels.TryGetValue(ServerLabels.ServerId, out var idText) || !Guid.TryParseExact(idText, "N", out var serverId))
            {
                continue;
            }

            ContainerInspectResponse inspect;
            try
            {
                inspect = await client.Containers.InspectContainerAsync(c.ID, cancellationToken);
            }
            catch (DockerContainerNotFoundException)
            {
                continue; // removed between list and inspect
            }

            var state = inspect.State;
            states.Add(new ServerContainerState(
                serverId,
                c.ID,
                state?.Status ?? c.State,
                ContainerMapping.ToHealth(state?.Health?.Status),
                state?.Running == true && DateTimeOffset.TryParse(state.StartedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var started)
                    ? started
                    : null,
                state is { Running: false } ? (int)state.ExitCode : null,
                state?.OOMKilled ?? false));
        }

        return states;
    }

    public async Task StreamLogsAsync(
        ServerRecord record,
        string? since,
        int tail,
        IReadOnlyList<string> secrets,
        Action<LogLine> onLine,
        CancellationToken cancellationToken)
    {
        var client = await _connection.GetClientAsync(cancellationToken);
        var containerId = await RequireOwnedContainerAsync(record, cancellationToken);
        var assembler = new LogLineAssembler(secrets);

        using var stream = await client.Containers.GetContainerLogsAsync(
            containerId,
            new ContainerLogsParameters
            {
                ShowStdout = true,
                ShowStderr = true,
                Timestamps = true,
                Follow = true,
                Since = since,
                Tail = since is null ? tail.ToString(CultureInfo.InvariantCulture) : "all",
            },
            cancellationToken);

        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadOutputAsync(buffer, 0, buffer.Length, cancellationToken);
            if (read.EOF)
            {
                break;
            }

            var source = read.Target == MultiplexedStream.TargetStream.StandardError ? LogSource.Stderr : LogSource.Stdout;
            foreach (var line in assembler.Feed(source, buffer.AsSpan(0, read.Count)))
            {
                onLine(line);
            }
        }

        foreach (var line in assembler.Flush())
        {
            onLine(line);
        }
    }

    public Task<RconResult> ExecRconAsync(
        ServerRecord record, string command, IReadOnlyList<string> secrets, TimeSpan timeout, CancellationToken cancellationToken) =>
        Locked(record, async () =>
        {
            if (RconCommands.Validate(command) is { } invalid)
            {
                throw new ArgumentException(invalid, nameof(command));
            }

            var client = await _connection.GetClientAsync(cancellationToken);
            var containerId = await RequireOwnedContainerAsync(record, cancellationToken);
            var state = (await client.Containers.InspectContainerAsync(containerId, cancellationToken)).State;
            if (state?.Running != true)
            {
                throw new ServerOperationException(ServerOperationError.NotRunning, containerId);
            }

            // Do not run two commands at once: an unconfirmed earlier one must have finished first.
            if (_unfinishedExecs.TryGetValue(record.Definition.Id, out var unfinished))
            {
                if (unfinished.ContainerId == containerId && await IsExecRunningAsync(client, unfinished.ExecId, cancellationToken))
                {
                    throw new ServerOperationException(ServerOperationError.CommandStillRunning, unfinished.ExecId);
                }

                _unfinishedExecs.TryRemove(record.Definition.Id, out _);
            }

            var exec = await client.Exec.CreateContainerExecAsync(
                containerId,
                new ContainerExecCreateParameters
                {
                    Cmd = [RconCliPath, "--", command],
                    User = BuildSpec(record.Definition).User, // the same non-root user as the server
                    AttachStdout = true,
                    AttachStderr = true,
                    AttachStdin = false,
                    TTY = false,
                    Privileged = false,
                    Env = [],
                },
                cancellationToken);

            // Registered before it starts; cleared only when its end is confirmed.
            _unfinishedExecs[record.Definition.Id] = (containerId, exec.ID);

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout);
            var output = new MemoryStream();
            var truncated = false;
            try
            {
                using var stream = await client.Exec.StartContainerExecAsync(
                    exec.ID, new ContainerExecStartParameters { Detach = false, TTY = false }, deadline.Token);
                var buffer = new byte[8192];
                while (true)
                {
                    var read = await stream.ReadOutputAsync(buffer, 0, buffer.Length, deadline.Token);
                    if (read.EOF)
                    {
                        break;
                    }

                    var room = RconCommands.MaxOutputBytes - (int)output.Length;
                    if (room > 0)
                    {
                        output.Write(buffer, 0, Math.Min(room, read.Count));
                    }

                    truncated |= read.Count > room;
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Timed out: the registration stays until Docker confirms the end.
                return new RconResult(RconOutcome.Unknown, Clean(output, secrets, cutOff: true), null, truncated);
            }

            var inspect = await client.Exec.InspectContainerExecAsync(exec.ID, CancellationToken.None);
            if (!inspect.Running)
            {
                _unfinishedExecs.TryRemove(new KeyValuePair<Guid, (string, string)>(record.Definition.Id, (containerId, exec.ID)));
            }

            return new RconResult(RconOutcome.Completed, Clean(output, secrets, cutOff: truncated), inspect.Running ? null : inspect.ExitCode, truncated);
        });

    private static async Task<bool> IsExecRunningAsync(DockerClient client, string execId, CancellationToken cancellationToken)
    {
        try
        {
            return (await client.Exec.InspectContainerExecAsync(execId, cancellationToken)).Running;
        }
        catch (DockerApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return false; // Docker forgot it (daemon restart, container recreated): nothing is running
        }
    }

    private static string Clean(MemoryStream output, IReadOnlyList<string> secrets, bool cutOff) =>
        RconCommands.CleanOutput(
            new System.Text.UTF8Encoding(false, false).GetString(output.GetBuffer(), 0, (int)output.Length), secrets, cutOff);

    public async Task<IReadOnlyList<PublishedPort>> ListPublishedPortsAsync(CancellationToken cancellationToken)
    {
        var client = await _connection.GetClientAsync(cancellationToken);
        // Stopped containers keep their port reservation in their configuration, so include them.
        var containers = await client.Containers.ListContainersAsync(new ContainersListParameters { All = true }, cancellationToken);
        var bindings = new List<PublishedPort>();
        foreach (var c in containers)
        {
            foreach (var p in c.Ports ?? [])
            {
                if (p.PublicPort > 0)
                {
                    bindings.Add(new PublishedPort(c.ID, new Core.Servers.PortBinding((int)p.PrivatePort, p.Type ?? "tcp", p.IP ?? "", (int)p.PublicPort)));
                }
            }

            if (c.State != "running")
            {
                var inspect = await client.Containers.InspectContainerAsync(c.ID, cancellationToken);
                bindings.AddRange(ContainerMapping.ToObserved(inspect).Ports.Select(b => new PublishedPort(c.ID, b)));
            }
        }

        return bindings;
    }

    private ContainerSpec BuildSpec(ServerDefinition definition) =>
        ContainerSpecBuilder.Build(definition, _pathsFor(definition), _uid, _gid, _installationId);

    private async Task VerifyAsync(DockerClient client, ServerRecord record, ContainerSpec spec, CancellationToken cancellationToken)
    {
        var containerId = RequireContainer(record);
        var networkId = record.NetworkId
            ?? throw new ServerOperationException(ServerOperationError.HardeningMismatch, "No network recorded");
        await VerifyNetworkAsync(client, spec, networkId, cancellationToken);

        IReadOnlyList<string> problems;
        try
        {
            problems = await CheckContainerAsync(client, spec, containerId, networkId, cancellationToken);
        }
        catch (DockerContainerNotFoundException ex)
        {
            throw new ServerOperationException(ServerOperationError.ContainerMissing, containerId, ex);
        }

        if (problems.Count > 0)
        {
            throw new ServerOperationException(ServerOperationError.HardeningMismatch, string.Join("; ", problems));
        }
    }

    private static async Task<IReadOnlyList<string>> CheckContainerAsync(
        DockerClient client, ContainerSpec spec, string containerId, string networkId, CancellationToken cancellationToken)
    {
        var inspect = await client.Containers.InspectContainerAsync(containerId, cancellationToken);
        // Defaults come from the pinned image itself, so an overridden entrypoint/env is detectable.
        var image = await client.Images.InspectImageAsync(spec.Image, cancellationToken);
        return HardeningVerifier.Verify(spec, ContainerMapping.ToImageDefaults(image), ContainerMapping.ToObserved(inspect), networkId);
    }

    private static async Task VerifyNetworkAsync(DockerClient client, ContainerSpec spec, string networkId, CancellationToken cancellationToken)
    {
        NetworkResponse network;
        try
        {
            network = await client.Networks.InspectNetworkAsync(networkId, cancellationToken);
        }
        catch (DockerApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            throw new ServerOperationException(ServerOperationError.HardeningMismatch, $"Network {networkId} is missing", ex);
        }

        var problems = HardeningVerifier.VerifyNetwork(spec, ContainerMapping.ToObservedNetwork(network)).ToList();

        // The container joins its network by name at start: the name must resolve to this network only.
        var sameName = await client.Networks.ListNetworksAsync(
            new NetworksListParameters
            {
                Filters = new Dictionary<string, IDictionary<string, bool>>
                {
                    ["name"] = new Dictionary<string, bool> { [$"^{spec.NetworkName}$"] = true },
                },
            },
            cancellationToken);
        if (sameName.Count != 1 || sameName[0].ID != networkId)
        {
            problems.Add("network name does not resolve to the recorded network");
        }

        if (problems.Count > 0)
        {
            throw new ServerOperationException(ServerOperationError.HardeningMismatch, string.Join("; ", problems));
        }
    }

    /// <summary>
    /// Label-only ownership check: a damaged record must never let us stop or kill a container of
    /// another installation. (Hardening is not checked, so an unsafe server can still be stopped.)
    /// </summary>
    private async Task<string> RequireOwnedContainerAsync(ServerRecord record, CancellationToken cancellationToken)
    {
        var client = await _connection.GetClientAsync(cancellationToken);
        var containerId = RequireContainer(record);
        ContainerInspectResponse inspect;
        try
        {
            inspect = await client.Containers.InspectContainerAsync(containerId, cancellationToken);
        }
        catch (DockerContainerNotFoundException ex)
        {
            throw new ServerOperationException(ServerOperationError.ContainerMissing, containerId, ex);
        }

        var labels = new Dictionary<string, string>(inspect.Config?.Labels ?? new Dictionary<string, string>());
        if (labels.GetValueOrDefault(ServerLabels.Installation) != _installationId.ToString("N")
            || labels.GetValueOrDefault(ServerLabels.ServerId) != record.Definition.Id.ToString("N"))
        {
            throw new ServerOperationException(ServerOperationError.Conflict, $"Container {containerId} does not belong to this server");
        }

        return containerId;
    }

    private static async Task<string> CreateContainerAsync(DockerClient client, ContainerSpec spec, CancellationToken cancellationToken)
    {
        try
        {
            var response = await client.Containers.CreateContainerAsync(ContainerMapping.ToCreateParameters(spec), cancellationToken);
            return response.ID;
        }
        catch (DockerApiException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
        {
            // A container with this name exists but does not carry our labels — not ours to touch.
            throw new ServerOperationException(ServerOperationError.Conflict, $"Container name {spec.Name} is taken", ex);
        }
    }

    private async Task<ContainerListResponse?> FindContainerAsync(DockerClient client, Guid serverId, CancellationToken cancellationToken)
    {
        var matches = await client.Containers.ListContainersAsync(
            new ContainersListParameters { All = true, Filters = LabelFilter(serverId) }, cancellationToken);
        return matches.Count switch
        {
            0 => null,
            1 => matches[0],
            _ => throw new ServerOperationException(ServerOperationError.Conflict, $"{matches.Count} containers claim server {serverId:N}"),
        };
    }

    private async Task<string?> FindNetworkAsync(DockerClient client, Guid serverId, CancellationToken cancellationToken)
    {
        var matches = await client.Networks.ListNetworksAsync(
            new NetworksListParameters { Filters = LabelFilter(serverId) }, cancellationToken);
        return matches.Count switch
        {
            0 => null,
            1 => matches[0].ID,
            _ => throw new ServerOperationException(ServerOperationError.Conflict, $"{matches.Count} networks claim server {serverId:N}"),
        };
    }

    private Dictionary<string, IDictionary<string, bool>> LabelFilter(Guid? serverId)
    {
        var labels = new Dictionary<string, bool>
        {
            [$"{ServerLabels.App}={ServerLabels.AppValue}"] = true,
            [$"{ServerLabels.Installation}={_installationId:N}"] = true,
        };
        if (serverId is { } id)
        {
            labels[$"{ServerLabels.ServerId}={id:N}"] = true;
        }

        return new Dictionary<string, IDictionary<string, bool>> { ["label"] = labels };
    }

    private static string RequireContainer(ServerRecord record) =>
        record.ContainerId ?? throw new ServerOperationException(ServerOperationError.ContainerMissing, "No container recorded");

    private static bool IsPortConflict(DockerApiException ex) =>
        ex.ResponseBody?.Contains("port is already allocated", StringComparison.OrdinalIgnoreCase) == true
        || ex.ResponseBody?.Contains("address already in use", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>Docker 29.8: "failed to bind host port 10.0.0.1:25565/tcp: cannot assign requested address".</summary>
    private static bool IsBindFailure(DockerApiException ex) =>
        ex.ResponseBody?.Contains("cannot assign requested address", StringComparison.OrdinalIgnoreCase) == true
        || ex.ResponseBody?.Contains("failed to bind host port", StringComparison.OrdinalIgnoreCase) == true;

    private static async Task IgnoreNotFound(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (DockerApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
        }
    }

    /// <summary>One mutation per server at a time; a second concurrent request fails fast as Busy.</summary>
    private async Task<T> Locked<T>(ServerRecord record, Func<Task<T>> action)
    {
        var gate = _locks.GetOrAdd(record.Definition.Id, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0))
        {
            throw new ServerOperationException(ServerOperationError.Busy, record.Definition.Id.ToString("N"));
        }

        try
        {
            return await action();
        }
        catch (DockerApiException ex)
        {
            throw new ServerOperationException(ServerOperationError.Docker, $"HTTP {(int)ex.StatusCode}", ex);
        }
        finally
        {
            gate.Release();
        }
    }

    private Task Locked(ServerRecord record, Func<Task> action) =>
        Locked(record, async () =>
        {
            await action();
            return true;
        });
}
