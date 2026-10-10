using Bonfire.Core.Servers;

namespace Bonfire.Tests;

public sealed class ServerCatalogTests
{
    private static RegistryEntry Entry(ServerRecord record) =>
        new(record.Definition.Id, new ServerPaths("/x"), record, null);

    private static ServerRecord Ready(string? containerId = "c1") =>
        ServerRecord.New(ServerTestData.Vanilla()) with { Phase = ProvisioningPhase.Ready, ContainerId = containerId, NetworkId = "n1" };

    private static ServerStatus StatusOf(ServerRecord record, params ServerContainerState[] containers) =>
        ServerCatalog.Build([Entry(record)], containers).Single(s => s.ServerId == record.Definition.Id).Status;

    [Theory]
    [InlineData("running", ContainerHealth.Healthy, ServerStatus.Running)]
    [InlineData("running", ContainerHealth.None, ServerStatus.Running)]
    [InlineData("running", ContainerHealth.Starting, ServerStatus.Starting)]
    [InlineData("running", ContainerHealth.Unhealthy, ServerStatus.Unhealthy)]
    [InlineData("exited", ContainerHealth.None, ServerStatus.Stopped)]
    [InlineData("created", ContainerHealth.None, ServerStatus.Created)]
    [InlineData("paused", ContainerHealth.None, ServerStatus.Paused)]
    [InlineData("restarting", ContainerHealth.None, ServerStatus.Restarting)]
    [InlineData("removing", ContainerHealth.None, ServerStatus.Removing)]
    [InlineData("dead", ContainerHealth.None, ServerStatus.Dead)]
    public void DockerStatesAreNotCompressed(string state, ContainerHealth health, ServerStatus expected)
    {
        var record = Ready();

        Assert.Equal(expected, StatusOf(record, FakeServerRuntime.State(record.Definition.Id, state, health)));
    }

    [Fact]
    public void UnknownDockerStateIsUnknownNotStopped()
    {
        var record = Ready();

        Assert.Equal(ServerStatus.Unknown, ServerCatalog.Build([Entry(record)], containers: null).Single().Status);
    }

    [Fact]
    public void RemovedOnPurposeVersusMissing()
    {
        Assert.Equal(ServerStatus.ContainerRemoved, StatusOf(Ready(containerId: null)));
        Assert.Equal(ServerStatus.ContainerMissing, StatusOf(Ready("c1")));
    }

    [Fact]
    public void ContainerNotReferencedByRecordNeedsRepair()
    {
        var record = Ready(containerId: null); // e.g. persisting the id was interrupted

        Assert.Equal(ServerStatus.UnrecordedContainer, StatusOf(record, FakeServerRuntime.State(record.Definition.Id, "created")));
    }

    [Fact]
    public void DuplicateContainersAreAConflict()
    {
        var record = Ready();
        var id = record.Definition.Id;

        Assert.Equal(ServerStatus.Conflict, StatusOf(record,
            FakeServerRuntime.State(id, "running", containerId: "c1"), FakeServerRuntime.State(id, "exited", containerId: "c2")));
    }

    [Theory]
    [InlineData(ProvisioningPhase.Provisioning)]
    [InlineData(ProvisioningPhase.Removing)]
    public void InterruptedOperationsAreIncompleteEvenWithoutDocker(ProvisioningPhase phase)
    {
        var record = Ready() with { Phase = phase };

        Assert.Equal(ServerStatus.Incomplete, ServerCatalog.Build([Entry(record)], containers: null).Single().Status);
    }

    [Fact]
    public void DamagedRecordReservesItsIdSoItsContainerIsNotAnOrphan()
    {
        var id = Guid.NewGuid();
        var snapshots = ServerCatalog.Build(
            [new RegistryEntry(id, new ServerPaths("/x"), null, "broken")],
            [FakeServerRuntime.State(id, "running")]);

        Assert.Equal(ServerStatus.DamagedRecord, Assert.Single(snapshots).Status);
    }

    [Fact]
    public void ContainerWithoutRecordIsAnOrphan()
    {
        var snapshots = ServerCatalog.Build([], [FakeServerRuntime.State(Guid.NewGuid(), "running")]);

        Assert.Equal(ServerStatus.Orphan, Assert.Single(snapshots).Status);
        Assert.Empty(ServerCatalog.AllowedActions(ServerStatus.Orphan));
    }

    [Fact]
    public void ActionsMatchStatus()
    {
        Assert.Contains(ServerAction.Start, ServerCatalog.AllowedActions(ServerStatus.Stopped));
        Assert.DoesNotContain(ServerAction.Start, ServerCatalog.AllowedActions(ServerStatus.Running));
        Assert.Contains(ServerAction.ForceStop, ServerCatalog.AllowedActions(ServerStatus.Unhealthy));
        Assert.DoesNotContain(ServerAction.RemoveContainer, ServerCatalog.AllowedActions(ServerStatus.Running));
        Assert.Equal([ServerAction.Repair], ServerCatalog.AllowedActions(ServerStatus.Incomplete));
        Assert.Empty(ServerCatalog.AllowedActions(ServerStatus.Unknown));
        Assert.Empty(ServerCatalog.AllowedActions(ServerStatus.Conflict));
        Assert.Empty(ServerCatalog.AllowedActions(ServerStatus.DamagedRecord));
    }
}
