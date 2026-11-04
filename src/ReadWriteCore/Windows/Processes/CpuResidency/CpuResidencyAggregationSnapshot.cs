
namespace ResourceManager.App.Infrastructure.CpuTopology;

internal readonly record struct CpuExecutionTimeAggregate(
    long ProcessInstanceId,
    int ProcessId,
    long? ProcessStartKey,
    string ProcessName,
    long ThreadInstanceId,
    int ThreadId,
    int LogicalProcessorId,
    long ExecutionTimeQpc,
    int SwitchCount);

internal sealed record CpuResidencyAggregationSnapshot(
    long QpcFrequency,
    long WindowStartQpc,
    long MeasuredThroughQpc,
    DateTimeOffset? MeasuredThrough,
    bool IsComplete,
    IReadOnlyList<CpuExecutionTimeAggregate> Records)
{
    public bool HasBoundaries { get; init; }
}

internal readonly record struct CpuResidencyAggregationDiagnostics(
    long QpcFrequency,
    long WindowQpc,
    int MaximumActivePairs,
    int ActivePairCount,
    int SlotsPerWindow,
    int RingSlots,
    int PairRecordBytes,
    long WindowStorageBytes,
    long MaximumWindowStorageBytes,
    int CurrentProcessCount,
    int CurrentThreadCount,
    int RetainedProcessIdentityCount,
    int RetainedThreadIdentityCount,
    long ContinuityEpoch,
    long CapacityResetCount)
{
    public CpuResidencyCapacityResetDiagnostics? LastCapacityReset { get; init; }
}

internal sealed record CpuResidencyCapacityResetDiagnostics(
    string Reason,
    long EventQpc,
    long? WindowStartQpc,
    int ActivePairCount,
    int MaximumActivePairs,
    long RequiredSlotSpan,
    int RingSlots,
    int CurrentProcessCount,
    int CurrentThreadCount);

