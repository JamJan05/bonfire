using System.Collections.Concurrent;
using Bonfire.Core.Rcon;
using Bonfire.Core.Servers;
using Bonfire.Infrastructure.Servers;
using Microsoft.Extensions.Logging;

namespace Bonfire.App.Services;

public enum SettingsApplyOutcome
{
    NoChange,

    /// <summary>The server was stopped: the new container is ready and settings apply on next start.</summary>
    SavedForNextStart,

    AppliedAndRestarted,

    /// <summary>Settings were applied, but starting the server again failed.</summary>
    AppliedStartFailed,
}

public sealed record SettingsApplyResult(SettingsApplyOutcome Outcome, ServerOperationException? StartError);

/// <summary>Everything the settings editor may change: game settings and the network binding.</summary>
public sealed record ServerSettingsPatch(GameSettings Game, int Port, PortExposure Exposure, string? HostAddress, RestartPolicyKind RestartPolicy)
{
    public static ServerSettingsPatch From(ServerDefinition definition, GameSettings game) =>
        new(game, definition.Port, definition.Exposure, definition.HostAddress, definition.RestartPolicy);

    public ServerDefinition ApplyTo(ServerDefinition definition) => definition with
    {
        Game = Game,
        Port = Port,
        Exposure = Exposure,
        HostAddress = HostAddress,
        RestartPolicy = RestartPolicy,
    };
}

public sealed class SettingsValidationException(IReadOnlyList<string> errors)
    : Exception($"Invalid settings: {string.Join(", ", errors)}")
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

/// <summary>
/// The single entry point for changing servers. Always works on a record freshly read from disk,
/// serializes operations per server and applies the Docker capability policy: anything that
/// creates or starts a container requires a fully verified daemon, while stopping, killing and
/// removing stay possible so an affected server can always be contained.
/// </summary>
public sealed class ServerOperationsService
{
    private readonly ServerRegistry _registry;
    private readonly IServerRuntime _runtime;
    private readonly IDockerStatusSource _docker;
    private readonly ILogger<ServerOperationsService> _logger;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _gates = new();
    private readonly ConcurrentDictionary<Guid, bool> _forced = new();

    /// <summary>Makes "check force stop, then cross the removal boundary" atomic with force stop itself.</summary>
    private readonly Lock _forceLock = new();

    public ServerOperationsService(
        ServerRegistry registry, IServerRuntime runtime, IDockerStatusSource docker, ILogger<ServerOperationsService> logger,
        RestoreEngine? restore = null,
        ContentEngine? content = null,
        IHostAddresses? hostAddresses = null,
        IPortAvailability? ports = null)
    {
        _content = content;
        _registry = registry;
        _runtime = runtime;
        _docker = docker;
        _logger = logger;
        _restore = restore;
        _hostAddresses = hostAddresses;
        _ports = ports;
    }

    private readonly RestoreEngine? _restore;
    private readonly ContentEngine? _content;
    private readonly IHostAddresses? _hostAddresses;
    private readonly IPortAvailability? _ports;

    /// <summary>Runs an operation exclusively for the server (shared gate with start/stop/settings/RCON).</summary>
    public Task<T> RunExclusiveAsync<T>(Guid serverId, Func<Task<T>> action) => SerializedResult(serverId, action);

    public Task StartAsync(Guid serverId, CancellationToken cancellationToken) =>
        Serialized(serverId, async () =>
        {
            RequireSafeDaemon();
            var record = LoadStartableRecord(serverId);
            await RequireHostAddressAsync(record.Definition, cancellationToken);
            await _runtime.StartAsync(record, cancellationToken);
        });

    /// <summary>
    /// A server published on a specific host address is started only while that address is
    /// assigned to an interface (VPN up) — never silently on another address. The runtime checks
    /// again immediately before the Docker start; this check gives the user the answer before
    /// anything is touched, and keeps the rule when a runtime cannot enumerate interfaces.
    /// </summary>
    private async Task RequireHostAddressAsync(ServerDefinition definition, CancellationToken cancellationToken)
    {
        if (definition.Exposure != PortExposure.SpecificAddress)
        {
            return;
        }

        if (_hostAddresses is null)
        {
            throw new ServerOperationException(ServerOperationError.AddressCheckFailed, "No interface enumeration is available");
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

    public Task StopAsync(Guid serverId, CancellationToken cancellationToken) =>
        Serialized(serverId, () =>
        {
            RequireConnected();
            return _runtime.StopAsync(LoadRecord(serverId), cancellationToken);
        });

    /// <summary>Stop, then start again only after the stop completed without a force stop.</summary>
    public Task RestartAsync(Guid serverId, CancellationToken cancellationToken) =>
        Serialized(serverId, async () =>
        {
            RequireSafeDaemon();
            _forced[serverId] = false; // stays registered until the start decision is made
            try
            {
                await _runtime.StopAsync(LoadRecord(serverId), cancellationToken);
                ThrowIfForced(serverId);

                var state = (await _runtime.ListAsync(cancellationToken)).SingleOrDefault(s => s.ServerId == serverId);
                if (state is null || state.IsRunning)
                {
                    throw new ServerOperationException(ServerOperationError.Docker, "Server did not reach the stopped state");
                }

                RequireSafeDaemon(); // the daemon may have changed while the server was stopping
                var record = LoadStartableRecord(serverId);
                await RequireHostAddressAsync(record.Definition, cancellationToken);
                ThrowIfForced(serverId); // checked immediately before starting (after the asynchronous address check)
                await _runtime.StartAsync(record, cancellationToken);
            }
            finally
            {
                _forced.TryRemove(serverId, out _);
            }
        });

    private void ThrowIfForced(Guid serverId)
    {
        if (_forced.TryGetValue(serverId, out var forced) && forced)
        {
            throw new ServerOperationException(ServerOperationError.RestartAborted, serverId.ToString("N"));
        }
    }

    /// <summary>Kills the server; deliberately not serialized, because it escalates a pending stop.</summary>
    public async Task ForceStopAsync(Guid serverId, CancellationToken cancellationToken)
    {
        RequireConnected();
        lock (_forceLock)
        {
            _forced.TryUpdate(serverId, true, false); // only marks a restart or settings change in progress
        }

        _logger.LogWarning("Force-stopping server {ServerId}", serverId);
        await _runtime.KillAsync(LoadRecord(serverId), cancellationToken);
    }

    public Task RemoveContainerAsync(Guid serverId, CancellationToken cancellationToken) =>
        Serialized(serverId, async () =>
        {
            RequireConnected();
            await _runtime.RemoveContainerAsync(LoadRecord(serverId), r => _registry.SaveAsync(r, cancellationToken), cancellationToken);
        });

    /// <summary>Resumes an interrupted operation according to its persisted intent, or recreates the container.</summary>
    public Task RepairAsync(Guid serverId, CancellationToken cancellationToken) =>
        Serialized(serverId, async () =>
        {
            var record = LoadRecord(serverId);
            if (record.PendingRestore is not null)
            {
                // Finish or cancel the interrupted restore; the server is left stopped.
                RequireConnected();
                await (_restore ?? throw new InvalidOperationException("Restore recovery is not available")).RecoverAsync(record);
                return;
            }

            if (record.PendingContent is not null)
            {
                // Roll back (or finish, once committed) the interrupted add-on change; the server is left stopped.
                RequireConnected();
                await (_content ?? throw new InvalidOperationException("Add-on recovery is not available")).RecoverAsync(record);
                return;
            }

            if (record.PendingChange is not null)
            {
                // Finish the interrupted settings change; the server is left stopped.
                RequireSafeDaemon();
                await CompleteSettingsChangeAsync(record, interactive: false, cancellationToken);
                return;
            }

            if (record.Phase == ProvisioningPhase.Removing)
            {
                RequireConnected();
                await _runtime.RemoveContainerAsync(record, r => _registry.SaveAsync(r, cancellationToken), cancellationToken);
                return;
            }

            RequireSafeDaemon();
            await _runtime.ProvisionAsync(record, r => _registry.SaveAsync(r, cancellationToken), cancellationToken);
        });

    public static readonly TimeSpan RconTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Sends a console command. The exact command "stop" goes through Bonfire's graceful stop
    /// instead (so the UI tracks it and force stop works); everything else goes to rcon-cli.
    /// Commands are never logged — they can contain secrets.
    /// </summary>
    public async Task<RconResult?> SendCommandAsync(Guid serverId, string command, CancellationToken cancellationToken)
    {
        if (RconCommands.IsStopCommand(command))
        {
            await StopAsync(serverId, cancellationToken);
            return null;
        }

        return await QueryRconAsync(serverId, command, cancellationToken);
    }

    /// <summary>Player list via "list"; fails as Busy while a lifecycle operation runs (no polling then).</summary>
    public async Task<PlayerList> QueryPlayersAsync(Guid serverId, CancellationToken cancellationToken)
    {
        var result = await QueryRconAsync(serverId, "list", cancellationToken);
        return result.Outcome == RconOutcome.Completed ? RconCommands.ParseList(result.Output) : PlayerList.Unknown;
    }

    private Task<RconResult> QueryRconAsync(Guid serverId, string command, CancellationToken cancellationToken) =>
        SerializedResult(serverId, () =>
        {
            RequireConnected();
            var secrets = _registry.ReadRconSecret(serverId) is { } secret ? new[] { secret } : [];
            return _runtime.ExecRconAsync(LoadRecord(serverId), command, secrets, RconTimeout, cancellationToken);
        });

    private async Task<T> SerializedResult<T>(Guid serverId, Func<Task<T>> action)
    {
        T result = default!;
        await Serialized(serverId, async () => result = await action());
        return result;
    }

    /// <summary>
    /// Applies new game settings by recreating the container (data, image digest, versions and the
    /// RCON secret stay). Each step is persisted before it happens; a force stop during the change
    /// aborts it without changes. A running server is started again afterwards as a separate step.
    /// </summary>
    public Task<SettingsApplyResult> ApplySettingsAsync(Guid serverId, GameSettings settings, CancellationToken cancellationToken) =>
        SerializedResult(serverId, () => ApplyPatchAsync(serverId, record => ServerSettingsPatch.From(record.Definition, settings), cancellationToken));

    /// <summary>Game settings and the network binding together; a changed binding is checked for conflicts first.</summary>
    public Task<SettingsApplyResult> ApplySettingsAsync(Guid serverId, ServerSettingsPatch patch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(patch);
        return SerializedResult(serverId, () => ApplyPatchAsync(serverId, _ => patch, cancellationToken));
    }

    private async Task<SettingsApplyResult> ApplyPatchAsync(Guid serverId, Func<ServerRecord, ServerSettingsPatch> patchFor, CancellationToken cancellationToken)
    {
        {
            var record = LoadRecord(serverId);
            if (record.HasPendingOperation)
            {
                throw new ServerOperationException(ServerOperationError.SettingsChangePending, serverId.ToString("N"));
            }

            RequireSafeDaemon();
            var newDefinition = patchFor(record).ApplyTo(record.Definition);
            var errors = ServerDefinitionValidator.Validate(newDefinition);
            if (errors.Count > 0)
            {
                throw new SettingsValidationException(errors);
            }

            if (newDefinition == record.Definition)
            {
                return new SettingsApplyResult(SettingsApplyOutcome.NoChange, null);
            }

            await RequireBindingFreeAsync(record, newDefinition, cancellationToken);

            var state = record.ContainerId is null
                ? null
                : (await _runtime.ListAsync(cancellationToken)).SingleOrDefault(s => s.ContainerId == record.ContainerId);
            var wasRunning = state?.IsRunning == true;

            record = record with { PendingChange = new PendingSettingsChange(record.Definition, newDefinition, wasRunning, SettingsChangeStep.Prepared) };
            await _registry.SaveAsync(record, CancellationToken.None);

            _forced[serverId] = false;
            bool forcedAfterBoundary;
            try
            {
                await CompleteSettingsChangeAsync(record, interactive: true, cancellationToken);
            }
            finally
            {
                lock (_forceLock)
                {
                    forcedAfterBoundary = _forced.TryRemove(serverId, out var f) && f;
                }
            }

            if (!wasRunning || forcedAfterBoundary)
            {
                // A force stop after the old container was removed: settings are applied, but the
                // user wanted the server down, so it is not started again.
                return new SettingsApplyResult(SettingsApplyOutcome.SavedForNextStart, null);
            }

            try
            {
                RequireSafeDaemon();
                var startable = LoadStartableRecord(serverId);
                await RequireHostAddressAsync(startable.Definition, cancellationToken);
                await _runtime.StartAsync(startable, cancellationToken);
                return new SettingsApplyResult(SettingsApplyOutcome.AppliedAndRestarted, null);
            }
            catch (ServerOperationException ex)
            {
                return new SettingsApplyResult(SettingsApplyOutcome.AppliedStartFailed, ex);
            }
        }
    }

    /// <summary>
    /// A changed binding must not collide with another Bonfire server, another container or a host
    /// program. The server's own record and container are ignored; the host probe is skipped when
    /// the new binding overlaps the current one (the old container's listener is not a conflict —
    /// a host program that took the port is still caught by Docker when the server starts).
    /// </summary>
    private async Task RequireBindingFreeAsync(ServerRecord record, ServerDefinition newDefinition, CancellationToken cancellationToken)
    {
        var current = record.Definition;
        var newHostIp = PortRules.HostIp(newDefinition);
        if (_ports is null || (newDefinition.Port == current.Port && newHostIp == PortRules.HostIp(current)))
        {
            return;
        }

        var scope = new PortCheckScope(
            IgnoreServerId: current.Id,
            IgnoreContainerId: record.ContainerId,
            ProbeHost: !PortRules.Overlaps(newHostIp, newDefinition.Port, PortRules.HostIp(current), current.Port));
        var check = await _ports.CheckAsync(newHostIp, newDefinition.Port, scope, cancellationToken);
        if (check is PortCheck.UsedByBonfireServer or PortCheck.UsedByContainer or PortCheck.UsedByHost)
        {
            throw new ServerOperationException(ServerOperationError.PortInUse, check.ToString());
        }
    }

    /// <summary>Runs the remaining steps of a persisted settings change. Never starts the server.</summary>
    private async Task<ServerRecord> CompleteSettingsChangeAsync(ServerRecord record, bool interactive, CancellationToken cancellationToken)
    {
        var change = record.PendingChange!;
        var serverId = record.Definition.Id;

        async Task<ServerRecord> Step(ServerRecord r, SettingsChangeStep step)
        {
            r = r with { PendingChange = r.PendingChange! with { Step = step } };
            await _registry.SaveAsync(r, CancellationToken.None);
            return r;
        }

        Task Persist(ServerRecord r) => _registry.SaveAsync(r, CancellationToken.None);

        if (record.ContainerId is not null && change.Step < SettingsChangeStep.OldRemoved)
        {
            if (change.Step < SettingsChangeStep.PolicyDisabled)
            {
                // No automatic restart may start the old container while it is being replaced.
                await IgnoreMissing(() => _runtime.SetRestartPolicyAsync(record, RestartPolicyKind.No, cancellationToken));
                record = await Step(record, SettingsChangeStep.PolicyDisabled);
            }

            var running = (await _runtime.ListAsync(cancellationToken)).Any(s => s.ContainerId == record.ContainerId && s.IsRunning);
            if (running)
            {
                await _runtime.StopAsync(record, cancellationToken);
            }

            // A force stop at any point before removal (even one that finished before we looked)
            // aborts the change: the old container is still there, so undo the policy and drop the intent.
            // Checked under the same lock force stop uses: a later force stop is past the boundary and
            // only prevents the restart at the end.
            bool forced;
            lock (_forceLock)
            {
                forced = interactive && _forced.TryGetValue(serverId, out var f) && f;
            }

            if (forced)
            {
                await IgnoreMissing(() => _runtime.SetRestartPolicyAsync(record, change.OldDefinition.RestartPolicy, cancellationToken));
                await _registry.SaveAsync(record with { PendingChange = null }, CancellationToken.None);
                throw new ServerOperationException(ServerOperationError.SettingsAborted, serverId.ToString("N"));
            }

            record = await Step(record, SettingsChangeStep.Stopped);
            record = await _runtime.RemoveContainerAsync(record, Persist, cancellationToken);
            record = await Step(record, SettingsChangeStep.OldRemoved);
        }

        // The replacement is created with restart policy "no" and only gets the intended policy
        // once it exists — so it cannot start on its own while the change is incomplete.
        var withoutRestart = record with { Definition = change.NewDefinition with { RestartPolicy = RestartPolicyKind.No } };
        record = await _runtime.ProvisionAsync(withoutRestart, Persist, cancellationToken);
        record = await Step(record, SettingsChangeStep.ReplacementCreated);

        if ((await _runtime.ListAsync(cancellationToken)).Any(s => s.ContainerId == record.ContainerId && s.IsRunning))
        {
            await _runtime.StopAsync(record, cancellationToken); // never leave a half-applied change running
        }

        await _runtime.SetRestartPolicyAsync(record, change.NewDefinition.RestartPolicy, cancellationToken);
        record = record with { Definition = change.NewDefinition, PendingChange = null };
        await _registry.SaveAsync(record, CancellationToken.None);
        return record;
    }

    private static async Task IgnoreMissing(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (ServerOperationException ex) when (ex.Error == ServerOperationError.ContainerMissing)
        {
        }
    }

    private ServerRecord LoadStartableRecord(Guid serverId)
    {
        var record = LoadRecord(serverId);
        return !record.HasPendingOperation
            ? record
            : throw new ServerOperationException(ServerOperationError.SettingsChangePending, serverId.ToString("N"));
    }

    private ServerRecord LoadRecord(Guid serverId) =>
        _registry.Load(serverId)?.Record
        ?? throw new ServerOperationException(ServerOperationError.RecordUnavailable, serverId.ToString("N"));

    public void RequireConnected()
    {
        if (_docker.Current is not { IsConnected: true })
        {
            throw new ServerOperationException(ServerOperationError.DockerUnsupported, "Docker is not connected");
        }
    }

    /// <summary>Fail closed: unknown capabilities block creating and starting just like missing ones.</summary>
    public void RequireSafeDaemon()
    {
        RequireConnected();
        var current = _docker.Current!;
        if (!_docker.CapabilitiesTrusted || current.Capabilities is not { Seccomp: true, Rootless: false })
        {
            throw new ServerOperationException(ServerOperationError.DockerUnsupported, "Daemon security features are missing or unknown");
        }
    }

    private async Task Serialized(Guid serverId, Func<Task> action)
    {
        var gate = _gates.GetOrAdd(serverId, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0))
        {
            throw new ServerOperationException(ServerOperationError.Busy, serverId.ToString("N"));
        }

        try
        {
            await action();
        }
        finally
        {
            gate.Release();
        }
    }
}
