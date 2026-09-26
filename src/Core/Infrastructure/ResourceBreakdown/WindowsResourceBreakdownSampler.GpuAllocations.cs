using ResourceManager.App.Domain.ProcessAttribution;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Infrastructure.Telemetry.Etw;
using ResourceManager.App.Domain.RuntimeSpecialization;

namespace ResourceManager.App.Infrastructure.ResourceBreakdown;

public sealed partial class WindowsResourceBreakdownSampler
{
    private static bool NeedsGpuAllocations(ResourceBreakdownSampleRequest request)
        => request.SchedulingMetricMask.HasFlag(SchedulingProcessMetricMask.GpuDedicatedMemory)
            || request.PublicationDatasetIds.Contains(SamplingDatasetIds.ProcessGpuVram)
            || request.MetricIds.Any(static id => TryParseGpuMetric(id, out _, out var kind) && kind == "vram");

    private GpuAllocationReading? ReadGpuAllocations(IReadOnlyList<ProcessResourceSample> samples)
        => gpuAllocations?.ReadCurrent(samples.Where(static p => p.StartKey is > 0)
            .ToDictionary(static p => p.ProcessId, static p => p.StartKey!.Value));

    internal static ResourceBreakdownBar CreateUnattributedMemoryUsageBar(
        string metricId, string label, string scaleMode, double? usedBytes, double? capacityBytes)
    {
        var percent = usedBytes.HasValue && capacityBytes is > 0 ? usedBytes / capacityBytes * 100 : null;
        var unknown = RuntimeSoftwareAttribution.Unattributed;
        // Device totals alone cannot establish per-process ownership.
        ResourceSoftwareSegment[] segments = usedBytes is > 0
            ? [new(unknown.Id, unknown.Name, unknown.Kind, unknown.DisplayKind,
                usedBytes.Value, percent ?? 0, 0, [])]
            : [];
        return new(metricId, label, "B", scaleMode, usedBytes, capacityBytes, percent, segments,
            usedBytes.HasValue ? SamplingObservationStatus.Current : SamplingObservationStatus.Unavailable,
            SamplingObservationStatus.Unavailable);
    }

    private static ResourceBreakdownBar CreateGpuResidentPartition(ResourceBreakdownBar bar,
        IReadOnlyDictionary<int, GpuAllocationAmount>? amounts, ProcessAttributionSnapshot attribution,
        CompiledBaseScorePlan baseScore)
    {
        if (amounts is null) return bar;
        var groups = new Dictionary<string, SoftwareGroupAccumulator>(StringComparer.OrdinalIgnoreCase);
        foreach (var (pid, amount) in amounts)
        {
            if (amount.TotalBytes <= 0) continue;
            var process = CreateAttributedProcess(pid, amount.TotalBytes, attribution.ProcessById,
                attribution.AttributionByProcessId, baseScore, bar.CapacityValue ?? 0, true, false);
            if (process is not { } value) continue;
            if (!groups.TryGetValue(value.Software.Id, out var group))
                groups[value.Software.Id] = group = new SoftwareGroupAccumulator(value.Software);
            group.Add(value);
        }
        var software = groups.Values.Select(group =>
        {
            var segment = CreateSoftwareSegment(group, bar.CapacityValue ?? 0, true, false);
            var processes = segment.Processes.Select(process => process with
            {
                SharedValue = amounts[process.ProcessId].SharedBytes
            }).ToArray();
            return segment with { Processes = processes, SharedValue = processes.Sum(p => p.SharedValue ?? 0) };
        }).ToList();
        var remaining = bar.TotalValue - software.Sum(segment => segment.Value);
        if (remaining is < 0) return bar;
        if (remaining is > 0)
        {
            var unknown = RuntimeSoftwareAttribution.Unattributed;
            software.Add(new(unknown.Id, unknown.Name, unknown.Kind, unknown.DisplayKind, remaining.Value,
                bar.CapacityValue is > 0 ? remaining.Value / bar.CapacityValue.Value * 100 : 0, 0, []));
        }
        return bar with { Software = software, SharedValue = software.Sum(s => s.SharedValue ?? 0),
            AttributionStatus = SamplingObservationStatus.Current };
    }
}
