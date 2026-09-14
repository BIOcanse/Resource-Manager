using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.Telemetry.Etw;

namespace ResourceManager.App.Infrastructure.ResourceBreakdown;

internal static class GpuAllocationSchedulingProjection
{
    internal static SchedulingProcessGpuRead Apply(SchedulingProcessGpuRead usage, GpuAllocationReading? allocations,
        IReadOnlyCollection<ProcessInstanceKey> processes, ulong generation, long observedAtUtcTicks)
    {
        var births = processes.ToDictionary(p => p.ProcessId, p => checked((ulong)p.StartKey));
        var amounts = new Dictionary<(ulong AdapterKey, int ProcessId, ulong ProcessStartKey), GpuAllocationAmount>();
        if (allocations is not null)
            foreach (var (adapter, members) in allocations.Adapters)
                foreach (var (pid, amount) in members)
                    if (births.TryGetValue(pid, out var birth)) amounts.Add((adapter, pid, birth), amount);
        return usage with
        {
            DedicatedMemoryStatus = allocations is null ? SamplingObservationStatus.Unavailable : SamplingObservationStatus.Current,
            DedicatedMemoryGeneration = allocations is null ? 0 : generation,
            DedicatedMemoryObservedAtUtcTicks = allocations is null ? 0 : observedAtUtcTicks,
            DedicatedMemoryBytes = amounts.ToDictionary(p => p.Key, p => p.Value.TotalBytes),
            AllocationAmounts = amounts
        };
    }
}
