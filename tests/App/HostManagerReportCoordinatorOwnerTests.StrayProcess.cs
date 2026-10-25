using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.Optimization.Reports;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using StrayFixture = Resource_Manager_APP.Tests.HostManagerStrayProcessReportFactProjectorTests;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerReportCoordinatorOwnerTests
{
    [Theory]
    [InlineData("cpu")]
    [InlineData("gpu")]
    [InlineData("memory")]
    [InlineData("exit")]
    public async Task StrayProcessActivatesAtWallTimeAndClearsOnActivityMemoryOrExit(string reason)
    {
        var deployment = new HostManagerDeploymentState();
        var provider = new RuntimePlanProvider(deployment);
        provider.Publish(CompiledRuntimePlan.Default with { Version = 1, HostManager = HostManagerTestPlanFactory.CreatePlan() });
        var resources = new CachedResourceSource();
        var processes = new StrayProcessSource();
        using var owner = new HostManagerReportCoordinatorOwner(provider, new HostManagerReportCoordinatorRuntime(deployment),
            new HostManagerRuntimeIdentity(), new NativeReportCoordinatorPersistenceStore(CreateDatabase()),
            new CachedMetricSource(), resources, new UnavailableInterruptSource(), new EmptyProtectionService(),
            NullLogger<HostManagerReportCoordinatorOwner>.Instance, OperationScratchFixture.Create, processes, new RegisteredSoftware(false));
        foreach (var seconds in new[] { 0, 7, 319, 899, 900 })
        {
            resources.Publish(StrayFixture.Memory(seconds));
            processes.Snapshot = StrayFixture.Processes(seconds);
            var overview = await owner.RefreshReportsAsync(CancellationToken.None);
            Assert.Equal(seconds == 900 ? 1 : 0, overview.Reports.Count);
        }
        var report = Assert.Single((await owner.GetReportsAsync(CancellationToken.None)).Reports);
        Assert.Equal(OptimizationReportTypes.SuspectedStrayProcess, report.Type);
        Assert.Equal(OptimizationReportTargetTypes.ProcessInstance, report.Target.TargetType);
        Assert.Equal("process:424242:1234", report.Target.TargetKey);
        Assert.Equal("tool", report.Target.SoftwareId);
        Assert.Equal(OptimizationSeverity.Info, report.Severity);
        Assert.Equal(900, report.Evidence.Process!.IdleDurationSeconds);
        Assert.Equal(50 * 1024 * 1024, report.Evidence.Process.MemoryBytes);
        Assert.Empty(owner.ReadSoftwareIssueSnapshot().Signals);
        // 45 MiB is the inclusive hysteresis boundary: only values below it clear.
        resources.Publish(StrayFixture.Memory(901, 45));
        processes.Snapshot = StrayFixture.Processes(901);
        Assert.Single((await owner.RefreshReportsAsync(CancellationToken.None)).Reports);
        resources.Publish(StrayFixture.Memory(902, reason == "memory" ? 44.9 : 50));
        processes.Snapshot = StrayFixture.Processes(902, reason == "cpu" ? 0.001 : 0, reason == "gpu" ? 0.001 : 0);
        if (reason == "exit") processes.Snapshot = processes.Snapshot with { Processes = [], EnumeratedCount = 0, EmittedCount = 0 };
        Assert.Empty((await owner.RefreshReportsAsync(CancellationToken.None)).Reports);
    }

    [Fact]
    public async Task StrayProcessBelowActivationAndMissingGpuDoNotCreateReportAndMissingDataDoesNotClearActive()
    {
        var deployment = new HostManagerDeploymentState();
        var provider = new RuntimePlanProvider(deployment);
        provider.Publish(CompiledRuntimePlan.Default with { Version = 1, HostManager = HostManagerTestPlanFactory.CreatePlan() });
        var resources = new CachedResourceSource();
        var processes = new StrayProcessSource();
        using var owner = new HostManagerReportCoordinatorOwner(provider, new HostManagerReportCoordinatorRuntime(deployment),
            new HostManagerRuntimeIdentity(), new NativeReportCoordinatorPersistenceStore(CreateDatabase()),
            new CachedMetricSource(), resources, new UnavailableInterruptSource(), new EmptyProtectionService(),
            NullLogger<HostManagerReportCoordinatorOwner>.Instance, OperationScratchFixture.Create, processes, new RegisteredSoftware(false));
        foreach (var seconds in new[] { 0, 900 })
        {
            resources.Publish(StrayFixture.Memory(seconds, 49.99)); processes.Snapshot = StrayFixture.Processes(seconds);
            Assert.Empty((await owner.RefreshReportsAsync(CancellationToken.None)).Reports);
        }
        resources.Publish(StrayFixture.Memory(901)); processes.Snapshot = StrayFixture.Processes(901);
        Assert.Single((await owner.RefreshReportsAsync(CancellationToken.None)).Reports);
        resources.Publish(StrayFixture.Memory(902));
        processes.Snapshot = StrayFixture.Processes(902);
        processes.Snapshot = processes.Snapshot with { Processes = [processes.Snapshot.Processes[0] with { ValidMetricMask = SchedulingProcessMetricMask.CpuUsage }] };
        Assert.Single((await owner.RefreshReportsAsync(CancellationToken.None)).Reports);
    }

    [Fact]
    public async Task StrayTrustIsScopedToProcessInstanceAndSurvivesOwnerRestart()
    {
        var deployment = new HostManagerDeploymentState();
        var provider = new RuntimePlanProvider(deployment);
        provider.Publish(CompiledRuntimePlan.Default with { Version = 1, HostManager = HostManagerTestPlanFactory.CreatePlan() });
        var resources = new CachedResourceSource();
        var processes = new StrayProcessSource();
        var persistence = new NativeReportCoordinatorPersistenceStore(CreateDatabase());
        HostManagerReportCoordinatorOwner Create() => new(provider, new HostManagerReportCoordinatorRuntime(deployment),
            new HostManagerRuntimeIdentity(), persistence, new CachedMetricSource(), resources,
            new UnavailableInterruptSource(), new EmptyProtectionService(), NullLogger<HostManagerReportCoordinatorOwner>.Instance,
            OperationScratchFixture.Create, processes, new RegisteredSoftware(false));
        using (var owner = Create())
        {
            foreach (var seconds in new[] { 0, 900 })
            {
                var snapshot = StrayFixture.Processes(seconds);
                processes.Snapshot = snapshot with { EnumeratedCount = 2, EmittedCount = 2,
                    Processes = [snapshot.Processes[0], snapshot.Processes[0] with { ProcessId = 424243 }] };
                var memory = StrayFixture.Memory(seconds);
                var software = memory.Bars[0].Software[0];
                resources.Publish(memory with { Bars = [memory.Bars[0] with { Software = [software with { ProcessCount = 2,
                    Processes = [software.Processes[0], software.Processes[0] with { ProcessId = 424243 }] }] }] });
                await owner.RefreshReportsAsync(CancellationToken.None);
            }
            var reports = (await owner.GetReportsAsync(CancellationToken.None)).Reports;
            Assert.Equal(2, reports.Count);
            var first = reports.Single(report => report.Target.ProcessIds.Contains(424242));
            await owner.TrustReportAsync(first.Id, CancellationToken.None);
            var current = await owner.GetReportsAsync(CancellationToken.None);
            Assert.Single(current.Reports, report => report.State == OptimizationReportStates.TrustedSuppressed);
            Assert.Single(current.Reports, report => report.State == OptimizationReportStates.Active);
        }
        deployment = new HostManagerDeploymentState();
        provider = new RuntimePlanProvider(deployment);
        provider.Publish(CompiledRuntimePlan.Default with { Version = 1, HostManager = HostManagerTestPlanFactory.CreatePlan() });
        using var restored = Create();
        await restored.StartAsync(CancellationToken.None);
        await restored.StopAsync(CancellationToken.None);
        var trust = Assert.Single((await restored.GetReportsAsync(CancellationToken.None)).TrustedTargets);
        Assert.Equal("process:424242:1234", trust.TargetKey);
        Assert.Equal("pwsh", trust.Name);
        Assert.True(await restored.RemoveTrustedTargetAsync(trust.Id, CancellationToken.None));
    }

    private sealed class StrayProcessSource : ISchedulingProcessFactObservationSource
    {
        internal SchedulingProcessFactSnapshot Snapshot { get; set; } = StrayFixture.Processes(0);
        public IDisposable AcquireSubscription(string subscriptionId, SchedulingProcessMetricMask metricMask, TimeSpan refreshInterval)
            => new CallbackLease(() => { });
        public SchedulingProcessFactSnapshot ReadLatest(SchedulingProcessFactRequest request) => Snapshot;
    }
}
