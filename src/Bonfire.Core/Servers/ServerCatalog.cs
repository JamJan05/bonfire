namespace Bonfire.Core.Servers;

/// <summary>One server directory as loaded from disk; <see cref="Record"/> is null when it is damaged.</summary>
public sealed record RegistryEntry(Guid ServerId, ServerPaths Paths, ServerRecord? Record, string? Error);

public enum ServerStatus
{
    /// <summary>Docker state is not known (daemon unreachable or not polled yet).</summary>
    Unknown,
    Running,
    Starting,
    Unhealthy,
    Restarting,
    Paused,

    /// <summary>Docker is removing the container.</summary>
    Removing,
    Stopped,

    /// <summary>Created but never started.</summary>
    Created,
    Dead,

    /// <summary>Container removed on purpose; data kept, can be recreated.</summary>
    ContainerRemoved,

    /// <summary>The recorded container no longer exists.</summary>
    ContainerMissing,

    /// <summary>A container for this server exists but the record does not reference it (interrupted save).</summary>
    UnrecordedContainer,

    /// <summary>Creation or removal was interrupted and must be resumed.</summary>
    Incomplete,

    /// <summary>server.json could not be read; shown read-only.</summary>
    DamagedRecord,

    /// <summary>More than one container claims this server.</summary>
    Conflict,

    /// <summary>A container of this installation without any server record.</summary>
    Orphan,
}

public enum ServerAction
{
    Start,
    Stop,
    Restart,
    ForceStop,
    RemoveContainer,

    /// <summary>Recreate, adopt an unrecorded container, or resume an interrupted operation.</summary>
    Repair,
}

public sealed record ServerSnapshot(
    Guid ServerId,
    ServerRecord? Record,
    string? RecordError,
    ServerStatus Status,
    ServerContainerState? Container)
{
    public bool IsActive => Status is ServerStatus.Running or ServerStatus.Starting or ServerStatus.Unhealthy
        or ServerStatus.Restarting or ServerStatus.Paused;
}

/// <summary>Pure merge of registry contents and Docker state into what the UI shows.</summary>
public static class ServerCatalog
{
    /// <param name="containers">Docker state, or null when it could not be read.</param>
    public static IReadOnlyList<ServerSnapshot> Build(IReadOnlyList<RegistryEntry> registry, IReadOnlyList<ServerContainerState>? containers)
    {
        ArgumentNullException.ThrowIfNull(registry);
        var byServer = (containers ?? []).ToLookup(c => c.ServerId);
        var snapshots = new List<ServerSnapshot>();

        foreach (var entry in registry)
        {
            var matches = byServer[entry.ServerId].ToList();
            snapshots.Add(new ServerSnapshot(
                entry.ServerId,
                entry.Record,
                entry.Error,
                StatusFor(entry, matches, containers is not null),
                matches.Count == 1 ? matches[0] : null));
        }

        // A damaged record still reserves its server id, so its container is not an orphan.
        var known = registry.Select(e => e.ServerId).ToHashSet();
        foreach (var group in byServer.Where(g => !known.Contains(g.Key)))
        {
            var list = group.ToList();
            snapshots.Add(new ServerSnapshot(
                group.Key, null, null, list.Count > 1 ? ServerStatus.Conflict : ServerStatus.Orphan, list.Count == 1 ? list[0] : null));
        }

        return snapshots;
    }

    private static ServerStatus StatusFor(RegistryEntry entry, List<ServerContainerState> matches, bool dockerKnown)
    {
        if (entry.Record is null)
        {
            return ServerStatus.DamagedRecord;
        }

        if (entry.Record.HasPendingOperation
            || entry.Record.Phase is ProvisioningPhase.Provisioning or ProvisioningPhase.Removing)
        {
            return ServerStatus.Incomplete;
        }

        if (!dockerKnown)
        {
            return ServerStatus.Unknown;
        }

        if (matches.Count > 1)
        {
            return ServerStatus.Conflict;
        }

        if (matches.Count == 0)
        {
            return entry.Record.ContainerId is null ? ServerStatus.ContainerRemoved : ServerStatus.ContainerMissing;
        }

        var container = matches[0];
        if (container.ContainerId != entry.Record.ContainerId)
        {
            return ServerStatus.UnrecordedContainer;
        }

        return container.State switch
        {
            "running" => container.Health switch
            {
                ContainerHealth.Starting => ServerStatus.Starting,
                ContainerHealth.Unhealthy => ServerStatus.Unhealthy,
                _ => ServerStatus.Running,
            },
            "restarting" => ServerStatus.Restarting,
            "paused" => ServerStatus.Paused,
            "removing" => ServerStatus.Removing,
            "exited" => ServerStatus.Stopped,
            "created" => ServerStatus.Created,
            "dead" => ServerStatus.Dead,
            _ => ServerStatus.Unknown,
        };
    }

    /// <summary>Actions that make sense in a status (capability policy is applied separately).</summary>
    public static IReadOnlySet<ServerAction> AllowedActions(ServerStatus status) => status switch
    {
        ServerStatus.Running or ServerStatus.Starting or ServerStatus.Unhealthy =>
            new HashSet<ServerAction> { ServerAction.Stop, ServerAction.Restart, ServerAction.ForceStop },
        ServerStatus.Restarting or ServerStatus.Paused =>
            new HashSet<ServerAction> { ServerAction.Stop, ServerAction.ForceStop },
        // Repair on a stopped container recreates it if its configuration no longer matches.
        ServerStatus.Stopped or ServerStatus.Created =>
            new HashSet<ServerAction> { ServerAction.Start, ServerAction.RemoveContainer, ServerAction.Repair },
        ServerStatus.Dead => new HashSet<ServerAction> { ServerAction.RemoveContainer, ServerAction.Repair },
        ServerStatus.ContainerRemoved or ServerStatus.ContainerMissing or ServerStatus.UnrecordedContainer or ServerStatus.Incomplete =>
            new HashSet<ServerAction> { ServerAction.Repair },
        _ => new HashSet<ServerAction>(),
    };
}
