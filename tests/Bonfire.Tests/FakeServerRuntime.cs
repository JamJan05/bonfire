using Bonfire.Core.Servers;

namespace Bonfire.Tests;

/// <summary>In-memory IServerRuntime; behaviour of each call can be replaced per test.</summary>
internal sealed class FakeServerRuntime : IServerRuntime
{
    public List<string> Calls { get; } = [];

    public List<ServerContainerState> Containers { get; } = [];

    public Func<ServerRecord, CancellationToken, Task> Stop { get; set; } = (_, _) => Task.CompletedTask;

    public Func<CancellationToken, Task<IReadOnlyList<ServerContainerState>>>? List { get; set; }

    public Task<ResolvedImage> AcquireImageAsync(string tag, IProgress<ImagePullProgress>? progress, CancellationToken cancellationToken)
    {
        Calls.Add($"pull:{tag}");
        return Acquire?.Invoke(tag, cancellationToken) ?? Task.FromResult(Image with { Reference = Image.Reference with { Tag = tag } });
    }

    /// <summary>Container id given to provisioned containers.</summary>
    public string ProvisionedContainerId { get; set; } = "c1";

    public List<ServerDefinition> Provisioned { get; } = [];

    public async Task<ServerRecord> ProvisionAsync(ServerRecord record, Func<ServerRecord, Task> persist, CancellationToken cancellationToken)
    {
        Calls.Add("provision");
        Provisioned.Add(record.Definition);
        record = record with { Phase = ProvisioningPhase.Ready, ContainerId = ProvisionedContainerId, NetworkId = "n1" };
        await persist(record);
        return record;
    }

    public Task StartAsync(ServerRecord record, CancellationToken cancellationToken)
    {
        Calls.Add("start");
        return StartBehavior?.Invoke(record) ?? Task.CompletedTask;
    }

    public Task StopAsync(ServerRecord record, CancellationToken cancellationToken)
    {
        Calls.Add("stop");
        return Stop(record, cancellationToken);
    }

    public Func<ServerRecord, RestartPolicyKind, Task>? PolicyBehavior { get; set; }

    public Task SetRestartPolicyAsync(ServerRecord record, RestartPolicyKind policy, CancellationToken cancellationToken)
    {
        Calls.Add($"policy:{policy}");
        return PolicyBehavior?.Invoke(record, policy) ?? Task.CompletedTask;
    }

    public Task KillAsync(ServerRecord record, CancellationToken cancellationToken)
    {
        Calls.Add("kill");
        return Task.CompletedTask;
    }

    public async Task<ServerRecord> RemoveContainerAsync(ServerRecord record, Func<ServerRecord, Task> persist, CancellationToken cancellationToken)
    {
        Calls.Add("remove");
        if (RemoveBehavior is not null)
        {
            await RemoveBehavior();
        }

        Containers.RemoveAll(c => c.ContainerId == record.ContainerId);
        record = record with { Phase = ProvisioningPhase.Ready, ContainerId = null, NetworkId = null };
        await persist(record);
        return record;
    }

    public Task<IReadOnlyList<ServerContainerState>> ListAsync(CancellationToken cancellationToken) =>
        List?.Invoke(cancellationToken) ?? Task.FromResult<IReadOnlyList<ServerContainerState>>(Containers.ToList());

    public List<PublishedPort> PublishedPorts { get; } = [];

    public Func<Task>? RemoveBehavior { get; set; }

    /// <summary>Called for each log stream; feed lines through the callback, return to end the stream.</summary>
    public Func<string?, Action<Bonfire.Core.Logs.LogLine>, CancellationToken, Task> Logs { get; set; } =
        (_, _, ct) => Task.Delay(Timeout.Infinite, ct);

    public List<string?> LogSinces { get; } = [];

    public Func<string, CancellationToken, Task<Bonfire.Core.Rcon.RconResult>> Rcon { get; set; } =
        (_, _) => Task.FromResult(new Bonfire.Core.Rcon.RconResult(Bonfire.Core.Rcon.RconOutcome.Completed, "There are 0 of a max of 10 players online: ", 0, false));

    public List<string> RconCommands { get; } = [];

    public Task<Bonfire.Core.Rcon.RconResult> ExecRconAsync(
        ServerRecord record, string command, IReadOnlyList<string> secrets, TimeSpan timeout, CancellationToken cancellationToken)
    {
        lock (RconCommands)
        {
            RconCommands.Add(command);
        }

        return Rcon(command, cancellationToken);
    }

    public Task StreamLogsAsync(
        ServerRecord record,
        string? since,
        int tail,
        IReadOnlyList<string> secrets,
        Action<Bonfire.Core.Logs.LogLine> onLine,
        CancellationToken cancellationToken)
    {
        lock (LogSinces)
        {
            LogSinces.Add(since);
        }

        return Logs(since, onLine, cancellationToken);
    }

    public bool FailPortListing { get; set; }

    public Task<IReadOnlyList<PublishedPort>> ListPublishedPortsAsync(CancellationToken cancellationToken) =>
        FailPortListing
            ? throw new HttpRequestException("docker down")
            : Task.FromResult<IReadOnlyList<PublishedPort>>(PublishedPorts.ToList());

    public ResolvedImage Image { get; set; } = new(
        new ImageReference(ImageReference.ItzgRepository, "java25", ServerTestData.Digest), "sha256:img");

    public Func<string, CancellationToken, Task<ResolvedImage>>? Acquire { get; set; }

    public Func<ServerRecord, Task>? StartBehavior { get; set; }

    public static ServerContainerState State(Guid id, string state, ContainerHealth health = ContainerHealth.None, string containerId = "c1") =>
        new(id, containerId, state, health, state == "running" ? DateTimeOffset.UtcNow.AddMinutes(-5) : null, state == "exited" ? 0 : null, false);
}
