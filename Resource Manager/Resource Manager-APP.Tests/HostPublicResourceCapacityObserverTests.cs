using ResourceManager.Adapter;
using ResourceManager.Adapter.SharedMemory;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.PublicResources;

namespace Resource_Manager_APP.Tests;

public sealed class HostPublicResourceCapacityObserverTests
{
    [Fact]
    public void MemoryObservation_DistinguishesDisabledUnknownShortageAndHealthy()
    {
        var cleanup = HostManagerTestPlanFactory.MemoryCleanup;

        AssertState(
            HostPublicResourceCapacityObservationState.Disabled,
            HostPublicResourceCapacityObserver.ObserveMemory(
                executionEnabled: false,
                cleanup,
                memoryFreeRatio: 1,
                memoryFreeRatioCurrent: true));
        AssertState(
            HostPublicResourceCapacityObservationState.UnknownOrStale,
            HostPublicResourceCapacityObserver.ObserveMemory(
                executionEnabled: true,
                CompiledHostManagerMemoryCleanupHotPublishPlan.Unpublished,
                memoryFreeRatio: 1,
                memoryFreeRatioCurrent: true));
        AssertState(
            HostPublicResourceCapacityObservationState.UnknownOrStale,
            HostPublicResourceCapacityObserver.ObserveMemory(
                executionEnabled: true,
                cleanup,
                memoryFreeRatio: 0,
                memoryFreeRatioCurrent: false));
        AssertState(
            HostPublicResourceCapacityObservationState.UnknownOrStale,
            HostPublicResourceCapacityObserver.ObserveMemory(
                executionEnabled: true,
                cleanup,
                memoryFreeRatio: double.NaN,
                memoryFreeRatioCurrent: true));
        AssertState(
            HostPublicResourceCapacityObservationState.ShortageFresh,
            HostPublicResourceCapacityObserver.ObserveMemory(
                executionEnabled: true,
                cleanup,
                memoryFreeRatio: cleanup.CriticalFreeRatio / 2,
                memoryFreeRatioCurrent: true));
        AssertState(
            HostPublicResourceCapacityObservationState.HealthyFresh,
            HostPublicResourceCapacityObserver.ObserveMemory(
                executionEnabled: true,
                cleanup,
                memoryFreeRatio: cleanup.CriticalFreeRatio,
                memoryFreeRatioCurrent: true));
    }

    [Fact]
    public void VideoMemoryObservation_RequiresCurrentCompleteCapacityEvidence()
    {
        var resourceScheduler = HostManagerTestPlanFactory.CreatePlan()
            .HotPublish.ResourceScheduler.Configuration!;
        var threshold = resourceScheduler.TargetFreeRatios[(int)AdapterResourceTier.Vram];
        Assert.True(threshold > 0);
        var current = CreateGpuInventory(SamplingObservationStatus.Current);

        AssertState(
            HostPublicResourceCapacityObservationState.Disabled,
            HostPublicResourceCapacityObserver.ObserveVideoMemory(
                executionEnabled: false,
                resourceScheduler,
                current).Summary);
        AssertState(
            HostPublicResourceCapacityObservationState.UnknownOrStale,
            HostPublicResourceCapacityObserver.ObserveVideoMemory(
                executionEnabled: true,
                resourceScheduler,
                CreateGpuInventory(SamplingObservationStatus.RetainedLastGood)).Summary);
        AssertState(
            HostPublicResourceCapacityObservationState.UnknownOrStale,
            HostPublicResourceCapacityObserver.ObserveVideoMemory(
                executionEnabled: true,
                resourceScheduler,
                current with
                {
                    Adapters =
                    [
                        current.Adapters[0] with
                        {
                            UsedDedicatedMemoryBytes = 0,
                            TotalDedicatedMemoryBytes = 0
                        }
                    ]
                }).Summary);
        AssertState(
            HostPublicResourceCapacityObservationState.ShortageFresh,
            HostPublicResourceCapacityObserver.ObserveVideoMemory(
                executionEnabled: true,
                resourceScheduler,
                current with
                {
                    Adapters =
                    [
                        current.Adapters[0] with
                        {
                            UsedDedicatedMemoryBytes = 100
                        }
                    ]
                }).Summary);
        AssertState(
            HostPublicResourceCapacityObservationState.HealthyFresh,
            HostPublicResourceCapacityObserver.ObserveVideoMemory(
                executionEnabled: true,
                resourceScheduler,
                current).Summary);
    }

    [Fact]
    public void VideoMemoryObservation_IgnoresCompleteUnsupportedNonDedicatedAdapters()
    {
        var resourceScheduler = HostManagerTestPlanFactory.CreatePlan()
            .HotPublish.ResourceScheduler.Configuration!;
        var current = CreateGpuInventory(SamplingObservationStatus.Current);
        var mixed = current with
        {
            ObservedCount = 2,
            Adapters =
            [
                current.Adapters[0],
                new SchedulingGpuAdapterObservation(
                    Index: 1,
                    AdapterKey: 202,
                    SchedulingGpuCapabilityMask.Usage,
                    SchedulingGpuMetricMask.Usage,
                    SamplingObservationStatus.Current,
                    SamplingObservationStatus.Unsupported,
                    UsagePercent: 10,
                    UsedDedicatedMemoryBytes: 0,
                    TotalDedicatedMemoryBytes: 0,
                    current.Generation,
                    current.ObservedAtUtcTicks)
            ]
        };

        Assert.True(mixed.IsCurrentComplete());
        AssertState(
            HostPublicResourceCapacityObservationState.HealthyFresh,
            HostPublicResourceCapacityObserver.ObserveVideoMemory(
                executionEnabled: true,
                resourceScheduler,
                mixed).Summary);
    }

    [Fact]
    public void VideoMemoryObservation_RejectsStaleDedicatedAdapterInMixedInventory()
    {
        var resourceScheduler = HostManagerTestPlanFactory.CreatePlan()
            .HotPublish.ResourceScheduler.Configuration!;
        var current = CreateGpuInventory(SamplingObservationStatus.Current);
        var staleDedicated = current.Adapters[0] with
        {
            ValidMetricMask = SchedulingGpuMetricMask.None,
            CapacityStatus = SamplingObservationStatus.RetainedLastGood
        };
        var mixed = current with
        {
            ObservedCount = 2,
            Adapters =
            [
                staleDedicated,
                new SchedulingGpuAdapterObservation(
                    Index: 1,
                    AdapterKey: 202,
                    SchedulingGpuCapabilityMask.Usage,
                    SchedulingGpuMetricMask.Usage,
                    SamplingObservationStatus.Current,
                    SamplingObservationStatus.Unsupported,
                    UsagePercent: 10,
                    UsedDedicatedMemoryBytes: 0,
                    TotalDedicatedMemoryBytes: 0,
                    current.Generation,
                    current.ObservedAtUtcTicks)
            ]
        };

        Assert.True(mixed.IsCurrentComplete());
        AssertState(
            HostPublicResourceCapacityObservationState.UnknownOrStale,
            HostPublicResourceCapacityObserver.ObserveVideoMemory(
                executionEnabled: true,
                resourceScheduler,
                mixed).Summary);
    }

    [Fact]
    public void VideoMemoryObservation_RequiresAtLeastOneDedicatedMemoryAdapter()
    {
        var resourceScheduler = HostManagerTestPlanFactory.CreatePlan()
            .HotPublish.ResourceScheduler.Configuration!;
        var current = CreateGpuInventory(SamplingObservationStatus.Current);
        var unsupportedOnly = current with
        {
            Adapters =
            [
                current.Adapters[0] with
                {
                    CapabilityMask = SchedulingGpuCapabilityMask.Usage,
                    ValidMetricMask = SchedulingGpuMetricMask.Usage,
                    CapacityStatus = SamplingObservationStatus.Unsupported,
                    UsageStatus = SamplingObservationStatus.Current,
                    UsagePercent = 10,
                    UsedDedicatedMemoryBytes = 0,
                    TotalDedicatedMemoryBytes = 0
                }
            ]
        };

        Assert.True(unsupportedOnly.IsCurrentComplete());
        AssertState(
            HostPublicResourceCapacityObservationState.UnknownOrStale,
            HostPublicResourceCapacityObserver.ObserveVideoMemory(
                executionEnabled: true,
                resourceScheduler,
                unsupportedOnly).Summary);
    }

    [Fact]
    public void VideoMemoryObservation_PreservesExactPerAdapterHealth()
    {
        var resourceScheduler = HostManagerTestPlanFactory.CreatePlan()
            .HotPublish.ResourceScheduler.Configuration!;
        var current = CreateGpuInventory(SamplingObservationStatus.Current);
        var multiAdapter = current with
        {
            ObservedCount = 2,
            Adapters =
            [
                current.Adapters[0] with
                {
                    UsedDedicatedMemoryBytes = 100
                },
                current.Adapters[0] with
                {
                    Index = 1,
                    AdapterKey = 202,
                    UsedDedicatedMemoryBytes = 0
                }
            ]
        };

        Assert.True(multiAdapter.IsCurrentComplete());
        var observed = HostPublicResourceCapacityObserver.ObserveVideoMemory(
            executionEnabled: true,
            resourceScheduler,
            multiAdapter);
        AssertState(
            HostPublicResourceCapacityObservationState.ShortageFresh,
            observed.Summary);
        var shortage = new HostPublicResourceCapacityShortage(
            HostPublicResourceCapacityObservation.HealthyFresh,
            observed.Summary,
            observed.Adapters);
        AssertState(
            HostPublicResourceCapacityObservationState.ShortageFresh,
            shortage.VideoMemoryForAdapter(101));
        AssertState(
            HostPublicResourceCapacityObservationState.HealthyFresh,
            shortage.VideoMemoryForAdapter(202));
        AssertState(
            HostPublicResourceCapacityObservationState.UnknownOrStale,
            shortage.VideoMemoryForAdapter(303));
    }

    private static void AssertState(
        HostPublicResourceCapacityObservationState expected,
        HostPublicResourceCapacityObservation actual)
    {
        Assert.Equal(expected, actual.State);
        Assert.False(actual.AfterNormalReleaseRounds);
    }

    private static SchedulingGpuInventorySnapshot CreateGpuInventory(
        SamplingObservationStatus inventoryStatus)
    {
        const ulong generation = 7;
        const long observedAtUtcTicks = 638_900_000_000_000_000;
        return new SchedulingGpuInventorySnapshot(
            inventoryStatus,
            generation,
            observedAtUtcTicks,
            ObservedCount: 1,
            SkippedCount: 0,
            OverflowCount: 0,
            TopologyFingerprint: 11,
            [
                new SchedulingGpuAdapterObservation(
                    Index: 0,
                    AdapterKey: 101,
                    SchedulingGpuCapabilityMask.DedicatedMemory,
                    SchedulingGpuMetricMask.UsedDedicatedMemory
                        | SchedulingGpuMetricMask.TotalDedicatedMemory,
                    SamplingObservationStatus.Unsupported,
                    SamplingObservationStatus.Current,
                    UsagePercent: 0,
                    UsedDedicatedMemoryBytes: 25,
                    TotalDedicatedMemoryBytes: 100,
                    generation,
                    observedAtUtcTicks)
            ]);
    }
}
