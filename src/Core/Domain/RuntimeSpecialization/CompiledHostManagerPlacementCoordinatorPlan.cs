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
    public CompiledCpuAutomaticExclusivityPolicy? CpuAutomaticExclusivity { get; init; }

    public bool IsPublished => ConfigurationGeneration > 0
        && RetryDelayMilliseconds > 0
        && ActionTimeoutMilliseconds > 0
        && MaximumFutureSkewMilliseconds >= 0
        && ApiObservationWindowMilliseconds > 0
        && CpuAutomaticExclusivity is { IsValid: true }
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

public sealed record CompiledCpuAutomaticExclusivityPolicy(
    [property: System.Text.Json.Serialization.JsonRequired, System.Text.Json.Serialization.JsonPropertyName("enabled")] bool Enabled,
    [property: System.Text.Json.Serialization.JsonRequired, System.Text.Json.Serialization.JsonPropertyName("core_enter_percent")] double CoreEnterPercent,
    [property: System.Text.Json.Serialization.JsonRequired, System.Text.Json.Serialization.JsonPropertyName("core_exit_percent")] double CoreExitPercent,
    [property: System.Text.Json.Serialization.JsonRequired, System.Text.Json.Serialization.JsonPropertyName("ccd_enter_percent")] double CcdEnterPercent,
    [property: System.Text.Json.Serialization.JsonRequired, System.Text.Json.Serialization.JsonPropertyName("ccd_exit_percent")] double CcdExitPercent,
    [property: System.Text.Json.Serialization.JsonRequired, System.Text.Json.Serialization.JsonPropertyName("qualification_completed_rounds")] int QualificationCompletedRounds)
{
    public bool IsValid => double.IsFinite(CoreEnterPercent) && CoreEnterPercent is > 0 and <= 100
        && double.IsFinite(CoreExitPercent) && CoreExitPercent >= 0 && CoreExitPercent < CoreEnterPercent
        && double.IsFinite(CcdEnterPercent) && CcdEnterPercent is > 0 and <= 100
        && double.IsFinite(CcdExitPercent) && CcdExitPercent >= 0 && CcdExitPercent < CcdEnterPercent
        && QualificationCompletedRounds is >= 0 and < int.MaxValue;
}
