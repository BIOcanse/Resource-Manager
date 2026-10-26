using ResourceManager.App.Domain.Operations;
using ResourceManager.App.Domain.RuntimeSpecialization;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerOperationCoordinatorOwnerTests
{
    [Fact]
    public async Task HostedStopStartReopensDurableRuntimeAndProcessesNewCommands()
    {
        await using var fixture = await Fixture.CreateAsync();
        var first = await fixture.SubmitComponentDownloadAsync();
        Assert.Equal(HostManagerOperationStates.Succeeded,
            (await fixture.WaitForTerminalAsync(first.Id)).State);
        var firstWorkspace = OwnerField<object>(fixture.Owner, "workspace");
        var firstWorker = OwnerField<object>(fixture.Owner, "plannerWorker");

        await fixture.Owner.StopAsync(CancellationToken.None).WaitAsync(TestBound);
        Assert.Null(OwnerField<object>(fixture.Owner, "workspace"));
        Assert.Null(OwnerField<object>(fixture.Owner, "store"));
        Assert.Null(OwnerField<object>(fixture.Owner, "workerCancellation"));
        Assert.False(fixture.Owner.GetHealth().Ready);

        await fixture.Owner.StartAsync(CancellationToken.None).WaitAsync(TestBound);
        Assert.True(fixture.Owner.GetHealth().Ready);
        Assert.NotSame(firstWorkspace, OwnerField<object>(fixture.Owner, "workspace"));
        Assert.NotSame(firstWorker, OwnerField<object>(fixture.Owner, "plannerWorker"));
        Assert.Equal(HostManagerOperationStates.Succeeded, fixture.Owner.Get(first.Id)!.State);
        var second = await fixture.SubmitComponentDownloadAsync();
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(HostManagerOperationStates.Succeeded,
            (await fixture.WaitForTerminalAsync(second.Id)).State);
        Assert.Equal(2, fixture.ComponentExecutor.ExecuteCount);

        var changed = CreateSmallPlan(root =>
            root["profile_revision"] = root["profile_revision"]!.GetValue<int>() + 1);
        fixture.Provider.Publish(CompiledRuntimePlan.Default with
        {
            Version = 2,
            CompiledAt = DateTimeOffset.UtcNow,
            Reason = "operation-owner-same-instance-reentry",
            HostManager = changed
        });
        await fixture.WaitForConfigurationGenerationAsync(
            changed.OperationCoordinator.HotPublish.ConfigurationGeneration);
        Assert.Null(fixture.Owner.LastPlanApplicationError);
    }
}
