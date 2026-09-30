using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.GpuPlacement;
using ResourceManager.App.Infrastructure.GpuPlacement.WindowExecution;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization;
using static Resource_Manager_APP.Tests.GpuWindowLedgerTestData;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerSmartCoordinatorScoreOnlyCompositionTests
{
    [Fact]
    public async Task UnreleasedExternalControlRetainsGpuEffectsAndStillRunsIndependentCleanup()
    {
        var actions = new RecordingRunningGpuActions { HasUnreleasedExternalControl = true };
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: true,
            scoreOnlyEnabled: false, runningGpuActions: actions,
            policyExecutionEnabled: true,
            processRecoveryRead: pid => RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                new(pid, DateTimeOffset.FromFileTime(133_900_000_000_000_002))),
            optimizationCapabilities: OptimizationModeCapabilities.AutomaticMemoryCleanup,
            processFactsSnapshot: CreateCompleteProcessFacts(24_076, 133_900_000_000_000_002,
                "software:gpu-isolated-cleanup", 20, 50, 25));
        var gpuPlacement = Placement(Policy(Prepared())) with { ResourceKind = "GPU" };
        fixture.StateStore.Current = fixture.StateStore.Current with { AppliedPlacements = [gpuPlacement] };
        await fixture.PrimeMemoryCleanupAdmissionAsync();
        _ = await fixture.RunRealtimeCycleAsync();

        Assert.True(fixture.MetricSampler.ReadLatestCalls > 0);
        Assert.NotNull(fixture.Coordinator.SchedulingAuthority.Compute?.Cpu);
        Assert.True(fixture.MemoryCleanupPlanner.PlanCalls > 0, fixture.DescribeMemoryCleanupState());
        Assert.True(fixture.PublicResourceManager.TickCalls > 0);
        Assert.Equal(gpuPlacement, Assert.Single(fixture.StateStore.Current.AppliedPlacements));
        Assert.Equal(0, fixture.StateStore.SaveCalls);
        Assert.Equal(0, actions.PrepareCalls);
        Assert.Equal(0, actions.ApplyCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingGpuCheckpointAllowsSamplingScoresAndIndependentMemoryTransactions(bool coldWorkspace)
    {
        const int pid = 24_075;
        var startedAt = CreateProcessStartedAtOrderedBeforeSoftwareTarget(pid);
        var startKey = checked((ulong)startedAt.ToFileTime());
        var softwareId = CreateSoftwareIdOrderedAfterProcessTarget(pid, startKey);
        var path = Path.Combine(NewRoot("gpu-isolation"), "recovery.json");
        using var committer = new PausedWindowCommitter();
        using var store = new JsonHostManagerRollbackStateStore(path, new FixedTime(), TimeSpan.FromHours(1),
            committer, WindowsHostManagerDurableRootManifestCommitter.Instance);
        var counts = new WindowCountingStore(store);
        var actions = new RecordingRunningGpuActions { Available = true, HasUnreleasedExternalControl = true };
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(warm: !coldWorkspace,
            scoreOnlyEnabled: false,
            rollbackStateStoreOverride: counts,
            runningGpuActions: actions,
            automaticMemoryCleanupEnabled: false,
            memoryModePolicyEnabled: true,
            optimizationCapabilities: OptimizationModeCapabilities.NonAdaptedMemoryPriority,
            processFactsSnapshot: CreateCompleteProcessFacts(pid, startKey, softwareId, 20, 50, 25),
            processRecoveryRead: actualPid => RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(new(actualPid, startedAt)));
        uint memoryPriority = 5;
        fixture.ProcessPolicyWriter.ProcessReadHandler = actualPid => new(actualPid, "memory-gpu-isolation",
            @"c:\tests\memory-gpu-isolation.exe", startedAt, "Normal", 1, 1, memoryPriority);
        fixture.ProcessPolicyWriter.BatchHandler = requests => requests.Select(request =>
        {
            memoryPriority = Assert.IsType<uint>(request.MemoryPriority);
            return new ProcessResourcePolicyBatchWriteResult(request.ProcessId,
                [new(ProcessResourcePolicyBatchFields.MemoryPriority, true, "fixture memory priority applied")]);
        }).ToArray();

        var prepared = Prepared();
        var state = (await store.ReserveNativeHostSessionIncarnationAsync(default)) with
        { AppliedPlacements = [Placement(Policy(prepared))] };
        await store.SaveAsync(state, default);
        var key = HostManagerPlacementReceiptKey.Create(state.AppliedPlacements[0]);
        committer.PauseNext();
        var preparation = InvokeWindowOwner(fixture.Coordinator, "SaveGpuWindowPreparationAsync",
            state, key, prepared, CancellationToken.None);
        await committer.Entered.WaitAsync(TimeSpan.FromSeconds(5));
        var result = Unknown(prepared) with
        { Outcome = GpuWindowActionOutcome.NotExecuted, AuthorizationMayHaveBeenSent = false, PersistencePending = true };
        var final = InvokeWindowOwner(fixture.Coordinator, "SaveGpuWindowResultAsync", key,
            GpuWindowActionRecord.Create(prepared).RecordId, result, CancellationToken.None);
        var ledgerCalls = (counts.Loads, counts.Saves, counts.Reservations);
        try
        {
            _ = await fixture.RunRealtimeCycleAsync().WaitAsync(TimeSpan.FromSeconds(5));
            var authority = fixture.Coordinator.SchedulingAuthority;
            Assert.True(authority.Availability == HostManagerSchedulingAuthorityAvailability.Ready, authority.UnavailableReason);
            Assert.NotNull(authority.Compute?.Cpu);
            Assert.NotNull(authority.MemoryModes);
            Assert.Equal(ledgerCalls, (counts.Loads, counts.Saves, counts.Reservations));
            Assert.Same(final, CurrentWindowTask(fixture.Coordinator));
            Assert.Equal(coldWorkspace ? 0 : 1, fixture.ProcessPolicyWriter.BatchCalls);
            Assert.Equal(coldWorkspace ? 5U : 3U, memoryPriority);
            Assert.Equal(0, actions.PrepareCalls);
            Assert.Equal(0, actions.ApplyCalls);

            fixture.RuntimePlanProvider.Publish(fixture.RuntimePlan with
            {
                Version = fixture.RuntimePlan.Version + 1,
                OptimizationMode = CompiledOptimizationModePlan.Compile(
                    ResourceManager.App.Domain.Settings.AppOptimizationModes.Normal)
            });
            _ = await fixture.RunRealtimeCycleAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("optimization-mode-normal", fixture.Coordinator.SchedulingAuthority.UnavailableReason);
            Assert.Equal(5U, memoryPriority);
            Assert.Equal(coldWorkspace ? 0 : 2, fixture.ProcessPolicyWriter.BatchCalls);
            Assert.Equal(ledgerCalls, (counts.Loads, counts.Saves, counts.Reservations));
            Assert.Same(final, CurrentWindowTask(fixture.Coordinator));
        }
        finally
        {
            committer.Release();
            await preparation.WaitAsync(TimeSpan.FromSeconds(5));
            await final.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.True(await SettleWindowCheckpoint(fixture.Coordinator));
        Assert.Single(ReadCanonical(path).AppliedPlacements[0].Records, GpuWindowActionRecord.IsActionFact);
        actions.HasUnreleasedExternalControl = false;
        fixture.RuntimePlanProvider.Publish(fixture.RuntimePlan with { Version = fixture.RuntimePlan.Version + 2 });
        _ = await fixture.RunRealtimeCycleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(3U, memoryPriority);
        Assert.Equal(coldWorkspace ? 1 : 3, fixture.ProcessPolicyWriter.BatchCalls);
        Assert.Equal(0, actions.ApplyCalls);
    }
}
