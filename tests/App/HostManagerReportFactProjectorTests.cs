using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Domain.SystemHealth;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization.Reports;

namespace Resource_Manager_APP.Tests;

public sealed class HostManagerReportFactProjectorTests
{
    [Fact]
    public void CompleteCpuAndInterruptSnapshotsProjectOnlyConfiguredFacts()
    {
        var plan = HostManagerTestPlanFactory.CreatePlan().ReportCoordinator;
        var capturedAt = DateTimeOffset.UtcNow;

        var sources = HostManagerReportFactProjector.Project(
            plan,
            CreateHardware(
                capturedAt,
                CpuMetricObservationStatus.Complete,
                sourceGeneration: 7),
            CreateInterrupts(capturedAt, available: true, sourceGeneration: 9));

        var cpu = Assert.Single(sources, source =>
            source.Facts.Any(fact =>
                fact.Rule.FactKind
                    == HostManagerReportFactKind.CpuTemperatureCelsius));
        Assert.Equal(NativeReportSourceStatus.Complete, cpu.Status);
        Assert.Equal(7, cpu.ProviderGeneration);
        Assert.Equal(1000, cpu.SampleDurationMilliseconds);
        Assert.Equal(3, cpu.Facts.Count);
        Assert.All(cpu.Facts, static fact => Assert.Null(fact.EventCount));

        var interrupts = Assert.Single(sources, source =>
            source.Facts.Any(fact =>
                fact.Rule.FactKind
                    == HostManagerReportFactKind.InterruptCpuCapacityPercent));
        Assert.Equal(NativeReportSourceStatus.Complete, interrupts.Status);
        Assert.Equal(9, interrupts.ProviderGeneration);
        Assert.Equal(60_000, interrupts.SampleDurationMilliseconds);
        Assert.Equal(2, interrupts.Facts.Count);

    }

    [Fact]
    public void UnknownAbsoluteFrequenciesDoNotDiscardValidCpuFacts()
    {
        var plan = HostManagerTestPlanFactory.CreatePlan().ReportCoordinator;
        var capturedAt = DateTimeOffset.UtcNow;

        var sources = HostManagerReportFactProjector.Project(
            plan,
            CreateHardware(
                capturedAt,
                CpuMetricObservationStatus.Complete,
                sourceGeneration: 13,
                currentFrequencyMhz: 0,
                maxFrequencyMhz: 0),
            CreateInterrupts(
                capturedAt,
                available: false,
                sourceGeneration: 0));

        var cpu = Assert.Single(
            sources,
            source => source.ProviderGeneration == 13);
        Assert.Equal(NativeReportSourceStatus.Complete, cpu.Status);
        Assert.Equal(3, cpu.Facts.Count);
        Assert.Contains(
            cpu.Facts,
            fact => fact.Rule.FactKind
                == HostManagerReportFactKind.CpuFrequencyPercent
                && fact.CurrentValue == 60);
    }

    [Fact]
    public void WarmingAndIncompleteSourcesNeverInventFacts()
    {
        var plan = HostManagerTestPlanFactory.CreatePlan().ReportCoordinator;
        var capturedAt = DateTimeOffset.UtcNow;

        var sources = HostManagerReportFactProjector.Project(
            plan,
            CreateHardware(
                capturedAt,
                CpuMetricObservationStatus.Warming,
                sourceGeneration: 11),
            CreateInterrupts(
                capturedAt,
                available: true,
                sourceGeneration: 12,
                eventsLost: 1,
                providerState: "Unavailable"));

        var cpu = Assert.Single(
            sources,
            source => source.ProviderGeneration == 11);
        Assert.Equal(NativeReportSourceStatus.Skipped, cpu.Status);
        Assert.Empty(cpu.Facts);

        var interrupts = Assert.Single(
            sources,
            source => source.ProviderGeneration == 12);
        Assert.Equal(NativeReportSourceStatus.Unavailable, interrupts.Status);
        Assert.Empty(interrupts.Facts);
    }

    [Fact]
    public void CompleteRecoveredInterruptWindowAcceptsHistoricalCumulativeLoss()
    {
        var plan = HostManagerTestPlanFactory.CreatePlan().ReportCoordinator;
        var capturedAt = DateTimeOffset.UtcNow;
        var sources = HostManagerReportFactProjector.Project(plan,
            CreateHardware(capturedAt, CpuMetricObservationStatus.Warming, sourceGeneration: 11),
            CreateInterrupts(capturedAt, available: true, sourceGeneration: 12, eventsLost: 4));

        var interrupts = Assert.Single(sources, source => source.ProviderGeneration == 12);
        Assert.Equal(NativeReportSourceStatus.Complete, interrupts.Status);
        Assert.Equal(2, interrupts.Facts.Count);
    }

    [Fact]
    public void SamplesShorterThanTheRuleMinimumAreSkippedWithoutFacts()
    {
        var plan = HostManagerTestPlanFactory.CreatePlan().ReportCoordinator;
        var capturedAt = DateTimeOffset.UtcNow;

        var sources = HostManagerReportFactProjector.Project(
            plan,
            CreateHardware(
                capturedAt,
                CpuMetricObservationStatus.Complete,
                sourceGeneration: 17,
                sampleDurationMilliseconds: 250),
            CreateInterrupts(
                capturedAt,
                available: true,
                sourceGeneration: 18,
                window: TimeSpan.FromMilliseconds(250)));

        Assert.All(sources, source =>
        {
            Assert.Equal(NativeReportSourceStatus.Skipped, source.Status);
            Assert.Empty(source.Facts);
        });
    }

    [Fact]
    public void CurrentSoftwareMemorySnapshotProjectsOnlyEligibleSoftwareTargets()
    {
        var plan = HostManagerTestPlanFactory.CreatePlan().ReportCoordinator;
        var capturedAt = DateTimeOffset.UtcNow;
        var resources = new ResourceBreakdownSnapshot(
            capturedAt,
            [
                new ResourceBreakdownBar(
                    ResourceBreakdownMetricIds.MemoryUsage,
                    "B",
                    ResourceBreakdownScaleModes.Capacity,
                    4_000,
                    10_000,
                    40,
                    [
                        CreateSoftware(
                            "software:editor",
                            "Editor",
                            SoftwareKinds.Other,
                            18,
                            101),
                        CreateSoftware(
                            "software:game",
                            "Game",
                            SoftwareKinds.Game,
                            22,
                            102)
                    ])
            ])
        {
            Datasets = [new ResourceBreakdownDatasetSamplingState(SamplingDatasetIds.ProcessMemoryUsage, ResourceBreakdownSamplingStatuses.Ready, 31, 31, capturedAt, capturedAt, capturedAt, null, null, null)],
            Sampling = new ResourceBreakdownSamplingState(
                ResourceBreakdownSamplingStatuses.Ready,
                31,
                capturedAt,
                capturedAt,
                null,
                null)
        };

        var batch = HostManagerReportFactProjector.Project(
            plan,
            hardware: null,
            CreateInterrupts(capturedAt, available: false, sourceGeneration: 0),
            resources, new HashSet<string> { "software:editor", "software:game" });

        var softwareSource = Assert.Single(
            batch.Sources,
            source => source.Facts.Any(fact =>
                fact.Rule.FactKind
                    == HostManagerReportFactKind.SoftwareMemorySystemPercent));
        // 每个合格软件同时供两种事实：占系统内存的百分比，和占用的绝对字节。
        // 两条规则各比各的阈值，谁先越线谁出报告。
        Assert.Equal(31, softwareSource.ProviderGeneration);
        var fact = Assert.Single(
            softwareSource.Facts,
            candidate => candidate.Rule.FactKind
                == HostManagerReportFactKind.SoftwareMemorySystemPercent);
        var bytesFact = Assert.Single(
            softwareSource.Facts,
            candidate => candidate.Rule.FactKind
                == HostManagerReportFactKind.SoftwareMemoryBytes);
        Assert.Equal(18, fact.CurrentValue);
        Assert.Equal(1_800, bytesFact.CurrentValue);
        Assert.Equal(fact.TargetHandle, bytesFact.TargetHandle);
        Assert.NotEqual(softwareSource.CoverageScopeHandle, fact.TargetHandle);

        var target = Assert.Single(batch.Targets).Value;
        Assert.Equal(fact.TargetHandle, target.TargetHandle);
        Assert.Equal("software:editor", target.SoftwareId);
        Assert.Equal("Editor", target.SoftwareName);
        Assert.Equal([101], target.ProcessIds);
        Assert.DoesNotContain(
            batch.Targets.Values,
            static candidate => candidate.SoftwareId == "software:game");
    }

    [Fact]
    public void RealComposedCpuSnapshotUsesCurrentDatasetsDespiteUnavailableLegacyProvider()
    {
        var now = DateTimeOffset.UtcNow;
        using var publisher = new ResourceManager.App.Infrastructure.Monitoring.LastSuccessfulHardwareMetricSnapshot();
        var request = MetricSampleRequest.ForIds(["cpu.usage", "cpu.temperature", "cpu.frequencyPercent"]);
        var result = publisher.Publish(CreateHardware(now, CpuMetricObservationStatus.Complete, 7), request);
        Assert.Equal(3, result.CommittedDatasetCount);
        var composed = Assert.IsType<HardwareMetricSnapshot>(publisher.Read(now));
        Assert.Equal(HardwareSensorProviderStates.Unavailable, composed.Cpu.Sensors.ProviderState.State);
        var facts = HostManagerReportFactProjector.Project(HostManagerTestPlanFactory.CreatePlan().ReportCoordinator,
            composed, CreateInterrupts(now, false, 0));
        Assert.Equal(3, Assert.Single(facts, source => source.ProviderGeneration == 7).Facts.Count);
    }

    [Theory]
    [InlineData("cpu.temperature")]
    [InlineData("cpu.frequencyPercent")]
    [InlineData("cpu.usage")]
    public void MissingRequiredCpuPublicationCannotInventThermalEvidence(string metricId)
    {
        var now = DateTimeOffset.UtcNow;
        var hardware = CreateHardware(now, CpuMetricObservationStatus.Complete, 7);
        hardware = hardware with { Datasets = hardware.Datasets.Where(item => item.Key != MetricDatasetIds.ForSystemMetric(metricId))
            .ToDictionary(item => item.Key, item => item.Value) };
        var sources = HostManagerReportFactProjector.Project(HostManagerTestPlanFactory.CreatePlan().ReportCoordinator,
            hardware, CreateInterrupts(now, false, 0));
        Assert.Empty(Assert.Single(sources, source => source.ProviderGeneration == 7).Facts);
    }

    [Fact]
    public void MemoryPublicationIdentityAndCandidateExclusionsAreIndependentOfWholeTableRevision()
    {
        var now = DateTimeOffset.UtcNow;
        var entries = new[] {
            CreateSoftware("background", "Background", SoftwareKinds.Other, 20, 101),
            CreateSoftware("foreground", "Foreground", SoftwareKinds.Other, 20, 102),
            CreateSoftware("protected", "Protected", SoftwareKinds.Other, 20, 103),
            CreateSoftware("game", "Game", SoftwareKinds.Game, 20, 104),
            CreateSoftware("resource-manager:self", "Self", SoftwareKinds.Adapted, 20, 105) };
        var resources = new ResourceBreakdownSnapshot(now, [new ResourceBreakdownBar(
            ResourceBreakdownMetricIds.MemoryUsage, "B", ResourceBreakdownScaleModes.Capacity, 100, 100, 100, entries)])
        {
            Datasets = [new ResourceBreakdownDatasetSamplingState(SamplingDatasetIds.ProcessMemoryUsage,
                ResourceBreakdownSamplingStatuses.Ready, 77, 100, now, now, now, null, null, null)]
        };
        var rules = HostManagerTestPlanFactory.CreatePlan().ReportCoordinator.Recreate.Rules
            .Where(rule => HostManagerSoftwareMemoryReportFactProjector.Supports(rule.FactKind)).ToArray();
        var backgrounds = new HashSet<string> { "background", "protected", "game", "resource-manager:self" };
        var targets = new Dictionary<ulong, HostManagerReportTargetDescriptor>();
        var first = HostManagerSoftwareMemoryReportFactProjector.Project(14, 14, rules, resources, targets,
            backgrounds, new HashSet<string> { "protected" });
        Assert.Equal("background", Assert.Single(targets).Value.SoftwareId);
        Assert.Equal(77, first.ProviderGeneration);
        var second = HostManagerSoftwareMemoryReportFactProjector.Project(14, 14, rules,
            resources with { CapturedAt = now.AddSeconds(1), Sampling = resources.Sampling with { StateRevision = 999, Status = ResourceBreakdownSamplingStatuses.Failed } },
            targets, backgrounds, new HashSet<string> { "protected" });
        Assert.Equal(first.ProviderGeneration, second.ProviderGeneration);
        Assert.Equal(first.ObservedAt, second.ObservedAt);
        var missingRuntime = HostManagerSoftwareMemoryReportFactProjector.Project(14, 14, rules, resources, targets);
        Assert.Equal(NativeReportSourceStatus.Unavailable, missingRuntime.Status);
        Assert.Empty(missingRuntime.Facts);
    }

    private static HardwareMetricSnapshot CreateHardware(
        DateTimeOffset capturedAt,
        CpuMetricObservationStatus status,
        ulong sourceGeneration,
        int currentFrequencyMhz = 3000,
        int maxFrequencyMhz = 5000,
        long sampleDurationMilliseconds = 1000)
    {
        return new HardwareMetricSnapshot(
            capturedAt,
            new CpuMetrics(
                "CPU",
                95,
                IsUsageAvailable: true,
                status,
                sourceGeneration,
                SampleDurationMilliseconds: sampleDurationMilliseconds,
                CurrentFrequencyMhz: currentFrequencyMhz,
                MaxFrequencyMhz: maxFrequencyMhz,
                FrequencyPercent: 60,
                FrequencySource: "Windows effective frequency",
                new CpuSensorMetrics(
                    new HardwareSensorProviderState("test", HardwareSensorProviderStates.Unavailable, null),
                    PackagePowerWatts: 80,
                    CoreVoltageVolts: 1,
                    PackageCurrentAmps: 70,
                    TemperatureCelsius: 96)),
            new MemoryMetrics(UsedBytes: 1, TotalBytes: 2, UsagePercent: 50, IsUsageAvailable: true),
            new VirtualMemoryMetrics(UsedBytes: 0, TotalBytes: 0, UsagePercent: 0, IsSelectable: false),
            [],
            new SchedulingGpuInventorySnapshot(
                SamplingObservationStatus.NotRequested,
                0,
                0,
                0,
                0,
                0,
                0,
                []),
            new Dictionary<string, MetricReadingValue>())
        {
            Datasets = new[] { MetricDatasetIds.SystemCpuUsage, MetricDatasetIds.ForSystemMetric("cpu.temperature"), MetricDatasetIds.ForSystemMetric("cpu.frequencyPercent") }
                .ToDictionary(id => id, id => new HardwareMetricDatasetObservation(id, SamplingObservationStatus.Current, sourceGeneration, capturedAt.UtcTicks, 1, 1, 1, sourceGeneration))
        };
    }

    private static SystemInterruptSnapshot CreateInterrupts(
        DateTimeOffset capturedAt,
        bool available,
        long sourceGeneration,
        int eventsLost = 0,
        TimeSpan? window = null,
        string providerState = SystemInterruptProviderStates.Running)
    {
        return new SystemInterruptSnapshot(
            capturedAt,
            available,
            sourceGeneration,
            window ?? TimeSpan.FromSeconds(60),
            16,
            8,
            2,
            SystemInterruptEventKinds.Dpc,
            "driver.sys",
            5,
            3,
            0.001,
            [],
            [],
            new SystemInterruptProviderState(
                "etw-system-interrupts",
                providerState,
                "test",
                eventsLost));
    }

    private static ResourceSoftwareSegment CreateSoftware(
        string softwareId,
        string name,
        string kind,
        double systemPercent,
        int processId)
    {
        return new ResourceSoftwareSegment(
            softwareId,
            name,
            kind,
            kind,
            systemPercent * 100,
            systemPercent,
            1,
            [
                new ResourceProcessSegment(
                    processId,
                    $"{name}.exe",
                    $@"C:\Tools\{name}.exe",
                    systemPercent * 100,
                    systemPercent,
                    100)
            ]);
    }
}
