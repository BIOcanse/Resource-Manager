using System.Collections.Frozen;
using System.Globalization;
using ResourceManager.App.Infrastructure.ResourceBreakdown;
namespace ResourceManager.App.Domain.CpuTopology;

public sealed record CpuCoreResidencySnapshot(
    DateTimeOffset CapturedAt,
    TimeSpan Window,
    long SessionGeneration,
    DateTimeOffset MeasuredFrom,
    DateTimeOffset MeasuredThrough,
    IReadOnlyList<CpuProcessCoreResidency> Processes)
{
    public IReadOnlyList<ResourceManager.ReadWrite.Windows.Processes.CpuLogicalProcessorResidencyScope> LogicalProcessorScopes { get; init; } = [];
    private readonly object indexGate = new();
    private IReadOnlyList<CpuProcessCoreResidency>? indexedProcesses;
    private FrozenDictionary<ProcessInstanceKey, CpuProcessCoreResidency?>? byProcess;
    /// <summary>Indexes references to this snapshot's original records. Ambiguous instances remain unavailable.</summary>
    public CpuProcessCoreResidency? FindProcess(ProcessInstanceKey identity)
    {
        lock (indexGate)
        {
            if (!ReferenceEquals(indexedProcesses, Processes))
            {
                byProcess = Processes.Select(process => (Process: process,
                    Birth: long.TryParse(process.ProcessStartKey, NumberStyles.None, CultureInfo.InvariantCulture, out var birth) ? birth : 0))
                    .Where(row => row.Process.ProcessId > 0 && row.Birth > 0)
                    .GroupBy(row => new ProcessInstanceKey(row.Process.ProcessId, row.Birth))
                    .ToFrozenDictionary(group => group.Key, group => group.Count() == 1 ? group.Single().Process : null);
                indexedProcesses = Processes;
            }
            return byProcess!.GetValueOrDefault(identity);
        }
    }
}

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
