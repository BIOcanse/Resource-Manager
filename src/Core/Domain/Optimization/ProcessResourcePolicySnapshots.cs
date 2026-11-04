namespace ResourceManager.App.Domain.Optimization;

public sealed record ProcessResourcePolicySnapshot(
    int ProcessId,
    string ProcessName,
    string? ExecutablePath,
    DateTimeOffset? StartedAt,
    string PriorityClass,
    long ProcessorAffinityMask,
    int LogicalProcessorCount,
    uint? MemoryPriority = null,
    string? MemoryPriorityRawValue = null,
    string? MemoryPriorityDisplayValue = null,
    uint? PowerThrottlingControlMask = null,
    uint? PowerThrottlingStateMask = null,
    string? PowerThrottlingRawValue = null,
    string? PowerThrottlingDisplayValue = null);

public sealed record ProcessMemoryPrioritySnapshot(
    int ProcessId,
    uint MemoryPriority,
    string RawValue,
    string DisplayValue);

public sealed record ThreadCpuSetPolicySnapshot(
    int ProcessId,
    int ThreadId,
    DateTimeOffset CreatedAt,
    IReadOnlyList<uint> CpuSetIds);

public sealed record ProcessResourcePolicyWriteResult(
    bool Succeeded,
    string Message);

public sealed record ProcessPowerThrottlingSnapshot(
    int ProcessId,
    uint Version,
    uint ControlMask,
    uint StateMask,
    string RawValue,
    string DisplayValue);
