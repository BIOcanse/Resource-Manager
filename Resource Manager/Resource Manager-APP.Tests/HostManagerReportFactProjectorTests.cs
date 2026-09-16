using ResourceManager.App.Domain.Metrics;
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
    public void CompleteCpuAndInterruptSnapshotsProjectSixExactFacts()
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
        Assert.Equal(3, interrupts.Facts.Count);
        Assert.Equal(
            3UL,
            interrupts.Facts.Single(fact =>
                fact.Rule.FactKind
                    == HostManagerReportFactKind
                        .InterruptEventsAtOrAboveOneMillisecond)
                .EventCount);
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
                eventsLost: 1));

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
                    "Memory",
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
            resources);

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
                    new HardwareSensorProviderState("test", "Active", null),
                    PackagePowerWatts: 80,
                    CoreVoltageVolts: 1,
                    PackageCurrentAmps: 70,
                    TemperatureCelsius: 96)),
            new MemoryMetrics(1, 2, 50, true, "DDR5"),
            new VirtualMemoryMetrics(0, 0, 0, string.Empty, false),
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
            new Dictionary<string, MetricValue>());
    }

    private static SystemInterruptSnapshot CreateInterrupts(
        DateTimeOffset capturedAt,
        bool available,
        long sourceGeneration,
        int eventsLost = 0,
        TimeSpan? window = null)
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
                "Running",
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
