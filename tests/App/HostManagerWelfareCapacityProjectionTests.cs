using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization;

namespace Resource_Manager_APP.Tests;

public sealed class HostManagerWelfareCapacityProjectionTests
{
    [Fact]
    public void ProjectionUsesTheFourIndependentCurrentBackendValues()
    {
        var runtimeFacts = CreateRuntimeFacts();
        var capacity = HostManagerSmartCoordinator.CreateWelfareCapacityInput(
            CreateHardware(includeCpuDataset: true),
            runtimeFacts);

        Assert.Equal(0.75, capacity.CpuFreeRatio, precision: 10);
        Assert.Equal(0.6, capacity.GpuFreeRatio, precision: 10);
        Assert.Equal(0.625, capacity.VramFreeRatio, precision: 10);
        Assert.Equal(0.5, capacity.MemoryFreeRatio, precision: 10);
        Assert.Equal(2U, capacity.EligibleProcessCount);
        Assert.NotEqual(0UL, capacity.PublicationFingerprint);
    }

    [Fact]
    public void MissingCurrentValueZerosOnlyThisRoundsWelfareInput()
    {
        var capacity = HostManagerSmartCoordinator.CreateWelfareCapacityInput(
            CreateHardware(includeCpuDataset: false),
            CreateRuntimeFacts());

        Assert.Equal(0D, capacity.CpuFreeRatio);
        Assert.Equal(0.6, capacity.GpuFreeRatio, precision: 10);
        Assert.Equal(0.625, capacity.VramFreeRatio, precision: 10);
        Assert.Equal(0.5, capacity.MemoryFreeRatio, precision: 10);
        Assert.Equal(2U, capacity.EligibleProcessCount);
    }

    [Theory]
    [InlineData("system.gpu.inventory", 0, 0)]
    [InlineData("gpu.1.usage", 0, 0.625)]
    [InlineData("gpu.1.vram", 0.6, 0)]
    public void MissingGpuPublicationZerosOnlyItsWelfareInput(
        string datasetId,
        double expectedGpuFreeRatio,
        double expectedVramFreeRatio)
    {
        var hardware = CreateHardware(includeCpuDataset: true);
        var datasets = hardware.Datasets
            .Where(pair => !pair.Key.Equals(datasetId, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(
                static pair => pair.Key,
                static pair => pair.Value,
                StringComparer.OrdinalIgnoreCase);

        var capacity = HostManagerSmartCoordinator.CreateWelfareCapacityInput(
            hardware with { Datasets = datasets },
            CreateRuntimeFacts());

        Assert.Equal(0.75, capacity.CpuFreeRatio, precision: 10);
        Assert.Equal(0.5, capacity.MemoryFreeRatio, precision: 10);
        Assert.Equal(expectedGpuFreeRatio, capacity.GpuFreeRatio, precision: 10);
        Assert.Equal(expectedVramFreeRatio, capacity.VramFreeRatio, precision: 10);
    }

    [Fact]
    public void CurrentZeroAndUnavailablePublicationHaveDifferentFingerprints()
    {
        var hardware = CreateHardware(includeCpuDataset: true);
        var currentZero = HostManagerSmartCoordinator.CreateWelfareCapacityInput(
            hardware with
            {
                Cpu = hardware.Cpu with { UsagePercent = 100 }
            },
            CreateRuntimeFacts());
        var datasets = hardware.Datasets.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value,
            StringComparer.OrdinalIgnoreCase);
        var cpu = datasets[SamplingDatasetIds.SystemCpuUsage];
        datasets[SamplingDatasetIds.SystemCpuUsage] = cpu with
        {
            Status = SamplingObservationStatus.Unavailable,
            SourceGeneration = 0,
            ObservedAtUtcTicks = 0,
            CommittedGeneration = 0,
            LastAttemptAtUtcTicks = checked(cpu.LastAttemptAtUtcTicks + 1)
        };
        var unavailable = HostManagerSmartCoordinator.CreateWelfareCapacityInput(
            hardware with { Datasets = datasets },
            CreateRuntimeFacts());

        Assert.Equal(0, currentZero.CpuFreeRatio);
        Assert.Equal(0, unavailable.CpuFreeRatio);
        Assert.NotEqual(
            currentZero.PublicationFingerprint,
            unavailable.PublicationFingerprint);
    }

    [Fact]
    public void SameValuesFromANewBackendPublicationHaveANewFingerprint()
    {
        var runtimeFacts = CreateRuntimeFacts();
        var first = HostManagerSmartCoordinator.CreateWelfareCapacityInput(
            CreateHardware(includeCpuDataset: true, memorySourceGeneration: 7),
            runtimeFacts);
        var second = HostManagerSmartCoordinator.CreateWelfareCapacityInput(
            CreateHardware(includeCpuDataset: true, memorySourceGeneration: 8),
            runtimeFacts);

        Assert.Equal(first.CpuFreeRatio, second.CpuFreeRatio);
        Assert.Equal(first.GpuFreeRatio, second.GpuFreeRatio);
        Assert.Equal(first.VramFreeRatio, second.VramFreeRatio);
        Assert.Equal(first.MemoryFreeRatio, second.MemoryFreeRatio);
        Assert.NotEqual(first.PublicationFingerprint, second.PublicationFingerprint);
    }

    [Fact]
    public void EveryIndependentWelfarePublicationContributesToTheFingerprint()
    {
        var runtimeFacts = CreateRuntimeFacts();
        var hardware = CreateHardware(includeCpuDataset: true);
        var baseline = HostManagerSmartCoordinator.CreateWelfareCapacityInput(
            hardware,
            runtimeFacts);
        string[] datasetIds =
        [
            SamplingDatasetIds.SystemCpuUsage,
            SamplingDatasetIds.SystemMemoryUsage,
            SamplingDatasetIds.SystemGpuInventory,
            "gpu.0.usage",
            "gpu.0.vram",
            "gpu.1.usage",
            "gpu.1.vram"
        ];

        foreach (var datasetId in datasetIds)
        {
            var datasets = hardware.Datasets.ToDictionary(
                static pair => pair.Key,
                static pair => pair.Value,
                StringComparer.OrdinalIgnoreCase);
            var current = datasets[datasetId];
            datasets[datasetId] = current with
            {
                SourceGeneration = checked(current.SourceGeneration + 1),
                ObservedAtUtcTicks = checked(current.ObservedAtUtcTicks + 1),
                CommittedGeneration = checked(current.CommittedGeneration + 1),
                LastAttemptAtUtcTicks = checked(current.LastAttemptAtUtcTicks + 1)
            };
            var changed = HostManagerSmartCoordinator.CreateWelfareCapacityInput(
                hardware with { Datasets = datasets },
                runtimeFacts);

            Assert.Equal(baseline.CpuFreeRatio, changed.CpuFreeRatio);
            Assert.Equal(baseline.GpuFreeRatio, changed.GpuFreeRatio);
            Assert.Equal(baseline.VramFreeRatio, changed.VramFreeRatio);
            Assert.Equal(baseline.MemoryFreeRatio, changed.MemoryFreeRatio);
            Assert.NotEqual(baseline.PublicationFingerprint, changed.PublicationFingerprint);
        }
    }

    [Fact]
    public void PublicationFingerprintIsIndependentOfDictionaryAndAdapterEnumerationOrder()
    {
        var runtimeFacts = CreateRuntimeFacts();
        var hardware = CreateHardware(includeCpuDataset: true);
        var reorderedDatasets = hardware.Datasets
            .Reverse()
            .ToDictionary(
                static pair => pair.Key,
                static pair => pair.Value,
                StringComparer.OrdinalIgnoreCase);
        var reordered = hardware with
        {
            Datasets = reorderedDatasets,
            GpuInventory = hardware.GpuInventory with
            {
                Adapters = hardware.GpuInventory.Adapters.Reverse().ToArray()
            }
        };

        var first = HostManagerSmartCoordinator.CreateWelfareCapacityInput(hardware, runtimeFacts);
        var second = HostManagerSmartCoordinator.CreateWelfareCapacityInput(reordered, runtimeFacts);

        Assert.Equal(first, second);
    }

    private static HardwareMetricSnapshot CreateHardware(
        bool includeCpuDataset,
        ulong memorySourceGeneration = 7)
    {
        var observedAt = new DateTimeOffset(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);
        var ticks = observedAt.UtcTicks;
        var datasets = new Dictionary<string, HardwareMetricDatasetObservation>(
            StringComparer.OrdinalIgnoreCase)
        {
            [SamplingDatasetIds.SystemMemoryUsage] = CreateDataset(
                SamplingDatasetIds.SystemMemoryUsage,
                ticks,
                memorySourceGeneration),
            [SamplingDatasetIds.SystemGpuInventory] = CreateDataset(
                SamplingDatasetIds.SystemGpuInventory,
                ticks,
                9),
            ["gpu.0.usage"] = CreateDataset("gpu.0.usage", ticks, 10),
            ["gpu.0.vram"] = CreateDataset("gpu.0.vram", ticks, 11),
            ["gpu.1.usage"] = CreateDataset("gpu.1.usage", ticks, 12),
            ["gpu.1.vram"] = CreateDataset("gpu.1.vram", ticks, 13)
        };
        if (includeCpuDataset)
        {
            datasets[SamplingDatasetIds.SystemCpuUsage] = CreateDataset(
                SamplingDatasetIds.SystemCpuUsage,
                ticks,
                7);
        }

        return new HardwareMetricSnapshot(
            observedAt,
            new CpuMetrics(
                "CPU",
                25,
                true,
                CpuMetricObservationStatus.Complete,
                7,
                5_000,
                0,
                0,
                0,
                string.Empty,
                null!),
            new MemoryMetrics(8, 16, 50, true, string.Empty)
            {
                ObservationStatus = SamplingObservationStatus.Current
            },
            null!,
            [],
            new SchedulingGpuInventorySnapshot(
                SamplingObservationStatus.Current,
                9,
                ticks,
                2,
                0,
                0,
                99,
                [
                    CreateAdapter(0, 11, 20, 2, 8, ticks),
                    CreateAdapter(1, 22, 40, 4, 8, ticks)
                ]),
            new Dictionary<string, MetricValue>())
        {
            Datasets = datasets
        };
    }

    private static HardwareMetricDatasetObservation CreateDataset(
        string datasetId,
        long observedAtUtcTicks,
        ulong sourceGeneration)
        => new(
            datasetId,
            SamplingObservationStatus.Current,
            sourceGeneration,
            observedAtUtcTicks,
            1,
            1,
            1,
            sourceGeneration)
        {
            LastAttemptAtUtcTicks = observedAtUtcTicks
        };

    private static SchedulingGpuAdapterObservation CreateAdapter(
        int index,
        ulong adapterKey,
        double usagePercent,
        ulong usedMemory,
        ulong totalMemory,
        long observedAtUtcTicks)
        => new(
            index,
            adapterKey,
            SchedulingGpuCapabilityMask.Usage |
                SchedulingGpuCapabilityMask.DedicatedMemory,
            SchedulingGpuMetricMask.Usage |
                SchedulingGpuMetricMask.UsedDedicatedMemory |
                SchedulingGpuMetricMask.TotalDedicatedMemory,
            SamplingObservationStatus.Current,
            SamplingObservationStatus.Current,
            usagePercent,
            usedMemory,
            totalMemory,
            9,
            observedAtUtcTicks);

    private static IReadOnlyDictionary<HostManagerComputeProcessIdentity, HostManagerComputeRuntimeFact>
        CreateRuntimeFacts()
        => new Dictionary<HostManagerComputeProcessIdentity, HostManagerComputeRuntimeFact>
        {
            [new HostManagerComputeProcessIdentity(10, 100)] = new(
                NativeComputeScoringRuntimeState.ForegroundFocused,
                1,
                1),
            [new HostManagerComputeProcessIdentity(20, 200)] = new(
                NativeComputeScoringRuntimeState.BackgroundProcess,
                1,
                1),
            [new HostManagerComputeProcessIdentity(30, 300)] = new(
                NativeComputeScoringRuntimeState.NotRunning,
                1,
                1)
        };
}
