using ResourceManager.App.Domain.RuntimeSpecialization;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization;

public sealed partial class HostManagerPlanCompiler
{
    private static CompiledHostManagerMemoryCleanupHotPublishPlan CompileMemoryCleanup(
        HostManagerMemoryCleanupProfile source,
        CompiledHostManagerSmartCoordinatorHotPublishPlan smartCoordinator,
        int stateCapacity,
        ResourceManager.App.Domain.Settings.AppPerformanceSettings performance)
    {
        ValidateClosedRatio(source.CriticalFreeRatio, "hot_publish.memory_cleanup.critical_free_ratio");
        ValidateClosedRatio(source.VeryLowFreeRatio, "hot_publish.memory_cleanup.very_low_free_ratio");
        ValidateClosedRatio(source.LowFreeRatio, "hot_publish.memory_cleanup.low_free_ratio");
        ValidateClosedRatio(source.GuardedFreeRatio, "hot_publish.memory_cleanup.guarded_free_ratio");
        if (source.CriticalFreeRatio >= source.VeryLowFreeRatio
            || source.VeryLowFreeRatio >= source.LowFreeRatio
            || source.LowFreeRatio >= source.GuardedFreeRatio)
        {
            throw new InvalidDataException(
                "memory cleanup free-ratio bands must be strictly ordered critical < very_low < low < guarded.");
        }

        ValidatePositive(source.CriticalBatchCount, "hot_publish.memory_cleanup.critical_batch_count");
        ValidatePositive(source.VeryLowBatchCount, "hot_publish.memory_cleanup.very_low_batch_count");
        ValidatePositive(source.LowBatchCount, "hot_publish.memory_cleanup.low_batch_count");
        ValidatePositive(source.GuardedBatchCount, "hot_publish.memory_cleanup.guarded_batch_count");
        ValidatePositive(source.EmergencyBatchCount, "hot_publish.memory_cleanup.emergency_batch_count");
        if (source.CriticalBatchCount < source.VeryLowBatchCount
            || source.VeryLowBatchCount < source.LowBatchCount
            || source.LowBatchCount < source.GuardedBatchCount)
        {
            throw new InvalidDataException(
                "memory cleanup batch counts must preserve critical >= very_low >= low >= guarded.");
        }

        ValidateMaximum(source.CriticalBatchCount, stateCapacity, "hot_publish.memory_cleanup.critical_batch_count");
        ValidateMaximum(source.EmergencyBatchCount, stateCapacity, "hot_publish.memory_cleanup.emergency_batch_count");
        var generation = HostManagerPlanIdentity.CreateGeneration(new
        {
            source.CriticalFreeRatio,
            source.VeryLowFreeRatio,
            source.LowFreeRatio,
            source.GuardedFreeRatio,
            PhysicalEmergencyFreeRatio = performance.PhysicalMemoryAutomaticCleanupPercent / 100d,
            VirtualEmergencyFreeRatio = performance.VirtualMemoryAutomaticCleanupPercent / 100d,
            HighTierMinimumBaseScore =
                smartCoordinator.BaseScoreTiers.HighMinimumBaseScore,
            source.CriticalBatchCount,
            source.VeryLowBatchCount,
            source.LowBatchCount,
            source.GuardedBatchCount,
            source.EmergencyBatchCount
        });
        return new CompiledHostManagerMemoryCleanupHotPublishPlan(
            generation,
            source.CriticalFreeRatio,
            source.VeryLowFreeRatio,
            source.LowFreeRatio,
            source.GuardedFreeRatio,
            performance.PhysicalMemoryAutomaticCleanupPercent / 100d,
            performance.VirtualMemoryAutomaticCleanupPercent / 100d,
            smartCoordinator.BaseScoreTiers.HighMinimumBaseScore,
            source.CriticalBatchCount,
            source.VeryLowBatchCount,
            source.LowBatchCount,
            source.GuardedBatchCount,
            source.EmergencyBatchCount);
    }

    private static void ValidateClosedRatio(double value, string path)
    {
        if (!double.IsFinite(value) || value < 0 || value > 1)
        {
            throw new InvalidDataException($"{path} must be a finite ratio between zero and one.");
        }
    }
}
