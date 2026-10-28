using ResourceManager.App.Domain.Operations;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.Shared.BusinessBlocks.State;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerOperationCoordinatorOwnerTests
{
    [Fact]
    public async Task PendingFactIsUnknownUntilCanonicalLoadAndPublishesKnownEmpty()
    {
        await using var fixture = await Fixture.CreateUnstartedAsync();
        Assert.Equal(NoValueReason.NotPublished, fixture.Owner.PendingOperationsPublication.NoValueReason);
        var publications = new List<StateRead<bool>>();
        fixture.Owner.PendingOperationsChanged += () => publications.Add(fixture.Owner.PendingOperationsPublication);

        await fixture.Owner.InitializeAsync(CancellationToken.None);
        Assert.False(fixture.Owner.PendingOperationsPublication.Value);
        Assert.Single(publications);
        Assert.False(publications[0].Value);
        Assert.Equal(0, fixture.ComponentExecutor.ExecuteCount);
    }

    [Fact]
    public async Task PendingFactLoadsPersistedQueuedOperationBeforeWorkersStart()
    {
        var root = CreateTemporaryRoot();
        var plan = CreateSmallPlan();
        try
        {
            await using (var first = await Fixture.CreateUnstartedAtAsync(root, plan))
            {
                await first.Owner.InitializeAsync(CancellationToken.None);
                await first.SubmitComponentDownloadAsync();
                Assert.True(first.Owner.PendingOperationsPublication.Value);
            }

            await using var reopened = await Fixture.CreateUnstartedAtAsync(root, plan);
            Assert.Equal(NoValueReason.NotPublished, reopened.Owner.PendingOperationsPublication.NoValueReason);
            var published = new List<StateRead<bool>>();
            reopened.Owner.PendingOperationsChanged += () => published.Add(reopened.Owner.PendingOperationsPublication);
            await reopened.Owner.InitializeAsync(CancellationToken.None);
            Assert.True(reopened.Owner.PendingOperationsPublication.Value);
            Assert.Contains(published, state => state.HasValue && state.Value);
            Assert.Equal(0, reopened.ComponentExecutor.ExecuteCount);
        }
        finally { DeleteTemporaryRoot(root); }
    }

    [Fact]
    public async Task StoppedWorkerAcceptsNewOperationAndPublishesPendingUntilSettlement()
    {
        await using var fixture = await Fixture.CreateAsync();
        var workspace = OwnerField<object>(fixture.Owner, "workspace");
        await fixture.Owner.StopAsync(CancellationToken.None).WaitAsync(TestBound);
        Assert.Null(OwnerField<object>(fixture.Owner, "workspace"));
        Assert.True(fixture.Owner.GetHealth().Ready);
        Assert.False(fixture.Owner.HasPendingOperations);
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Owner.PendingOperationsChanged += () =>
        {
            if (!fixture.Owner.HasPendingOperations) settled.TrySetResult();
        };
        var submitted = await fixture.SubmitComponentDownloadAsync();
        Assert.True(fixture.Owner.HasPendingOperations);
        Assert.Equal(0, fixture.ComponentExecutor.ExecuteCount);
        await fixture.Owner.StartAsync(CancellationToken.None).WaitAsync(TestBound);
        await settled.Task.WaitAsync(TestBound);
        Assert.Equal(HostManagerOperationStates.Succeeded, fixture.Owner.Get(submitted.Id)!.State);
        Assert.False(fixture.Owner.HasPendingOperations);
        Assert.NotSame(workspace, OwnerField<object>(fixture.Owner, "workspace"));
    }

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
        Assert.NotNull(OwnerField<object>(fixture.Owner, "store"));
        Assert.Null(OwnerField<object>(fixture.Owner, "workerCancellation"));
        Assert.True(fixture.Owner.GetHealth().Ready);

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

    private sealed partial class Fixture
    {
        internal static Task<Fixture> CreateUnstartedAtAsync(string root, CompiledHostManagerPlan plan)
            => CreateAsync(root, plan, ownsRoot: false, start: false);
    }
}
