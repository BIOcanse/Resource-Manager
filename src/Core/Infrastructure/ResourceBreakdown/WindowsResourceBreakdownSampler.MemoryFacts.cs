using System.Globalization;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.ReadWrite.ProcessFields;
using ResourceManager.ReadWrite.Windows.Processes;

namespace ResourceManager.App.Infrastructure.ResourceBreakdown;

public sealed partial class WindowsResourceBreakdownSampler
{
    // The historical storage name is shared with the original memory demand path.
    // All quantities use their original owner and exact field projection.
    internal static IReadOnlyList<ProcessMemoryFact> CreateProcessMeasureFacts(
        IReadOnlyList<ProcessMetricFieldDefinition> definitions, ProcessMetricFieldSnapshot? fields,
        HardwareMetricSnapshot? hardware, ProcessNetworkTrafficWindow? network,
        PhysicalDiskProcessIoWindow? disk, IResourceResidualBreakdownProvider? capturedResiduals = null)
    {
        var facts = new List<ProcessMemoryFact>();
        foreach (var field in definitions.Where(static field => field.GpuIndex is null))
        {
            if (field.ResourceFactTotalMetricId is not { } totalId) continue;
            var observation = fields?.Sources.FirstOrDefault(source => source.SourceId == field.SourceId);
            var capturedAt = observation is null ? fields?.CapturedAt : observation.CapturedAt;
            if (capturedAt is null) continue;
            var values = fields!.Processes.Where(static row => row.GpuEngine is null && row.LogicalProcessor is null)
                .Select(row => (Row: row, Value: row.Values.FirstOrDefault(value => value.Field == field.Name)))
                .Where(static item => item.Value?.NumericValue is not null || item.Value?.IntegerValue is not null)
                .Where(static item => long.TryParse(item.Row.CreationWindowsFileTime100Ns, out var key) && key > 0)
                .GroupBy(static item => (item.Row.ProcessId, Start: long.Parse(item.Row.CreationWindowsFileTime100Ns!, CultureInfo.InvariantCulture)))
                .Select(group => new ProcessMemoryValue(group.Key.ProcessId, group.Key.Start,
                    group.Sum(static item => item.Value!.NumericValue ?? (double)item.Value.IntegerValue!.Value.ToBigInteger())))
                .ToArray();
            double? total = null, capacity = null;
            if (field.DatasetId == ProcessMetricDatasetIds.ObservedNetworkTraffic)
            {
                if (network is { WindowMilliseconds: 2000 })
                {
                    var receive = field.Code is ProcessMetricFieldCode.NetworkReceiveBytes or ProcessMetricFieldCode.NetworkReceiveRate;
                    total = SumCounts(network.Processes.Select(row => receive ? row.ReceiveBytes : row.SendBytes));
                    if (field.Unit == "B/s") total /= 2;
                    capacity = total is { } number ? Math.Max(1, number) : null;
                }
            }
            else if (field.DatasetId == ProcessMetricDatasetIds.ObservedPhysicalDiskIo)
            {
                if (disk is { Complete: true, WindowMilliseconds: 2000 })
                {
                    total = SumCounts(disk.Processes.Select(row => field.Code == ProcessMetricFieldCode.PhysicalDiskReadBytes ? row.ReadBytes : row.WriteBytes));
                    capacity = total is { } number ? Math.Max(1, number) : null;
                }
            }
            else
            {
                if (hardware is not null && TryGetCurrentHardwareMetric(hardware, totalId, out var reading)) total = reading.NumericValue;
                capacity = totalId switch
                {
                    "cpu.usage" => 100,
                    "memory.usage" => hardware?.Memory.IsUsageAvailable == true ? hardware.Memory.TotalBytes : null,
                    "virtualMemory.usage" => hardware?.VirtualMemory.IsSelectable == true ? hardware.VirtualMemory.TotalBytes : null,
                    _ => null
                };
            }
            var sum = values.Sum(static row => row.Value);
            facts.Add(new(field.Name, totalId, total, capacity, sum, values)
            {
                Residuals = field.Name == "process_all_private_working_set_actual"
                    && total is { } systemTotal && systemTotal > sum
                    ? [CreateResidualSoftwareSegment(totalId, systemTotal - sum, capacity ?? 0,
                        isBytes: true, isBytesPerSecond: false,
                        values.Select(static row => row.ProcessId).ToHashSet(),
                        capturedResiduals ?? new StaticResourceResidualBreakdownProvider())] : [],
                Unit = field.Unit, AdditiveAcrossProcesses = field.AdditiveAcrossProcesses == true,
                CapturedAt = capturedAt, WindowMilliseconds = observation?.WindowMilliseconds
                    ?? (field.DatasetId is ProcessMetricDatasetIds.ObservedNetworkTraffic or ProcessMetricDatasetIds.ObservedPhysicalDiskIo ? 2000 : null)
            });
        }
        return facts;
    }

    private static double? SumCounts(IEnumerable<string?> counts)
    {
        double sum = 0;
        foreach (var count in counts)
        {
            if (!ulong.TryParse(count, NumberStyles.None, CultureInfo.InvariantCulture, out var value)) return null;
            sum += value;
        }
        return sum;
    }
}
