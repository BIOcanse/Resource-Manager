using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;

namespace ResourceManager.App.Domain.ResourceBreakdown;

public sealed partial record SchedulingProcessFactSnapshot
{
    public SchedulingProcessFactSnapshot ProjectValidFacts(SchedulingGpuInventorySnapshot inventory)
    {
        var exclusions = new List<SchedulingFactExclusion>(FactExclusions);
        void Exclude(SchedulingProcessFact? process, SchedulingFactCode fact, SchedulingFactExclusionReason reason, ulong? adapterKey)
            => exclusions.Add(new(process?.ProcessId, process?.ProcessStartKey, null, fact, reason, adapterKey));

        if (!IsInventoryCurrent())
        {
            Exclude(null, SchedulingFactCode.Inventory, SchedulingFactExclusionReason.DatasetNotCurrent, null);
            return this with { Processes = [], FactExclusions = exclusions };
        }
        var incompleteReason = InventoryIncompleteReason();
        if (incompleteReason is { } reason) Exclude(null, SchedulingFactCode.Inventory, reason, null);
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
            else Exclude(null, FactCodeFor(metric), SchedulingFactExclusionReason.DatasetNotCurrent, null);
        }
        var selected = new List<SchedulingProcessFact>();
        var unidentified = HasUnidentifiedProcesses;
        foreach (var process in Processes)
        {
            if (process.ProcessId <= 0 || process.ProcessStartKey == 0
                || duplicates.Contains((process.ProcessId, process.ProcessStartKey)))
            {
                if (!HasSeparateFoundationPayloads) unidentified = true;
                Exclude(process, SchedulingFactCode.Identity, SchedulingFactExclusionReason.InvalidOrDuplicateProcess, null);
                continue;
            }
            if (!double.IsFinite(process.BaseScore) || process.BaseScore < 0)
                Exclude(process, SchedulingFactCode.BaseScore, SchedulingFactExclusionReason.RowMissingOrInvalid, null);
            if (string.IsNullOrWhiteSpace(process.ProcessName) || string.IsNullOrWhiteSpace(process.SoftwareId))
                Exclude(process, SchedulingFactCode.Attribution, SchedulingFactExclusionReason.RowMissingOrInvalid, null);
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
                    Exclude(process, FactCodeFor(metric), current.HasFlag(metric) ? SchedulingFactExclusionReason.RowMissingOrInvalid : SchedulingFactExclusionReason.DatasetNotCurrent, null);
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
                    else Exclude(process, SchedulingFactCode.GpuUsage, SchedulingFactExclusionReason.GpuRowMissingOrInvalid, gpu.AdapterKey);
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
                    else Exclude(process, SchedulingFactCode.GpuDedicatedMemory, SchedulingFactExclusionReason.GpuRowMissingOrInvalid, gpu.AdapterKey);
                }
                double? ProjectBytes(double? bytes, SchedulingFactCode fact)
                {
                    if (bytes is null) return null;
                    if (memorySourceValid && double.IsFinite(bytes.Value) && bytes >= 0) return bytes;
                    Exclude(process, fact, SchedulingFactExclusionReason.GpuRowMissingOrInvalid, gpu.AdapterKey);
                    return null;
                }
                var privateBytes = ProjectBytes(gpu.PrivateMemoryBytes, SchedulingFactCode.GpuPrivateMemoryBytes);
                var sharedBytes = ProjectBytes(gpu.SharedMemoryBytes, SchedulingFactCode.GpuSharedMemoryBytes);
                if (gpuMask != SchedulingProcessMetricMask.None || privateBytes is not null || sharedBytes is not null)
                    gpuRows.Add(gpu with { ValidMetricMask = gpuMask,
                        PrivateMemoryBytes = privateBytes, SharedMemoryBytes = sharedBytes });
            }
            foreach (var metric in new[] { SchedulingProcessMetricMask.GpuUsage, SchedulingProcessMetricMask.GpuDedicatedMemory })
                if (!gpuRows.Any(g => g.HasMetric(metric))) mask &= ~metric;
            selected.Add(process with { ValidMetricMask = mask, Gpus = gpuRows });
        }
        var removedDecisionRows = checked((uint)(Processes.Count - selected.Count));
        var accountDecisionExclusions = HasSeparateFoundationPayloads && incompleteReason is null;
        return this with
        {
            Processes = selected, CurrentMetricMask = current,
            // With a separate inventory, filtering a decision row changes only
            // emitted/skipped accounting. Its live inventory identity remains.
            EmittedCount = accountDecisionExclusions ? EmittedCount - removedDecisionRows : EmittedCount,
            SkippedCount = accountDecisionExclusions ? SkippedCount + removedDecisionRows : SkippedCount,
            HasUnidentifiedProcesses = unidentified,
            FactExclusions = exclusions
        };
    }

    private static SchedulingFactCode FactCodeFor(SchedulingProcessMetricMask metric)
        => metric switch
        {
            SchedulingProcessMetricMask.CpuUsage => SchedulingFactCode.CpuUsage,
            SchedulingProcessMetricMask.MemoryUsage => SchedulingFactCode.MemoryUsage,
            SchedulingProcessMetricMask.GpuUsage => SchedulingFactCode.GpuUsage,
            SchedulingProcessMetricMask.GpuDedicatedMemory => SchedulingFactCode.GpuDedicatedMemory,
            SchedulingProcessMetricMask.RuntimeState => SchedulingFactCode.RuntimeState,
            _ => throw new ArgumentOutOfRangeException(nameof(metric))
        };
}
