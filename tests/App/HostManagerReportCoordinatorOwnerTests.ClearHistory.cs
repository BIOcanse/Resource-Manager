using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization.Reports;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using StrayFixture = Resource_Manager_APP.Tests.HostManagerStrayProcessReportFactProjectorTests;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerReportCoordinatorOwnerTests
{
    [Fact]
    public async Task ColdOwnerCanClearPersistedHistoryWithoutStartingOrEvaluatingSources()
    {
        var persistence = new NativeReportCoordinatorPersistenceStore(CreateDatabase());
        var resources = new CachedResourceSource();
        var processes = new StrayProcessSource();
        OptimizationReportItem retainedActive;
        string historyId;
        string trustId;
        await using (var previous = ClearHistoryOwner(persistence, resources, processes))
        {
            var (history, active) = await PrepareClearHistoryReports(previous, resources, processes);
            retainedActive = active;
            historyId = history.Id;
            trustId = (await previous.TrustReportAsync(history.Id, CancellationToken.None))!.Id;
        }

        var unavailableResources = new CachedResourceSource();
        // This process snapshot omits the active target. Loading must not treat it as an exit judgment.
        await using var cold = ClearHistoryOwner(persistence, unavailableResources, new StrayProcessSource());
        Assert.Null(ReportOwnerField<NativeReportCoordinatorWorkspace>(cold, "workspace"));

        Assert.Equal(1, await cold.ClearHistoryAsync(CancellationToken.None));
        Assert.Null(ReportOwnerField<NativeReportCoordinatorWorkspace>(cold, "workspace"));

        var overview = await cold.GetReportsAsync(CancellationToken.None);
        var activeReport = Assert.Single(overview.Reports);
        Assert.Equal(retainedActive.Id, activeReport.Id);
        Assert.Equal(OptimizationReportStates.Active, activeReport.State);
        Assert.Equal(retainedActive.CreatedAt, activeReport.CreatedAt);
        Assert.Equal(retainedActive.LastObservedAt, activeReport.LastObservedAt);
        Assert.Equal(retainedActive.Evidence, activeReport.Evidence);
        Assert.Equal(1, overview.Status.ActiveReportCount);
        Assert.Null(overview.Status.LastEvaluationAt);
        Assert.Equal("notEvaluated", overview.Status.EvaluationState);
        Assert.Equal(0, unavailableResources.ActiveSubscriptions);
        Assert.Null(await cold.GetReportAsync(historyId, CancellationToken.None));
        Assert.Equal(trustId, Assert.Single(overview.TrustedTargets).Id);
        Assert.Equal(0, await cold.ClearHistoryAsync(CancellationToken.None));
        var rows = await persistence.LoadAsync(CancellationToken.None);
        Assert.Single(rows, row => row.OperationKind == (uint)NativeReportPersistenceKind.Report);
        Assert.Single(rows, row => row.OperationKind == (uint)NativeReportPersistenceKind.Trust);
    }

    [Fact]
    public async Task ClearHistoryPersistsBeforePublishingAndSerializesQueuedEvaluationWithoutClearingNewHistory()
    {
        var inner = new NativeReportCoordinatorPersistenceStore(CreateDatabase());
        var persistence = new ControlledReportStore(inner);
        var resources = new CachedResourceSource();
        var processes = new StrayProcessSource();
        await using var owner = ClearHistoryOwner(persistence, resources, processes);
        var (history, active) = await PrepareClearHistoryReports(owner, resources, processes);
        var trust = await owner.TrustReportAsync(history.Id, CancellationToken.None);
        Assert.NotNull(trust);
        var before = await owner.GetReportsAsync(CancellationToken.None);
        persistence.BlockNext = true;

        var clear = owner.ClearHistoryAsync(CancellationToken.None);
        await persistence.Entered.Task.WaitAsync(ScratchTestBound);
        Task<OptimizationReportOverview>? refresh = null;
        try
        {
            Assert.False(clear.IsCompleted);
            Assert.Equal(before.Reports, (await owner.GetReportsAsync(CancellationToken.None)).Reports);
            Assert.Equal(2, (await inner.LoadAsync(CancellationToken.None)).Count(
                row => row.OperationKind == (uint)NativeReportPersistenceKind.Report));
            PublishClearHistoryProcesses(resources, processes, 902, secondActive: false);
            refresh = owner.RefreshReportsAsync(CancellationToken.None);
            Assert.False(refresh.IsCompleted);
        }
        finally { persistence.Release.TrySetResult(); }

        Assert.Equal(1, await clear.WaitAsync(ScratchTestBound));
        var evaluated = await refresh!.WaitAsync(ScratchTestBound);
        var newHistory = Assert.Single(evaluated.Reports);
        Assert.Equal(active.Id, newHistory.Id);
        Assert.Equal(OptimizationReportStates.History, newHistory.State);
        Assert.Null(await owner.GetReportAsync(history.Id, CancellationToken.None));
        Assert.Equal(trust!.Id, Assert.Single(evaluated.TrustedTargets).Id);
        Assert.Equal(0, evaluated.Status.ActiveReportCount);
        Assert.Equal(1, await owner.ClearHistoryAsync(CancellationToken.None));
        Assert.Equal(0, await owner.ClearHistoryAsync(CancellationToken.None));
        Assert.Empty((await owner.GetReportsAsync(CancellationToken.None)).Reports);
        Assert.Single(await owner.GetTrustedTargetsAsync(CancellationToken.None));
        foreach (var seconds in new[] { 903, 1803 })
        {
            PublishClearHistoryProcesses(resources, processes, seconds, secondActive: true);
            await owner.RefreshReportsAsync(CancellationToken.None);
        }
        var recreated = Assert.Single((await owner.GetReportsAsync(CancellationToken.None)).Reports);
        Assert.NotEqual(active.Id, recreated.Id);
        Assert.Equal(active.Target.TargetKey, recreated.Target.TargetKey);
        Assert.Equal(OptimizationReportStates.Active, recreated.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClearHistoryFailedPersistenceReplaysOnlyThePendingBatchBeforeNewJudgments(bool committedBeforeFailure)
    {
        var inner = new NativeReportCoordinatorPersistenceStore(CreateDatabase());
        var persistence = new ControlledReportStore(inner);
        var resources = new CachedResourceSource();
        var processes = new StrayProcessSource();
        OptimizationReportItem remaining;
        string trustId;
        await using (var owner = ClearHistoryOwner(persistence, resources, processes))
        {
            await using var hold = HoldClearHistoryWorkspace(owner);
            var (history, active) = await PrepareClearHistoryReports(owner, resources, processes);
            trustId = (await owner.TrustReportAsync(history.Id, CancellationToken.None))!.Id;
            var before = ReportOwnerField<HostManagerReportReadModel>(owner, "readModel")!;
            persistence.FailNext = !committedBeforeFailure;
            persistence.FailAfterCommitNext = committedBeforeFailure;

            await Assert.ThrowsAsync<IOException>(() => owner.ClearHistoryAsync(CancellationToken.None));

            Assert.Same(before, ReportOwnerField<HostManagerReportReadModel>(owner, "readModel"));
            Assert.NotNull(ReportOwnerField<NativeReportCoordinatorWorkspace>(owner, "workspace")!.PendingPlan);
            var diskReports = (await inner.LoadAsync(CancellationToken.None))
                .Where(row => row.OperationKind == (uint)NativeReportPersistenceKind.Report).ToArray();
            Assert.Equal(committedBeforeFailure ? 1 : 2, diskReports.Length);
            PublishClearHistoryProcesses(resources, processes, 902, secondActive: false);

            // Refresh first retries the exact deletion batch, then judges the formerly active entry.
            var recovered = await owner.RefreshReportsAsync(CancellationToken.None);
            remaining = Assert.Single(recovered.Reports);
            Assert.Equal(active.Id, remaining.Id);
            Assert.Equal(OptimizationReportStates.History, remaining.State);
            Assert.Equal(trustId, Assert.Single(recovered.TrustedTargets).Id);
            Assert.Null(ReportOwnerField<NativeReportCoordinatorWorkspace>(owner, "workspace")!.PendingPlan);
        }

        await using var restarted = ClearHistoryOwner(persistence, new CachedResourceSource(), UnavailableClearHistoryProcesses());
        var restored = await restarted.RefreshReportsAsync(CancellationToken.None);
        var report = Assert.Single(restored.Reports);
        Assert.Equal(remaining.Id, report.Id);
        Assert.Equal(remaining.State, report.State);
        Assert.Equal(remaining.Evidence, report.Evidence);
        Assert.Equal(trustId, Assert.Single(restored.TrustedTargets).Id);
        Assert.Equal(1, await restarted.ClearHistoryAsync(CancellationToken.None));
        Assert.Equal(0, await restarted.ClearHistoryAsync(CancellationToken.None));
        Assert.Single(await restarted.GetTrustedTargetsAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClearHistoryRestartRestoresTheDurableOutcomeOfAnUnacknowledgedDeletion(bool committedBeforeFailure)
    {
        var inner = new NativeReportCoordinatorPersistenceStore(CreateDatabase());
        var persistence = new ControlledReportStore(inner);
        string activeId;
        string historyId;
        string trustId;
        var resources = new CachedResourceSource();
        var processes = new StrayProcessSource();
        await using (var owner = ClearHistoryOwner(persistence, resources, processes))
        {
            await using var hold = HoldClearHistoryWorkspace(owner);
            var (history, active) = await PrepareClearHistoryReports(owner, resources, processes);
            activeId = active.Id;
            historyId = history.Id;
            trustId = (await owner.TrustReportAsync(history.Id, CancellationToken.None))!.Id;
            persistence.FailNext = !committedBeforeFailure;
            persistence.FailAfterCommitNext = committedBeforeFailure;
            await Assert.ThrowsAsync<IOException>(() => owner.ClearHistoryAsync(CancellationToken.None));
            // Dispose without settling the pending delete, as if the service restarted here.
        }

        await using var restarted = ClearHistoryOwner(persistence, new CachedResourceSource(), UnavailableClearHistoryProcesses());
        var restored = await restarted.RefreshReportsAsync(CancellationToken.None);
        Assert.Equal(committedBeforeFailure ? 1 : 2, restored.Reports.Count);
        Assert.Equal(OptimizationReportStates.Active, restored.Reports.Single(report => report.Id == activeId).State);
        Assert.Equal(!committedBeforeFailure, restored.Reports.Any(report => report.Id == historyId));
        Assert.Equal(trustId, Assert.Single(restored.TrustedTargets).Id);
        Assert.Equal(committedBeforeFailure ? 0 : 1, await restarted.ClearHistoryAsync(CancellationToken.None));
        Assert.Equal(activeId, Assert.Single((await restarted.GetReportsAsync(CancellationToken.None)).Reports).Id);
        Assert.Single(await restarted.GetTrustedTargetsAsync(CancellationToken.None));
    }

    // Holds the workspace as a running report block does, so a failed batch stays pending in it.
    private static IAsyncDisposable HoldClearHistoryWorkspace(HostManagerReportCoordinatorOwner owner)
        => (IAsyncDisposable)typeof(HostManagerReportCoordinatorOwner)
            .GetMethod("AcquireWorkspaceHold", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(owner, [CancellationToken.None])!;

    private static StrayProcessSource UnavailableClearHistoryProcesses()
        => new() { Snapshot = StrayFixture.Processes(0) with { InventoryStatus = SamplingObservationStatus.Unavailable } };

    private HostManagerReportCoordinatorOwner ClearHistoryOwner(INativeReportCoordinatorPersistenceStore persistence,
        CachedResourceSource resources, StrayProcessSource processes)
    {
        var deployment = new HostManagerDeploymentState();
        var provider = new RuntimePlanProvider(deployment);
        provider.Publish(CompiledRuntimePlan.Default with { Version = 1, HostManager = HostManagerTestPlanFactory.CreatePlan() });
        return new HostManagerReportCoordinatorOwner(provider, new HostManagerReportCoordinatorRuntime(deployment),
            new HostManagerRuntimeIdentity(), persistence, new CachedMetricSource(), resources,
            new UnavailableInterruptSource(), new EmptyProtectionService(), NullLogger<HostManagerReportCoordinatorOwner>.Instance,
            OperationScratchFixture.Create, processes, new RegisteredSoftware(include: false));
    }

    private async Task<(OptimizationReportItem History, OptimizationReportItem Active)> PrepareClearHistoryReports(
        HostManagerReportCoordinatorOwner owner, CachedResourceSource resources, StrayProcessSource processes)
    {
        foreach (var seconds in new[] { 0, 900, 901 })
        {
            PublishClearHistoryProcesses(resources, processes, seconds, secondActive: true);
            await owner.RefreshReportsAsync(CancellationToken.None);
        }
        var reports = (await owner.GetReportsAsync(CancellationToken.None)).Reports;
        return (Assert.Single(reports, report => report.State == OptimizationReportStates.History),
            Assert.Single(reports, report => report.State == OptimizationReportStates.Active));
    }

    private static void PublishClearHistoryProcesses(CachedResourceSource resources, StrayProcessSource processes,
        int seconds, bool secondActive)
    {
        var snapshot = StrayFixture.Processes(seconds);
        processes.Snapshot = snapshot with
        {
            EnumeratedCount = 2, EmittedCount = 2,
            Processes = [snapshot.Processes[0] with { CpuUsagePercent = seconds > 900 ? 0.1 : 0 },
                snapshot.Processes[0] with { ProcessId = 424243, CpuUsagePercent = secondActive ? 0 : 0.1 }]
        };
        var memory = StrayFixture.Memory(seconds);
        var software = memory.Bars[0].Software[0];
        resources.Publish(memory with { Bars = [memory.Bars[0] with { Software = [software with
        {
            ProcessCount = 2, Processes = [software.Processes[0], software.Processes[0] with { ProcessId = 424243 }]
        }] }] });
    }
}
