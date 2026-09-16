using ResourceManager.App.Domain.ProcessAttribution;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Infrastructure.Telemetry.Etw;

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

    private static ResourceBreakdownBar CreateGpuAllocationBar(
        ResourceBreakdownBar bar,
        IReadOnlyDictionary<int, GpuAllocationAmount>? amounts,
        ProcessAttributionSnapshot attribution,
        CompiledBaseScorePlan baseScore)
    {
        if (amounts is null) return bar with { TotalValue = null, TotalSystemPercent = null, SharedValue = null,
            Software = [], ObservationStatus = SamplingObservationStatus.Unavailable,
            AttributionStatus = SamplingObservationStatus.Unavailable };
        var groups = new Dictionary<string, SoftwareGroupAccumulator>(StringComparer.OrdinalIgnoreCase);
        foreach (var (pid, amount) in amounts)
        {
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
        }).ToArray();
        var total = software.Sum(s => s.Value);
        return bar with { TotalValue = total, TotalSystemPercent = bar.CapacityValue is > 0 ? total / bar.CapacityValue * 100 : 0,
            CapacityValue = bar.CapacityValue ?? 0,
            Software = software, SharedValue = software.Sum(s => s.SharedValue ?? 0),
            ObservationStatus = SamplingObservationStatus.Current, AttributionStatus = SamplingObservationStatus.Current };
    }
}
