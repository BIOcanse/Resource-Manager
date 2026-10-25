using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.ProcessAttribution;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization.Reports;

namespace Resource_Manager_APP.Tests;

public sealed class HostManagerStrayProcessReportFactProjectorTests
{
    internal static readonly DateTimeOffset Origin = DateTimeOffset.UtcNow.AddHours(-1);
    internal static readonly CompiledHostManagerReportCoordinatorRulePlan[] Rules = HostManagerTestPlanFactory.CreatePlan()
        .ReportCoordinator.Recreate.Rules.Where(rule => HostManagerStrayProcessReportFactProjector.Supports(rule.FactKind)).ToArray();

    [Fact]
    public void IdleUsesWallTimeAcrossIrregularIntervalsAndDoesNotAdvanceOnRepeatedPublication()
    {
        var projector = new HostManagerStrayProcessReportFactProjector();
        Assert.Equal(0, Idle(Project(projector, 0)));
        Assert.Equal(13_000, Idle(Project(projector, 13)));
        Assert.Equal(422_000, Idle(Project(projector, 422)));
        Assert.Equal(899_000, Idle(Project(projector, 899)));
        Assert.Equal(899_000, Idle(Project(projector, 899)));
        Assert.Equal(900_000, Idle(Project(projector, 900)));
        var targets = new Dictionary<ulong, HostManagerReportTargetDescriptor>();
        var source = projector.Project(Rules, Memory(901), Processes(901), targets, new HashSet<string>());
        Assert.Equal(NativeReportSourceStatus.Complete, source.Status);
        var target = Assert.Single(targets).Value;
        Assert.Equal("tool", target.SoftwareName);
        Assert.Equal(901, target.Process!.IdleDurationSeconds);
        Assert.Equal(50 * 1024 * 1024, target.Process.MemoryBytes);
        Assert.Equal("1234", target.Process.ProcessStartKey);
    }

    [Theory]
    [InlineData(0.001, 0)]
    [InlineData(0, 0.001)]
    public void AnyCpuOrGpuActivityRestartsIdleWindow(double cpu, double gpu)
    {
        var projector = new HostManagerStrayProcessReportFactProjector();
        Project(projector, 0);
        Assert.Equal(800_000, Idle(Project(projector, 800)));
        Assert.Equal(0, Idle(Project(projector, 810, cpu: cpu, gpu: gpu)));
        Assert.Equal(0, Idle(Project(projector, 820)));
        Assert.Equal(80_000, Idle(Project(projector, 900)));
    }

    [Theory]
    [InlineData("unlisted.exe", "tool", false)]
    [InlineData("pwsh.exe", RuntimeAttributionIds.ResourceManagerSelf, false)]
    [InlineData("pwsh.exe", "tool", true)]
    public void ExcludesNotListedSelfAndProtectedSoftware(string executable, string softwareId, bool isProtected)
    {
        var projector = new HostManagerStrayProcessReportFactProjector();
        var snapshot = Processes(900);
        var process = snapshot.Processes[0] with { ExecutablePath = $@"C:\Tools\{executable}", SoftwareId = softwareId };
        var source = projector.Project(Rules, Memory(900), snapshot with { Processes = [process] },
            new Dictionary<ulong, HostManagerReportTargetDescriptor>(), isProtected ? new HashSet<string> { softwareId } : new HashSet<string>());
        Assert.Empty(source.Facts);
    }

    [Fact]
    public void ExcludesCurrentHostEvenWithoutSelfAttribution()
    {
        var snapshot = Processes(0);
        var source = new HostManagerStrayProcessReportFactProjector().Project(Rules, Memory(0),
            snapshot with { Processes = [snapshot.Processes[0] with { ProcessId = Environment.ProcessId }] },
            new Dictionary<ulong, HostManagerReportTargetDescriptor>(), new HashSet<string>());
        Assert.Empty(source.Facts);
    }

    [Theory]
    [InlineData(SchedulingProcessMetricMask.GpuUsage)]
    [InlineData(SchedulingProcessMetricMask.CpuUsage)]
    public void MissingPerProcessMetricMakesOnlyThatProcessUnavailableAndBreaksIdleContinuity(SchedulingProcessMetricMask missing)
    {
        var projector = new HostManagerStrayProcessReportFactProjector();
        Project(projector, 0);
        var snapshot = Processes(901);
        var good = snapshot.Processes[0];
        var bad = good with { ValidMetricMask = good.ValidMetricMask & ~missing };
        var source = projector.Project(Rules, Memory(901), snapshot with { Processes = [bad] },
            new Dictionary<ulong, HostManagerReportTargetDescriptor>(), new HashSet<string>());
        Assert.All(source.Facts, fact => Assert.Null(fact.CurrentValue));
        Assert.Equal(NativeReportSourceStatus.Complete, source.Status);
        Assert.Equal(0, Idle(Project(projector, 902)));
    }

    [Fact]
    public void MissingGpuDatasetOrMemoryNeverFallsBackToCpuOrZeroMemory()
    {
        var projector = new HostManagerStrayProcessReportFactProjector();
        var snapshot = Processes(900);
        var missingGpu = snapshot with { DatasetObservations = snapshot.DatasetObservations.Where(pair => pair.Key != SchedulingProcessMetricMask.GpuUsage).ToDictionary() };
        Assert.All(projector.Project(Rules, Memory(900), missingGpu, new Dictionary<ulong, HostManagerReportTargetDescriptor>(), new HashSet<string>()).Facts,
            fact => Assert.Null(fact.CurrentValue));
        Assert.All(projector.Project(Rules, Memory(901) with { Bars = [] }, Processes(901),
            new Dictionary<ulong, HostManagerReportTargetDescriptor>(), new HashSet<string>()).Facts, fact => Assert.Null(fact.CurrentValue));
    }

    [Fact]
    public void MissingExecutableIsUnavailableAndEmptyVisibleListDisablesCandidates()
    {
        var snapshot = Processes(0);
        var projector = new HostManagerStrayProcessReportFactProjector();
        Assert.All(projector.Project(Rules, Memory(0), snapshot with { Processes = [snapshot.Processes[0] with { ExecutablePath = null }] },
            new Dictionary<ulong, HostManagerReportTargetDescriptor>(), new HashSet<string>()).Facts, fact => Assert.Null(fact.CurrentValue));
        var disabled = Rules.Select(rule => rule.FactKind == HostManagerReportFactKind.ProcessIdleMilliseconds ? rule with { ExecutableNames = [] } : rule).ToArray();
        Assert.Empty(projector.Project(disabled, Memory(0), snapshot, new Dictionary<ulong, HostManagerReportTargetDescriptor>(), new HashSet<string>()).Facts);
    }

    [Fact]
    public void MemoryUsesBreakdownValueRatherThanSchedulingValueAndKeepsBelowThresholdFact()
    {
        var projector = new HostManagerStrayProcessReportFactProjector();
        var source = Project(projector, 0, memoryMiB: 49);
        Assert.Equal(49 * 1024 * 1024, source.Facts.Single(fact => fact.Rule.FactKind == HostManagerReportFactKind.ProcessMemoryBytes).CurrentValue);
        Assert.True(source.Facts[0].CurrentValue < Rules[0].ActivationThreshold);
    }

    [Fact]
    public void RestartWithSamePidGetsNewHandleAndZeroIdleAndExitRemovesHistory()
    {
        var projector = new HostManagerStrayProcessReportFactProjector();
        var old = Project(projector, 0).Facts[0].TargetHandle;
        Assert.Equal(900_000, Idle(Project(projector, 900)));
        var snapshot = Processes(901);
        snapshot = snapshot with { Processes = [snapshot.Processes[0] with { ProcessStartKey = 5678 }] };
        var memory = Memory(901);
        memory = memory with { Bars = [memory.Bars[0] with { Software = [memory.Bars[0].Software[0] with { Processes = [memory.Bars[0].Software[0].Processes[0] with { ProcessStartKey = 5678 }] }] }] };
        var restarted = projector.Project(Rules, memory, snapshot, new Dictionary<ulong, HostManagerReportTargetDescriptor>(), new HashSet<string>());
        Assert.NotEqual(old, restarted.Facts[0].TargetHandle);
        Assert.Equal(0, Idle(restarted));
        var empty = snapshot with { Processes = [], EnumeratedCount = 0, EmittedCount = 0 };
        Assert.Empty(projector.Project(Rules, Memory(902), empty, new Dictionary<ulong, HostManagerReportTargetDescriptor>(), new HashSet<string>()).Facts);
        Assert.Equal(0, Idle(Project(projector, 903)));
    }

    internal static HostManagerReportSourceObservation Project(HostManagerStrayProcessReportFactProjector projector,
        int seconds, double cpu = 0, double gpu = 0, double memoryMiB = 50)
        => projector.Project(Rules, Memory(seconds, memoryMiB), Processes(seconds, cpu, gpu),
            new Dictionary<ulong, HostManagerReportTargetDescriptor>(), new HashSet<string>());

    private static double? Idle(HostManagerReportSourceObservation source)
        => source.Facts.Single(fact => fact.Rule.FactKind == HostManagerReportFactKind.ProcessIdleMilliseconds).CurrentValue;

    [Fact]
    public void UnequalCpuGpuPublicationTimesCannotBackdateTheIdleWindow()
    {
        var projector = new HostManagerStrayProcessReportFactProjector();
        var snapshot = Processes(400);
        var datasets = snapshot.DatasetObservations.ToDictionary();
        datasets[SchedulingProcessMetricMask.GpuUsage] = datasets[SchedulingProcessMetricMask.GpuUsage] with
        { ObservedAtUtcTicks = Origin.UtcTicks };
        snapshot = snapshot with { DatasetObservations = datasets };
        var source = projector.Project(Rules, Memory(400), snapshot, new Dictionary<ulong, HostManagerReportTargetDescriptor>(), new HashSet<string>());
        Assert.Equal(0, Idle(source));
        Assert.Equal(500_000, Idle(Project(projector, 900)));
        Assert.Equal(900_000, Idle(Project(projector, 1300)));
    }

    [Fact]
    public void SourceIdentityReadsOnlyItsOwnDatasetsAndTracksTheirSuccessStatus()
    {
        var projector = new HostManagerStrayProcessReportFactProjector();
        var resources = Memory(0);
        var snapshot = Processes(0);
        var targets = new Dictionary<ulong, HostManagerReportTargetDescriptor>();
        var baseline = projector.Project(Rules, resources, snapshot, targets, new HashSet<string>());
        var unrelated = resources.Datasets[0] with { DatasetId = SamplingDatasetIds.ProcessDiskThroughput, Generation = 999 };
        resources = resources with { CapturedAt = Origin.AddHours(2), Datasets = [.. resources.Datasets, unrelated] };
        snapshot = snapshot with { Generation = 999, ObservedAtUtcTicks = Origin.AddHours(2).UtcTicks };
        var unchanged = projector.Project(Rules, resources, snapshot, targets, new HashSet<string>());
        Assert.Equal(baseline.ProviderGeneration, unchanged.ProviderGeneration);
        Assert.Equal(baseline.ObservedAt, unchanged.ObservedAt);
        var datasets = snapshot.DatasetObservations.ToDictionary();
        datasets[SchedulingProcessMetricMask.GpuUsage] = datasets[SchedulingProcessMetricMask.GpuUsage] with
        { LastSuccessAtUtcTicks = Origin.AddSeconds(1).UtcTicks };
        var changed = projector.Project(Rules, resources, snapshot with { DatasetObservations = datasets }, targets, new HashSet<string>());
        Assert.NotEqual(baseline.ProviderGeneration, changed.ProviderGeneration);
    }

    internal static ResourceBreakdownSnapshot Memory(int seconds, double memoryMiB = 50)
    {
        var time = Origin.AddSeconds(seconds);
        return new ResourceBreakdownSnapshot(time, [new ResourceBreakdownBar(ResourceBreakdownMetricIds.MemoryUsage, "B", ResourceBreakdownScaleModes.Capacity, 1, 1, 1,
            [new ResourceSoftwareSegment("tool", "tool", SoftwareKinds.Other, SoftwareKinds.Other,
                memoryMiB * 1024 * 1024, 1, 1, [new ResourceProcessSegment(424242, "pwsh", @"C:\Tools\pwsh.exe",
                    memoryMiB * 1024 * 1024, 1, 1) { ProcessStartKey = 1234 }])])])
        { Datasets = [new(SamplingDatasetIds.ProcessMemoryUsage, ResourceBreakdownSamplingStatuses.Ready,
            (ulong)(seconds + 1), seconds + 1, time, time, time, time.AddSeconds(60), null, null)] };
    }

    internal static SchedulingProcessFactSnapshot Processes(int seconds, double cpu = 0, double gpu = 0)
    {
        var ticks = Origin.AddSeconds(seconds).UtcTicks;
        var generation = (ulong)(seconds + 1);
        const SchedulingProcessMetricMask mask = SchedulingProcessMetricMask.CpuUsage | SchedulingProcessMetricMask.GpuUsage;
        return new SchedulingProcessFactSnapshot(SamplingObservationStatus.Current, generation, ticks, 1, 1, 0, 0, mask, mask,
            [new SchedulingProcessFact(424242, 1234, "pwsh", @"C:\Tools\pwsh.exe", "tool", "tool", SoftwareKinds.Other,
                SoftwareKinds.Other, 0, mask, cpu, 999999999, generation,
                [new SchedulingProcessGpuFact(0, 1, SchedulingProcessMetricMask.GpuUsage, gpu, 0, generation, 0, 1)])])
        {
            FoundationDatasetObservations = new Dictionary<string, SchedulingProcessFoundationDatasetObservation>
            {
                [SamplingDatasetIds.ProcessInventory] = SchedulingProcessFoundationDatasetObservation.CreateCurrent(SamplingDatasetIds.ProcessInventory, generation, ticks),
                [SamplingDatasetIds.ProcessAttribution] = SchedulingProcessFoundationDatasetObservation.CreateCurrent(SamplingDatasetIds.ProcessAttribution, generation, ticks)
            },
            DatasetObservations = new Dictionary<SchedulingProcessMetricMask, SchedulingProcessDatasetObservation>
            {
                [SchedulingProcessMetricMask.CpuUsage] = SchedulingProcessDatasetObservation.CreateCurrent(SchedulingProcessMetricMask.CpuUsage, generation, ticks, generation, ticks),
                [SchedulingProcessMetricMask.GpuUsage] = SchedulingProcessDatasetObservation.CreateCurrent(SchedulingProcessMetricMask.GpuUsage, generation, ticks, generation, ticks, topologyGeneration: 1, topologyFingerprint: 1)
            }
        };
    }
}
