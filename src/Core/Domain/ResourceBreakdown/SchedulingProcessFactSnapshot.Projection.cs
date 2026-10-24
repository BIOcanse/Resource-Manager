using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;

namespace ResourceManager.App.Domain.ResourceBreakdown;

public sealed partial record SchedulingProcessFactSnapshot
{
    public SchedulingProcessFactSnapshot ProjectValidFacts(SchedulingGpuInventorySnapshot inventory)
    {
        var exclusions = new List<SchedulingFactExclusion>(FactExclusions);
        void Exclude(SchedulingProcessFact? process, string fact, string reason)
            => exclusions.Add(new(process?.ProcessId, process?.ProcessStartKey, null, fact, reason));

        if (!IsInventoryCurrent())
        {
            Exclude(null, "inventory", "dataset-not-current");
            return this with { Processes = [], FactExclusions = exclusions };
        }
        var complete = IsInventoryCurrentComplete();
        if (!complete) Exclude(null, "inventory", "full-list-not-proven");
        var duplicates = Processes.GroupBy(p => (p.ProcessId, p.ProcessStartKey))
            .Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        if (HasSeparateFoundationPayloads)
            duplicates.UnionWith(InventoryProcesses.GroupBy(p => (p.ProcessId, p.ProcessStartKey))
                .Where(g => g.Count() > 1).Select(g => g.Key));
        var current = SchedulingProcessMetricMask.None;
        foreach (var metric in EnumerateMetrics(RequestedMetricMask))
        {
            var available = metric switch
            {
                SchedulingProcessMetricMask.GpuUsage => IsGpuUsageCurrentComplete(inventory),
                SchedulingProcessMetricMask.GpuDedicatedMemory => IsGpuDedicatedMemoryCurrentComplete(inventory),
                _ => IsScalarMetricCurrentComplete(metric)
            };
            if (available) current |= metric;
            else Exclude(null, metric.ToString(), "dataset-not-current");
        }
        var selected = new List<SchedulingProcessFact>();
        var unidentified = HasUnidentifiedProcesses;
        foreach (var process in Processes)
        {
            if (process.ProcessId <= 0 || process.ProcessStartKey == 0
                || duplicates.Contains((process.ProcessId, process.ProcessStartKey)))
            {
                unidentified = true;
                Exclude(process, "identity", "invalid-or-duplicate-process");
                continue;
            }
            if (!double.IsFinite(process.BaseScore) || process.BaseScore < 0)
                Exclude(process, "base-score", "row-missing-or-invalid");
            if (string.IsNullOrWhiteSpace(process.ProcessName) || string.IsNullOrWhiteSpace(process.SoftwareId))
                Exclude(process, "attribution", "row-missing-or-invalid");
            var mask = process.ValidMetricMask & current;
            foreach (var metric in EnumerateMetrics(RequestedMetricMask))
            {
                var valid = mask.HasFlag(metric) && (metric switch
                {
                    SchedulingProcessMetricMask.CpuUsage => IsPercentValid(process.CpuUsagePercent, mask, metric),
                    SchedulingProcessMetricMask.MemoryUsage => IsMemoryValid(process.MemoryUsedBytes, mask),
                    SchedulingProcessMetricMask.RuntimeState => IsRuntimeStateValid(process),
                    _ => true
                });
                if (!valid)
                {
                    mask &= ~metric;
                    Exclude(process, metric.ToString(), current.HasFlag(metric) ? "row-missing-or-invalid" : "dataset-not-current");
                }
            }
            var gpuRows = new List<SchedulingProcessGpuFact>();
            var duplicateIndexes = process.Gpus.GroupBy(g => g.GpuIndex).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
            var duplicateKeys = process.Gpus.GroupBy(g => g.AdapterKey).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
            foreach (var gpu in process.Gpus)
            {
                var gpuMask = SchedulingProcessMetricMask.None;
                var adapter = inventory.Adapters.FirstOrDefault(a => a.Index == gpu.GpuIndex && a.AdapterKey == gpu.AdapterKey);
                var identityValid = !duplicateIndexes.Contains(gpu.GpuIndex) && !duplicateKeys.Contains(gpu.AdapterKey)
                    && adapter is not null;
                const SchedulingProcessMetricMask usage = SchedulingProcessMetricMask.GpuUsage;
                if (gpu.ValidMetricMask.HasFlag(usage))
                {
                    if (mask.HasFlag(usage) && identityValid && IsGpuLineageValid(gpu, usage)
                        && IsPercentValid(gpu.UsagePercent, gpu.ValidMetricMask, usage))
                        gpuMask |= usage;
                    else Exclude(process, $"{usage}:{gpu.AdapterKey}", "gpu-row-missing-or-invalid");
                }
                const SchedulingProcessMetricMask memory = SchedulingProcessMetricMask.GpuDedicatedMemory;
                var memorySourceValid = mask.HasFlag(memory) && identityValid
                    && gpu.DedicatedMemorySourceGeneration > 0 && gpu.DedicatedMemoryTopologyGeneration > 0;
                if (gpu.ValidMetricMask.HasFlag(memory))
                {
                    if (memorySourceValid && adapter!.CapabilityMask.HasFlag(SchedulingGpuCapabilityMask.DedicatedMemory)
                        && adapter.CapacityStatus == SamplingObservationStatus.Current
                        && IsPercentValid(gpu.DedicatedMemoryUsedPercent, gpu.ValidMetricMask, memory))
                        gpuMask |= memory;
                    else Exclude(process, $"{memory}:{gpu.AdapterKey}", "gpu-row-missing-or-invalid");
                }
                double? ProjectBytes(double? bytes, string fact)
                {
                    if (bytes is null) return null;
                    if (memorySourceValid && double.IsFinite(bytes.Value) && bytes >= 0) return bytes;
                    Exclude(process, $"{fact}:{gpu.AdapterKey}", "gpu-row-missing-or-invalid");
                    return null;
                }
                var privateBytes = ProjectBytes(gpu.PrivateMemoryBytes, "gpu-private-memory-bytes");
                var sharedBytes = ProjectBytes(gpu.SharedMemoryBytes, "gpu-shared-memory-bytes");
                if (gpuMask != SchedulingProcessMetricMask.None || privateBytes is not null || sharedBytes is not null)
                    gpuRows.Add(gpu with { ValidMetricMask = gpuMask,
                        PrivateMemoryBytes = privateBytes, SharedMemoryBytes = sharedBytes });
            }
            foreach (var metric in new[] { SchedulingProcessMetricMask.GpuUsage, SchedulingProcessMetricMask.GpuDedicatedMemory })
                if (!gpuRows.Any(g => g.HasMetric(metric))) mask &= ~metric;
            selected.Add(process with { ValidMetricMask = mask, Gpus = gpuRows });
        }
        return this with
        {
            Processes = selected, CurrentMetricMask = current,
            // Preserve the source accounting. Removed identities cannot prove absence.
            HasUnidentifiedProcesses = unidentified || !complete,
            FactExclusions = exclusions
        };
    }
}
