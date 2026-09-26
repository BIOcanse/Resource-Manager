namespace ResourceManager.App.Domain.CpuTopology;

public sealed record CpuCoreResidencySnapshot(
    DateTimeOffset CapturedAt,
    TimeSpan Window,
    long SessionGeneration,
    DateTimeOffset MeasuredFrom,
    DateTimeOffset MeasuredThrough,
    IReadOnlyList<CpuProcessCoreResidency> Processes);

public sealed record CpuProcessCoreResidency(
    string ProcessInstanceId,
    int ProcessId,
    string? ProcessStartKey,
    string ProcessName,
    double ExecutionTimeMilliseconds,
    int SwitchCount,
    int ThreadCount,
    string? PrimaryCcdId,
    string? PrimaryPhysicalCoreId,
    int? PrimaryLogicalProcessorId,
    IReadOnlyList<CpuCcdResidency> Ccds,
    IReadOnlyList<CpuPhysicalCoreResidency> PhysicalCores,
    IReadOnlyList<CpuLogicalProcessorResidency> LogicalProcessors,
    IReadOnlyList<CpuThreadCoreResidency> Threads);

public sealed record CpuCcdResidency(
    string CcdId,
    double ExecutionTimeMilliseconds,
    int SwitchCount,
    double SharePercent);

public sealed record CpuPhysicalCoreResidency(
    string PhysicalCoreId,
    string CcdId,
    double ExecutionTimeMilliseconds,
    int SwitchCount,
    double SharePercent,
    double UsagePercent);

public sealed record CpuLogicalProcessorResidency(
    int LogicalProcessorId,
    string PhysicalCoreId,
    string CcdId,
    double ExecutionTimeMilliseconds,
    int SwitchCount,
    double SharePercent);

public sealed record CpuThreadCoreResidency(
    string ThreadInstanceId,
    int ThreadId,
    double ExecutionTimeMilliseconds,
    int SwitchCount,
    string? PrimaryCcdId,
    string? PrimaryPhysicalCoreId,
    int? PrimaryLogicalProcessorId,
    IReadOnlyList<string>? PhysicalCoreIds = null);
