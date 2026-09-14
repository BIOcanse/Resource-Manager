using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Infrastructure.PublicResources;

namespace Resource_Manager_APP.Tests;

public sealed class HostPublicResourceCapacitySampleFreshnessTrackerTests
{
    [Fact]
    public void MemoryFreshnessUsesOnlyMemoryDatasetLineage()
    {
        var tracker = new HostPublicResourceCapacitySampleFreshnessTracker();
        var first = CreateSnapshot(memoryGeneration: 1, memoryObservedAtSecond: 1);

        Assert.True(tracker.Observe(first).Memory);
        Assert.False(tracker.Observe(first).Memory);
        Assert.False(tracker.Observe(first with
        {
            CapturedAt = first.CapturedAt.AddSeconds(1),
            CommittedGeneration = 99
        }).Memory);
        Assert.True(tracker.Observe(CreateSnapshot(
            memoryGeneration: 2,
            memoryObservedAtSecond: 2)).Memory);
    }

    [Fact]
    public void RetainedMemoryDoesNotAdvanceOrReplaceCurrentStamp()
    {
        var tracker = new HostPublicResourceCapacitySampleFreshnessTracker();
        Assert.True(tracker.Observe(CreateSnapshot(1, 1)).Memory);
        var retained = CreateSnapshot(2, 2) with
        {
            Datasets = new Dictionary<string, HardwareMetricDatasetObservation>(
                StringComparer.OrdinalIgnoreCase)
            {
                [SamplingDatasetIds.SystemMemoryUsage] = Observation(
                    SamplingDatasetIds.SystemMemoryUsage,
                    SamplingObservationStatus.RetainedLastGood,
                    1,
                    1)
            }
        };

        Assert.False(tracker.Observe(retained).Memory);
        Assert.True(tracker.Observe(CreateSnapshot(2, 2)).Memory);
    }

    [Fact]
    public void VideoMemoryFreshnessIsIndependentFromMemoryAndGpuUsage()
    {
        var tracker = new HostPublicResourceCapacitySampleFreshnessTracker();
        var first = CreateSnapshot(1, 1, gpuCapacityGeneration: 10);
        var firstFreshness = tracker.Observe(first);
        Assert.True(firstFreshness.Memory);
        Assert.True(firstFreshness.VideoMemory);

        var unrelated = first with
        {
            CapturedAt = first.CapturedAt.AddSeconds(1),
            CommittedGeneration = 11,
            Datasets = new Dictionary<string, HardwareMetricDatasetObservation>(
                first.Datasets,
                StringComparer.OrdinalIgnoreCase)
            {
                [SamplingDatasetIds.SystemCpuUsage] = Observation(
                    SamplingDatasetIds.SystemCpuUsage,
                    SamplingObservationStatus.Current,
                    11,
                    2)
            }
        };
        var unrelatedFreshness = tracker.Observe(unrelated);
        Assert.False(unrelatedFreshness.Memory);
        Assert.False(unrelatedFreshness.VideoMemory);

        var capacityAdvanced = CreateSnapshot(1, 1, gpuCapacityGeneration: 11);
        var advancedFreshness = tracker.Observe(capacityAdvanced);
        Assert.False(advancedFreshness.Memory);
        Assert.True(advancedFreshness.VideoMemory);
    }

    private static HardwareMetricSnapshot CreateSnapshot(
        ulong memoryGeneration,
        int memoryObservedAtSecond,
        ulong gpuCapacityGeneration = 0)
    {
        var capturedAt = DateTimeOffset.UnixEpoch.AddSeconds(
            Math.Max(memoryObservedAtSecond, (int)gpuCapacityGeneration));
        var datasets = new Dictionary<string, HardwareMetricDatasetObservation>(
            StringComparer.OrdinalIgnoreCase)
        {
            [SamplingDatasetIds.SystemMemoryUsage] = Observation(
                SamplingDatasetIds.SystemMemoryUsage,
                SamplingObservationStatus.Current,
                memoryGeneration,
                memoryObservedAtSecond)
        };
        var inventory = new SchedulingGpuInventorySnapshot(
            SamplingObservationStatus.NotRequested,
            0,
            0,
            0,
            0,
            0,
            0,
            []);
        if (gpuCapacityGeneration != 0)
        {
            var observedAt = checked((long)gpuCapacityGeneration * TimeSpan.TicksPerSecond);
            var adapter = new SchedulingGpuAdapterObservation(
                0,
                100,
                SchedulingGpuCapabilityMask.Usage
                    | SchedulingGpuCapabilityMask.DedicatedMemory,
                SchedulingGpuMetricMask.Usage
                    | SchedulingGpuMetricMask.UsedDedicatedMemory
                    | SchedulingGpuMetricMask.TotalDedicatedMemory,
                SamplingObservationStatus.Current,
                SamplingObservationStatus.Current,
                50,
                1,
                2,
                gpuCapacityGeneration,
                observedAt);
            inventory = new SchedulingGpuInventorySnapshot(
                SamplingObservationStatus.Current,
                gpuCapacityGeneration,
                observedAt,
                1,
                0,
                0,
                123,
                [adapter]);
            datasets[SamplingDatasetIds.SystemGpuInventory] = Observation(
                SamplingDatasetIds.SystemGpuInventory,
                SamplingObservationStatus.Current,
                gpuCapacityGeneration,
                checked((int)gpuCapacityGeneration));
            datasets["gpu.0.vram"] = Observation(
                "gpu.0.vram",
                SamplingObservationStatus.Current,
                gpuCapacityGeneration,
                checked((int)gpuCapacityGeneration));
        }

        return new HardwareMetricSnapshot(
            capturedAt,
            null!,
            null!,
            null!,
            [],
            inventory,
            new Dictionary<string, MetricValue>())
        {
            Datasets = datasets,
            WorkspaceIdentity = 1,
            ConfigurationGeneration = 1,
            CatalogGeneration = 1,
            CommittedGeneration = Math.Max(memoryGeneration, gpuCapacityGeneration)
        };
    }

    private static HardwareMetricDatasetObservation Observation(
        string datasetId,
        SamplingObservationStatus status,
        ulong generation,
        int observedAtSecond)
        => new(
            datasetId,
            status,
            generation,
            DateTimeOffset.UnixEpoch.AddSeconds(observedAtSecond).UtcTicks,
            1,
            1,
            1,
            generation)
        {
            LastAttemptAtUtcTicks = DateTimeOffset.UnixEpoch
                .AddSeconds(observedAtSecond)
                .UtcTicks,
            LastSuccessAtUtcTicks = DateTimeOffset.UnixEpoch
                .AddSeconds(observedAtSecond)
                .UtcTicks
        };
}
