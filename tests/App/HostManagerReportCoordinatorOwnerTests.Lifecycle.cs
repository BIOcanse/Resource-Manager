using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization.Reports;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using StrayFixture = Resource_Manager_APP.Tests.HostManagerStrayProcessReportFactProjectorTests;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerReportCoordinatorOwnerTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RetainedReportStateTrustEvidenceAndIdentitySurviveRuleRebindingAndRestart(bool history, bool trusted)
    {
        var persistence = new NativeReportCoordinatorPersistenceStore(CreateDatabase());
        var resources = new CachedResourceSource();
        var changedPlan = HostManagerTestPlanFactory.CreatePlan(planEpoch: 2, reports: new AppReportSettings(
            [new AppReportRuleOverride("background_memory_share", ActivationThreshold: 25)]));
        OptimizationReportItem retained;
        using (var owner = ScratchOwner(persistence, OperationScratchFixture.Create, resources, out var provider))
        {
            var observedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            for (var sample = 0; sample < (history ? 5 : 3); sample++)
            {
                resources.Publish(CreateSoftwareMemorySnapshot(41, observedAt.AddSeconds(sample * 10), sample < 3 ? 18 : 5));
                await owner.RefreshReportsAsync(CancellationToken.None);
            }
            var report = Assert.Single((await owner.GetReportsAsync(CancellationToken.None)).Reports);
            if (trusted) await owner.TrustReportAsync(report.Id, CancellationToken.None);
            retained = (await owner.GetReportAsync(report.Id, CancellationToken.None))!;
            Assert.Equal(history ? OptimizationReportStates.History : OptimizationReportStates.Active, retained.State);
            Assert.Equal(trusted, retained.Trusted);

            // No fresh target descriptor can repair a catalog overwritten during rebind.
            resources.Publish(CreateSoftwareMemorySnapshot(42, observedAt.AddSeconds(50), 0)
                with { Bars = [], Datasets = [] });
            provider.Publish(CompiledRuntimePlan.Default with { Version = 2, HostManager = changedPlan });
            var rebound = await owner.RefreshReportsAsync(CancellationToken.None);
            var reboundReport = Assert.Single(rebound.Reports);
            Assert.Equal(retained.Id, reboundReport.Id);
            Assert.Equal(retained.State, reboundReport.State);
            Assert.Equal(retained.Trusted, reboundReport.Trusted);
            Assert.Equal(retained.CreatedAt, reboundReport.CreatedAt);
            Assert.Equal(retained.LastObservedAt, reboundReport.LastObservedAt);
            Assert.Equal(retained.Evidence, reboundReport.Evidence);
            Assert.Equal(retained.Target.SoftwareId, reboundReport.Target.SoftwareId);
            Assert.Equal(retained.Target.SoftwareName, reboundReport.Target.SoftwareName);
            Assert.Equal(retained.Target.SoftwareKind, reboundReport.Target.SoftwareKind);
            Assert.Equal(retained.Target.ProcessIds, reboundReport.Target.ProcessIds);
            Assert.Equal(retained.Target.ProcessNames, reboundReport.Target.ProcessNames);
            Assert.Equal(history ? 0 : 1, rebound.Status.ActiveReportCount);
            Assert.Equal(0, rebound.Status.UnrecordedReportCount);
            Assert.Equal(!history && !trusted ? 1 : 0, owner.ReadSoftwareIssueSnapshot().Signals.Count);
            var catalog = ReportOwnerField<Dictionary<ulong, HostManagerReportTargetDescriptor>>(owner, "targetCatalog")!;
            Assert.Contains(catalog.Values, target => target.SoftwareId == retained.Target.SoftwareId);
        }

        var deployment = new HostManagerDeploymentState();
        var restartedProvider = new RuntimePlanProvider(deployment);
        restartedProvider.Publish(CompiledRuntimePlan.Default with { Version = 2, HostManager = changedPlan });
        using var restarted = new HostManagerReportCoordinatorOwner(restartedProvider, new HostManagerReportCoordinatorRuntime(deployment),
            new HostManagerRuntimeIdentity(), persistence, new CachedMetricSource(), new CachedResourceSource(),
            new UnavailableInterruptSource(), new EmptyProtectionService(), NullLogger<HostManagerReportCoordinatorOwner>.Instance,
            OperationScratchFixture.Create, new BackgroundProcessSource(), new RegisteredSoftware(include: false));
        // No current resource publication: restarting and unavailable data cannot judge the old entry.
        var restored = await restarted.RefreshReportsAsync(CancellationToken.None);
        var actual = Assert.Single(restored.Reports);
        Assert.Equal(retained.Id, actual.Id);
        Assert.Equal(retained.State, actual.State);
        Assert.Equal(retained.Trusted, actual.Trusted);
        Assert.Equal(retained.CreatedAt, actual.CreatedAt);
        Assert.Equal(retained.FirstObservedAt, actual.FirstObservedAt);
        Assert.Equal(retained.LastObservedAt, actual.LastObservedAt);
        Assert.Equal(retained.Evidence, actual.Evidence);
        Assert.Equal(retained.Target.SoftwareId, actual.Target.SoftwareId);
        Assert.Equal(retained.Target.SoftwareName, actual.Target.SoftwareName);
        Assert.Equal(history ? 0 : 1, restored.Status.ActiveReportCount);
        Assert.Equal(!history && !trusted ? 1 : 0, restarted.ReadSoftwareIssueSnapshot().Signals.Count);
        if (trusted)
        {
            Assert.True(await restarted.RemoveTrustedTargetAsync(Assert.Single(restored.TrustedTargets).Id, CancellationToken.None));
            var untrusted = (await restarted.GetReportAsync(actual.Id, CancellationToken.None))!;
            Assert.Equal(actual.State, untrusted.State);
            Assert.False(untrusted.Trusted);
            Assert.Equal(history ? 0 : 1, restarted.ReadSoftwareIssueSnapshot().Signals.Count);
        }
    }

    [Fact]
    public async Task FullReportCapacityRetainsHistoryAndPublishesUnrecordedCandidateCount()
    {
        var plan = HostManagerTestPlanFactory.CreatePlan(root =>
        {
            var capacity = root["host_recreate"]!["report_coordinator"]!["capacity"]!;
            capacity["maximum_report_count"] = 1;
            capacity["maximum_report_output_count"] = 1;
        });
        var deployment = new HostManagerDeploymentState();
        var provider = new RuntimePlanProvider(deployment);
        provider.Publish(CompiledRuntimePlan.Default with { Version = 1, HostManager = plan });
        var resources = new CachedResourceSource();
        var processes = new StrayProcessSource();
        using var owner = new HostManagerReportCoordinatorOwner(provider, new HostManagerReportCoordinatorRuntime(deployment),
            new HostManagerRuntimeIdentity(), new NativeReportCoordinatorPersistenceStore(CreateDatabase()),
            new CachedMetricSource(), resources, new UnavailableInterruptSource(), new EmptyProtectionService(),
            NullLogger<HostManagerReportCoordinatorOwner>.Instance, OperationScratchFixture.Create, processes, new RegisteredSoftware(false));
        foreach (var seconds in new[] { 0, 900 })
        {
            resources.Publish(StrayFixture.Memory(seconds));
            processes.Snapshot = StrayFixture.Processes(seconds);
            await owner.RefreshReportsAsync(CancellationToken.None);
        }
        var original = Assert.Single((await owner.GetReportsAsync(CancellationToken.None)).Reports);
        // First identity ends; its retained history must still consume capacity.
        resources.Publish(StrayFixture.Memory(901));
        processes.Snapshot = StrayFixture.Processes(901, cpu: 0.1);
        Assert.Equal(OptimizationReportStates.History,
            Assert.Single((await owner.RefreshReportsAsync(CancellationToken.None)).Reports).State);
        foreach (var seconds in new[] { 902, 1802 })
        {
            var snapshot = StrayFixture.Processes(seconds);
            processes.Snapshot = snapshot with { Processes = [snapshot.Processes[0] with { ProcessStartKey = 5678 }] };
            var memory = StrayFixture.Memory(seconds);
            var software = memory.Bars[0].Software[0];
            resources.Publish(memory with { Bars = [memory.Bars[0] with { Software = [software with
            {
                Processes = [software.Processes[0] with { ProcessStartKey = 5678 }]
            }] }] });
            await owner.RefreshReportsAsync(CancellationToken.None);
        }
        var full = await owner.GetReportsAsync(CancellationToken.None);
        Assert.Equal(original.Id, Assert.Single(full.Reports).Id);
        Assert.Equal(OptimizationReportStates.History, full.Reports[0].State);
        Assert.Equal(0, full.Status.ActiveReportCount);
        Assert.Equal(1, full.Status.UnrecordedReportCount);
        Assert.Equal("completed", full.Status.EvaluationState);
        // Another evaluation of the same publication does not count the candidate twice.
        Assert.Equal(1, (await owner.RefreshReportsAsync(CancellationToken.None)).Status.UnrecordedReportCount);
    }
}
