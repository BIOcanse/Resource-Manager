using ResourceManager.Adapter;
using ResourceManager.Adapter.NativeScheduling;
using ResourceManager.Adapter.SharedMemory;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.RuntimeSpecialization;

namespace ResourceManager.App.Infrastructure.PublicResources;

internal static class HostPublicResourceCapacityObserver
{
    internal static HostPublicResourceCapacityObservation ObserveMemory(
        bool executionEnabled,
        CompiledHostManagerMemoryCleanupHotPublishPlan cleanup,
        double memoryFreeRatio,
        bool memoryFreeRatioCurrent)
    {
        if (!executionEnabled)
        {
            return HostPublicResourceCapacityObservation.Disabled;
        }
        if (!cleanup.IsPublished
            || !memoryFreeRatioCurrent
            || !double.IsFinite(memoryFreeRatio))
        {
            return HostPublicResourceCapacityObservation.UnknownOrStale;
        }
        return memoryFreeRatio < cleanup.CriticalFreeRatio
            ? HostPublicResourceCapacityObservation.ShortageFresh()
            : HostPublicResourceCapacityObservation.HealthyFresh;
    }

    internal static HostPublicResourceVideoMemoryCapacitySnapshot ObserveVideoMemory(
        bool executionEnabled,
        ResourceSchedulerConfig? resourceScheduler,
        SchedulingGpuInventorySnapshot inventory)
    {
        if (!executionEnabled)
        {
            return new(
                HostPublicResourceCapacityObservation.Disabled,
                []);
        }
        var dedicatedMemoryAdapters = inventory.Adapters
            .Where(static adapter => adapter.CapabilityMask.HasFlag(
                SchedulingGpuCapabilityMask.DedicatedMemory))
            .ToArray();
        if (resourceScheduler is null
            || !inventory.IsCurrentComplete()
            || dedicatedMemoryAdapters.Length == 0
            || dedicatedMemoryAdapters.Any(static adapter =>
                adapter.CapacityStatus != SamplingObservationStatus.Current)
            || dedicatedMemoryAdapters.Any(static adapter =>
                adapter.TotalDedicatedMemoryBytes == 0)
            || resourceScheduler.TargetFreeRatios.Length !=
                ResourceSchedulerConfig.TierCount)
        {
            return new(
                HostPublicResourceCapacityObservation.UnknownOrStale,
                []);
        }

        var targetFreeRatio =
            resourceScheduler.TargetFreeRatios[(int)AdapterResourceTier.Vram];
        if (!double.IsFinite(targetFreeRatio)
            || targetFreeRatio < 0
            || targetFreeRatio > 1)
        {
            return new(
                HostPublicResourceCapacityObservation.UnknownOrStale,
                []);
        }

        var adapters = new HostPublicResourceAdapterCapacityObservation[
            dedicatedMemoryAdapters.Length];
        var anyShortage = false;
        for (var index = 0; index < dedicatedMemoryAdapters.Length; index++)
        {
            var adapter = dedicatedMemoryAdapters[index];
            var freeRatio = 1 - adapter.UsedDedicatedMemoryBytes
                / (double)adapter.TotalDedicatedMemoryBytes;
            var capacity = freeRatio < targetFreeRatio
                ? HostPublicResourceCapacityObservation.ShortageFresh()
                : HostPublicResourceCapacityObservation.HealthyFresh;
            anyShortage |= capacity.CurrentShortage;
            adapters[index] = new(
                adapter.AdapterKey,
                capacity);
        }

        return new(
            anyShortage
                ? HostPublicResourceCapacityObservation.ShortageFresh()
                : HostPublicResourceCapacityObservation.HealthyFresh,
            adapters);
    }
}

internal readonly record struct HostPublicResourceVideoMemoryCapacitySnapshot(
    HostPublicResourceCapacityObservation Summary,
    IReadOnlyList<HostPublicResourceAdapterCapacityObservation> Adapters);
