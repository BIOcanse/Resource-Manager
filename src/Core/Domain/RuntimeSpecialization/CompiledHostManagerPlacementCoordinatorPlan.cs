namespace ResourceManager.App.Domain.RuntimeSpecialization;

public sealed record CompiledHostManagerPlacementCoordinatorRecreatePlan(
    int MaximumDesiredCount,
    int MaximumAppliedCount,
    int MaximumActionCount,
    int MaximumStateCount,
    int CoreCapacity,
    int CcdCapacity,
    int TargetCapacity,
    int ReservationCapacity)
{
    public bool IsPublished => MaximumDesiredCount > 0
        && MaximumAppliedCount > 0
        && MaximumActionCount > 0
        && MaximumStateCount > 0
        && MaximumDesiredCount <= MaximumStateCount
        && MaximumAppliedCount <= MaximumStateCount
        && MaximumActionCount == MaximumStateCount
        && CoreCapacity > 0
        && CcdCapacity > 0
        && TargetCapacity > 0
        && ReservationCapacity >= TargetCapacity;

    public static CompiledHostManagerPlacementCoordinatorRecreatePlan Unpublished { get; } = new(
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0);
}

public sealed record CompiledHostManagerPlacementCoordinatorHotPublishPlan(
    ulong ConfigurationGeneration,
    int RetryDelayMilliseconds,
    int ActionTimeoutMilliseconds,
    int MaximumFutureSkewMilliseconds,
    CompiledGpuWindowExecutionLimits WindowExecution,
    int ApiObservationWindowMilliseconds)
{
    public CompiledGpuOverflowPolicy? GpuOverflow { get; init; }

    public bool IsPublished => ConfigurationGeneration > 0
        && RetryDelayMilliseconds > 0
        && ActionTimeoutMilliseconds > 0
        && MaximumFutureSkewMilliseconds >= 0
        && ApiObservationWindowMilliseconds > 0
        && WindowExecution is { MaximumWindowCount: > 0, CleanupReserveMilliseconds: > 0, MaximumFrameBytes: > 0, PipeBufferBytes: > 0, PreparationMaximumFrameBytes: > 0 }
        && ActionTimeoutMilliseconds > WindowExecution.CleanupReserveMilliseconds;

    public static CompiledHostManagerPlacementCoordinatorHotPublishPlan Unpublished { get; } = new(
        0,
        0,
        0,
        0,
        new(0, 0, 0, 0, 0),
        0);
}

public sealed record CompiledGpuWindowExecutionLimits(
    int MaximumWindowCount, int CleanupReserveMilliseconds, int MaximumFrameBytes, int PipeBufferBytes,
    int PreparationMaximumFrameBytes);

public sealed record CompiledGpuOverflowPolicy(
    [property: System.Text.Json.Serialization.JsonPropertyName("usage_percent")] double UsagePercent,
    [property: System.Text.Json.Serialization.JsonPropertyName("dedicated_memory_percent")] double DedicatedMemoryPercent)
{
    public bool IsValid => double.IsFinite(UsagePercent) && UsagePercent is > 0 and <= 100
        && double.IsFinite(DedicatedMemoryPercent) && DedicatedMemoryPercent is > 0 and <= 100;
}
