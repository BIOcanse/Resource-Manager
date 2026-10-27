using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Application.SystemHealth;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Domain.SystemHealth;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization.Reports;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerReportCoordinatorOwnerTests
{
    [Theory]
    [InlineData(120, 1, 0, 0, 0, SchedulingProcessMetricMask.None)]
    [InlineData(121, 0, 0, 1, 0, SchedulingProcessMetricMask.None)]
    [InlineData(122, 0, 1, 0, 1, SchedulingProcessMetricMask.RuntimeState)]
    [InlineData(123, 1, 1, 0, 1, SchedulingProcessMetricMask.CpuUsage | SchedulingProcessMetricMask.GpuUsage)]
    [InlineData(0, 1, 1, 1, 1, SchedulingProcessMetricMask.RuntimeState | SchedulingProcessMetricMask.CpuUsage | SchedulingProcessMetricMask.GpuUsage)]
    public async Task PublishedRulesAcquireOnlyTheirInputsAtTheUnchangedPeriod(
        int family, int metrics, int resources, int interrupts, int processes, SchedulingProcessMetricMask processMask)
    {
        var plan = ReportFamilyPlan(family);
        var sources = new ReportDemandSources();
        await using var owner = DemandOwner(plan, sources, out _);
        await owner.StartAsync(CancellationToken.None);
        try
        {
            await owner.RefreshReportsAsync(CancellationToken.None);
            Assert.Equal((metrics, resources, interrupts, processes), sources.Counts);
            Assert.Equal(processMask, sources.ProcessMask);
            if (sources.MetricRequest is { } request)
            {
                Assert.Equal(family is 0 or 120, request.Includes("cpu.temperature"));
                Assert.Equal(family is 0 or 120, request.Includes("cpu.usage"));
                Assert.Equal(family is 0 or 120, request.Includes("cpu.frequencyPercent"));
                Assert.Equal(family is 0 or 123, request.Includes(ResourceManager.App.Domain.Monitoring.SamplingDatasetIds.SystemGpuInventory));
            }
            if (sources.ResourceRequest is { } resourceRequest)
            {
                Assert.Contains(ResourceBreakdownMetricIds.MemoryUsage, resourceRequest.MetricIds);
                Assert.Equal(family is 0 or 123, resourceRequest.MetricIds.Contains("process_all_command_line_actual"));
                Assert.Equal(ResourceBreakdownScaleModes.Capacity, resourceRequest.ScaleModes[ResourceBreakdownMetricIds.MemoryUsage]);
            }
            Assert.All(sources.Intervals, interval => Assert.Equal(TimeSpan.FromSeconds(10), interval));
            Assert.Equal(plan.ReportCoordinator.Recreate.Rules.Length, owner.GetStatus().ConfiguredRuleCount);
            Assert.Empty((await owner.GetReportsAsync(CancellationToken.None)).Reports);
        }
        finally { await owner.StopAsync(CancellationToken.None); }
        Assert.Equal((0, 0, 0, 0), sources.Counts);
        // A direct refresh after Stop reads current facts without retaining demand.
        await owner.RefreshReportsAsync(CancellationToken.None);
        Assert.Equal((0, 0, 0, 0), sources.Counts);
    }

    [Fact]
    public async Task PublicationUpdatesInputDemandAndUnchangedDemandKeepsItsLeases()
    {
        var sources = new ReportDemandSources();
        await using var owner = DemandOwner(ReportFamilyPlan(0), sources, out var provider);
        await owner.StartAsync(CancellationToken.None);
        try
        {
            await owner.RefreshReportsAsync(CancellationToken.None);
            Assert.Equal((1, 1, 1, 1), sources.Counts);
            var acquisitions = sources.Acquisitions;
            var changedThreshold = HostManagerTestPlanFactory.CreatePlan(reports: new ResourceManager.App.Domain.Settings.AppReportSettings(
                [new ResourceManager.App.Domain.Settings.AppReportRuleOverride("cpu_throttle_temperature", ActivationThreshold: 95)]));
            provider.Publish(CompiledRuntimePlan.Default with { Version = 2, HostManager = changedThreshold });
            await owner.RefreshReportsAsync(CancellationToken.None);
            Assert.Equal(acquisitions, sources.Acquisitions);

            provider.Publish(CompiledRuntimePlan.Default with { Version = 3, HostManager = ReportFamilyPlan(121) });
            await owner.RefreshReportsAsync(CancellationToken.None);
            Assert.Equal((0, 0, 1, 0), sources.Counts);

            provider.Publish(CompiledRuntimePlan.Default with { Version = 4, HostManager = ReportFamilyPlan(122) });
            await owner.RefreshReportsAsync(CancellationToken.None);
            Assert.Equal((0, 1, 0, 1), sources.Counts);
            Assert.Equal(SchedulingProcessMetricMask.RuntimeState, sources.ProcessMask);
        }
        finally { await owner.StopAsync(CancellationToken.None); }
        Assert.Equal((0, 0, 0, 0), sources.Counts);
    }

    [Fact]
    public async Task StopReleasesDemandAdmittedByABlockedRulePublication()
    {
        var sources = new ReportDemandSources();
        await using var owner = DemandOwner(ReportFamilyPlan(121), sources, out var provider);
        await owner.StartAsync(CancellationToken.None);
        using var resume = new ManualResetEventSlim(false);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sources.BeforeMetricAcquire = () =>
        {
            entered.TrySetResult();
            if (!resume.Wait(ScratchTestBound)) throw new TimeoutException("The controlled report acquisition was not released.");
        };
        provider.Publish(CompiledRuntimePlan.Default with { Version = 2, HostManager = ReportFamilyPlan(123) });
        var refresh = Task.Run(() => owner.RefreshReportsAsync(CancellationToken.None));
        try
        {
            await entered.Task.WaitAsync(ScratchTestBound);
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await owner.StopAsync(cancelled.Token);
            resume.Set();
            await refresh.WaitAsync(ScratchTestBound);
            await owner.StopAsync(CancellationToken.None);
            Assert.Equal((0, 0, 0, 0), sources.Counts);
        }
        finally
        {
            resume.Set();
            await refresh.WaitAsync(ScratchTestBound);
            await owner.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task StrayOnlyReportUsesActivityAndIdentityWithoutRuntimeState()
    {
        var sources = new ReportDemandSources();
        await using var owner = DemandOwner(ReportFamilyPlan(123), sources, out _);
        await owner.StartAsync(CancellationToken.None);
        try
        {
            foreach (var seconds in new[] { 0, 900 })
            {
                sources.Resources.Publish(HostManagerStrayProcessReportFactProjectorTests.Memory(seconds));
                sources.ProcessSnapshot = HostManagerStrayProcessReportFactProjectorTests.Processes(seconds);
                var overview = await owner.RefreshReportsAsync(CancellationToken.None);
                Assert.Equal(seconds == 900 ? 1 : 0, overview.Reports.Count);
            }
            Assert.Equal(SchedulingProcessMetricMask.CpuUsage | SchedulingProcessMetricMask.GpuUsage, sources.ProcessMask);
            var report = Assert.Single((await owner.GetReportsAsync(CancellationToken.None)).Reports);
            Assert.Equal(ResourceManager.App.Domain.Optimization.OptimizationReportTypes.SuspectedStrayProcess, report.Type);
            Assert.Equal(900, report.Evidence.Process!.IdleDurationSeconds);
            Assert.Equal(50 * 1024 * 1024, report.Evidence.Process.MemoryBytes);
            sources.Resources.Publish(HostManagerStrayProcessReportFactProjectorTests.Memory(901));
            sources.ProcessSnapshot = HostManagerStrayProcessReportFactProjectorTests.Processes(901, cpu: 0.001);
            Assert.Empty((await owner.RefreshReportsAsync(CancellationToken.None)).Reports);
        }
        finally { await owner.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task BackgroundMemoryReportMatchesDefaultRulesWithoutUnrelatedInputs()
    {
        var defaults = HostManagerTestPlanFactory.CreatePlan();
        Assert.Equal(new[] { "cpu_throttle_temperature", "cpu_throttle_usage", "cpu_throttle_frequency", "interrupt_longest_event",
            "interrupt_cpu_share", "background_memory_share", "background_memory_bytes", "stray_process_memory", "stray_process_idle" },
            defaults.ReportCoordinator.Recreate.Rules.Select(rule => rule.Name));
        Assert.Equal(new double[] { 90, 70, 85, 10, 5, 10, 4L * 1024 * 1024 * 1024, 50 * 1024 * 1024, 15 * 60 * 1000 },
            defaults.ReportCoordinator.Recreate.Rules.Select(rule => rule.ActivationThreshold));
        var allInputs = new ReportDemandSources();
        var memoryInputs = new ReportDemandSources();
        await using var allOwner = DemandOwner(defaults, allInputs, out _);
        await using var memoryOwner = DemandOwner(ReportFamilyPlan(122), memoryInputs, out _);
        await allOwner.StartAsync(CancellationToken.None);
        await memoryOwner.StartAsync(CancellationToken.None);
        try
        {
            var observedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            for (var index = 0; index < 3; index++)
            {
                var snapshot = CreateSoftwareMemorySnapshot(index + 1, observedAt.AddSeconds(index * 10), 18);
                allInputs.Resources.Publish(snapshot);
                memoryInputs.Resources.Publish(snapshot);
                await allOwner.RefreshReportsAsync(CancellationToken.None);
                await memoryOwner.RefreshReportsAsync(CancellationToken.None);
            }
            var expected = Assert.Single((await allOwner.GetReportsAsync(CancellationToken.None)).Reports);
            var actual = Assert.Single((await memoryOwner.GetReportsAsync(CancellationToken.None)).Reports);
            Assert.Equal(expected.Id, actual.Id);
            Assert.Equal(expected.Type, actual.Type);
            Assert.Equal(expected.Target.SoftwareId, actual.Target.SoftwareId);
            // Separate owners stamp their commands with their own wall-clock
            // times; only the report facts must match across these executions.
            Assert.Equal(expected.Evidence with { DurationSeconds = 0 },
                actual.Evidence with { DurationSeconds = 0 });
            Assert.True(double.IsFinite(expected.Evidence.DurationSeconds));
            Assert.True(expected.Evidence.DurationSeconds >= 0);
            Assert.True(double.IsFinite(actual.Evidence.DurationSeconds));
            Assert.True(actual.Evidence.DurationSeconds >= 0);
            Assert.Equal((0, 1, 0, 1), memoryInputs.Counts);
        }
        finally
        {
            await allOwner.StopAsync(CancellationToken.None);
            await memoryOwner.StopAsync(CancellationToken.None);
        }
    }

    private static CompiledHostManagerPlan ReportFamilyPlan(int family)
        => HostManagerTestPlanFactory.CreatePlan(root =>
        {
            if (family == 0) return;
            var rules = root["host_recreate"]!["report_coordinator"]!["rules"]!.AsArray();
            for (var index = rules.Count - 1; index >= 0; index--)
                if (rules[index]!["family_handle"]!.GetValue<int>() != family) rules.RemoveAt(index);
        });

    private HostManagerReportCoordinatorOwner DemandOwner(CompiledHostManagerPlan plan, ReportDemandSources sources, out RuntimePlanProvider provider)
    {
        var deployment = new HostManagerDeploymentState();
        provider = new RuntimePlanProvider(deployment);
        provider.Publish(CompiledRuntimePlan.Default with { Version = 1, HostManager = plan });
        return new HostManagerReportCoordinatorOwner(provider, new HostManagerReportCoordinatorRuntime(deployment),
            new HostManagerRuntimeIdentity(), new NativeReportCoordinatorPersistenceStore(new ResourceManager.App.Infrastructure.Persistence.ResourceManagerDatabase(
                new TestHostEnvironment(Directory.CreateDirectory(Path.Combine(testRoot, $"demand-{Guid.NewGuid():N}")).FullName))), sources, sources, sources,
            new EmptyProtectionService(), NullLogger<HostManagerReportCoordinatorOwner>.Instance, OperationScratchFixture.Create,
            sources, new RegisteredSoftware());
    }

    private sealed class ReportDemandSources : IMetricSnapshotObservationSource, IResourceBreakdownObservationSource,
        ISystemInterruptSnapshotSource, ISchedulingProcessFactObservationSource
    {
        private readonly int[] counts = new int[4];
        internal readonly CachedResourceSource Resources = new();
        internal (int, int, int, int) Counts => (counts[0], counts[1], counts[2], counts[3]);
        internal int Acquisitions { get; private set; }
        internal List<TimeSpan> Intervals { get; } = [];
        internal MetricSampleRequest? MetricRequest { get; private set; }
        internal ResourceBreakdownSampleRequest? ResourceRequest { get; private set; }
        internal SchedulingProcessMetricMask ProcessMask { get; private set; }
        internal SchedulingProcessFactSnapshot? ProcessSnapshot { get; set; }
        internal Action? BeforeMetricAcquire;
        private IDisposable Acquire(int index, TimeSpan interval)
        {
            counts[index]++;
            Acquisitions++;
            Intervals.Add(interval);
            return new CallbackLease(() => counts[index]--);
        }
        public IDisposable AcquireSubscription(string id, MetricSampleRequest request, TimeSpan interval)
        {
            Interlocked.Exchange(ref BeforeMetricAcquire, null)?.Invoke();
            MetricRequest = request;
            return Acquire(0, interval);
        }
        public IDisposable AcquireSubscription(string id, ResourceBreakdownSampleRequest request, TimeSpan interval)
        { ResourceRequest = request; return Acquire(1, interval); }
        public IDisposable AcquireSubscription(string id, TimeSpan interval) => Acquire(2, interval);
        public IDisposable AcquireSubscription(string id, SchedulingProcessMetricMask mask, TimeSpan interval)
        { ProcessMask = mask; return Acquire(3, interval); }
        public HardwareMetricSnapshot? ReadLatest(MetricSampleRequest request) => null;
        public ResourceBreakdownSnapshot ReadLatest(ResourceBreakdownSampleRequest request) => Resources.ReadLatest(request);
        public SystemInterruptSnapshot Read() => new UnavailableInterruptSource().Read();
        public SchedulingProcessFactSnapshot ReadLatest(SchedulingProcessFactRequest request) => ProcessSnapshot ?? new BackgroundProcessSource().ReadLatest(request);
    }
}
