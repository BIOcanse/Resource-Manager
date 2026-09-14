namespace ResourceManager.App.Domain.RuntimeSpecialization;

public sealed record CompiledHostManagerMemoryCleanupRecreatePlan(int StateCapacity);

public sealed record CompiledHostManagerMemoryCleanupHotPublishPlan(
    ulong ConfigurationGeneration,
    double CriticalFreeRatio,
    double VeryLowFreeRatio,
    double LowFreeRatio,
    double GuardedFreeRatio,
    double PhysicalEmergencyFreeRatio,
    double VirtualEmergencyFreeRatio,
    double HighTierMinimumBaseScore,
    int CriticalBatchCount,
    int VeryLowBatchCount,
    int LowBatchCount,
    int GuardedBatchCount,
    int EmergencyBatchCount)
{
    public bool IsPublished => ConfigurationGeneration > 0
        && StateIsOrdered
        && CriticalBatchCount >= VeryLowBatchCount
        && VeryLowBatchCount >= LowBatchCount
        && LowBatchCount >= GuardedBatchCount
        && GuardedBatchCount > 0
        && EmergencyBatchCount > 0;

    private bool StateIsOrdered => CriticalFreeRatio >= 0
        && CriticalFreeRatio < VeryLowFreeRatio
        && VeryLowFreeRatio < LowFreeRatio
        && LowFreeRatio < GuardedFreeRatio
        && GuardedFreeRatio <= 1
        && PhysicalEmergencyFreeRatio >= 0
        && PhysicalEmergencyFreeRatio <= 1
        && VirtualEmergencyFreeRatio >= 0
        && VirtualEmergencyFreeRatio <= 1
        && HighTierMinimumBaseScore > 0;

    public static CompiledHostManagerMemoryCleanupHotPublishPlan Unpublished { get; } = new(
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
}
