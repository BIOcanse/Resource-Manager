using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.Telemetry.Etw;
using ResourceManager.App.Domain.Metrics;

namespace ResourceManager.App.Infrastructure.ResourceBreakdown;

internal static class GpuAllocationSchedulingProjection
{
    internal static SchedulingProcessGpuRead Apply(SchedulingProcessGpuRead usage, GpuAllocationReading? allocations,
        IReadOnlyCollection<ProcessInstanceKey> processes)
    {
        var births = processes.ToDictionary(p => p.ProcessId, p => checked((ulong)p.StartKey));
        var amounts = new Dictionary<(ulong AdapterKey, int ProcessId, ulong ProcessStartKey), GpuAllocationAmount>();
        if (allocations is not null)
            foreach (var (adapter, members) in allocations.Adapters)
                foreach (var (pid, amount) in members)
                    if (births.TryGetValue(pid, out var birth)) amounts.Add((adapter, pid, birth), amount);
        return usage with
        {
            AllocationAmounts = amounts,
            DedicatedMemoryBytes = amounts.ToDictionary(static item => item.Key, static item => item.Value.TotalBytes),
            DedicatedMemoryStatus = allocations is null ? SamplingObservationStatus.Unavailable : SamplingObservationStatus.Current,
            DedicatedMemoryGeneration = allocations?.Generation ?? 0,
            DedicatedMemoryObservedAtUtcTicks = allocations?.ObservedAtUtcTicks ?? 0
        };
    }
}
