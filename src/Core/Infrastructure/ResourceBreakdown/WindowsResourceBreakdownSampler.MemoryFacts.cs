using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.ReadWrite.ProcessFields;

namespace ResourceManager.App.Infrastructure.ResourceBreakdown;

public sealed partial class WindowsResourceBreakdownSampler
{
    private IReadOnlyList<ProcessMemoryFact> CreateProcessMemoryFacts(
        IReadOnlyList<ProcessMetricFieldDefinition> definitions, IReadOnlyList<ProcessResourceSample> samples,
        HardwareMetricSnapshot? hardware)
    {
        var facts = new List<ProcessMemoryFact>();
        foreach (var field in definitions)
        {
            if (ProcessMemoryFactFields.TotalMetricId(field.Name) is not { } totalId) continue;
            double? total = hardware is not null && TryGetCurrentHardwareMetric(hardware, totalId, out var reading)
                ? reading.NumericValue : null;
            double? capacity = hardware is null ? null : totalId == "memory.usage"
                ? hardware.Memory.IsUsageAvailable ? hardware.Memory.TotalBytes : null
                : hardware.VirtualMemory.IsSelectable ? hardware.VirtualMemory.TotalBytes : null;
            var values = new List<ProcessMemoryValue>();
            foreach (var process in samples)
                if (process.StartKey is > 0
                    && ProcessMetricFieldProjection.CreateBasicValue(field, process.BasicMetrics).IntegerValue is { } integer)
                    values.Add(new(process.ProcessId, process.StartKey.Value, (double)integer.ToBigInteger()));
            var sum = values.Sum(static row => row.Value);
            facts.Add(new(field.Name, totalId, total, capacity, sum, values)
            {
                Residuals = total is { } systemTotal && systemTotal > sum
                    ? [CreateResidualSoftwareSegment(totalId, systemTotal - sum, capacity ?? 0,
                        isBytes: true, isBytesPerSecond: false,
                        values.Select(static row => row.ProcessId).ToHashSet(), residualBreakdownProvider)] : []
            });
        }
        return facts;
    }
}
