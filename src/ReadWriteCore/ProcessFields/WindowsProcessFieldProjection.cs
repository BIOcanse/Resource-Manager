using ResourceManager.App.Domain.Metrics;
using System.Globalization;
using ResourceManager.ReadWrite.Windows.Processes;
using ResourceManager.App.Domain.ResourceBreakdown;

namespace ResourceManager.ReadWrite.ProcessFields;

internal static class WindowsProcessFieldProjection
{
    internal static ProcessMetricFieldSnapshot CreateProcessBasicFields(IEnumerable<ProcessMetricFieldDefinition> selected,
        IReadOnlyList<ProcessFieldProjectionSample> samples, DateTimeOffset capturedAt, TimeSpan? cpuInterval, ProcessIoRatePreparation? ioRates = null)
    {
        var definitions = selected.Where(static field => field.DatasetId == ProcessMetricDatasetIds.BasicMetrics).ToArray();
        var rows = samples.OrderBy(static sample => sample.ProcessId).Select(sample => new ProcessMetricFieldValues(sample.ProcessId,
            sample.StartKey is > 0 ? sample.StartKey.Value.ToString(CultureInfo.InvariantCulture) : null,
            definitions.Select(field => field.Code == ProcessMetricFieldCode.CpuUsage
                ? ProcessMetricFieldProjection.CreateCpuUsageValue(field, sample.HasCpuPercent ? sample.CpuPercent : null)
                : field.SourceId == "windows.process.io-interval"
                    ? new ProcessMetricFieldValue(field.Name, null, ioRates?.Find(sample.ProcessId, sample.StartKey)?.Value(field.Code))
                    : ProcessMetricFieldProjection.CreateBasicValue(field, sample.BasicMetrics)).ToArray())
            { IoInterval = definitions.Any(static field => field.SourceId == "windows.process.io-interval")
                ? ioRates?.Find(sample.ProcessId, sample.StartKey)?.Interval : null }).ToArray();
        var sources = definitions.Select(static field => field.SourceId).Distinct(StringComparer.Ordinal)
            .Select(source => new ProcessMetricFieldSourceObservation(source, capturedAt,
                source == "windows.process.cpu-interval" && cpuInterval is { } elapsed
                    ? elapsed.Ticks / TimeSpan.TicksPerMillisecond : null)).ToArray();
        return new(capturedAt, sources.Length == 1 ? sources[0].WindowMilliseconds : null, definitions, rows) { Sources = sources };
    }

    internal static ProcessMetricFieldSnapshot CreateProcessNetworkFields(IReadOnlyList<string> selected,
        IReadOnlyList<ProcessFieldProjectionSample> samples, ProcessNetworkTrafficWindow? window)
    {
        var names = selected.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var definitions = ProcessMetricFieldRegistry.Definitions.Where(field => names.Contains(field.Name)
            && field.DatasetId == ProcessMetricDatasetIds.ObservedNetworkTraffic).ToArray();
        // The registered source contract has a complete, fixed two-second delivery window.
        if (window?.WindowMilliseconds != 2000) window = null;
        var sourceRows = window?.Processes.ToDictionary(static process => process.ProcessId);
        var rows = new List<ProcessMetricFieldValues>(samples.Count);
        foreach (var sample in samples.OrderBy(static sample => sample.ProcessId))
        {
            var birth = sample.StartKey is > 0 ? sample.StartKey.Value.ToString(CultureInfo.InvariantCulture) : null;
            ulong? receive = null, send = null;
            if (window is not null && birth is not null)
            {
                if (!sourceRows!.TryGetValue(sample.ProcessId, out var source))
                {
                    receive = 0;
                    send = 0;
                }
                else if (source.CreationWindowsFileTime100Ns == birth)
                {
                    receive = ReadCount(source.ReceiveBytes);
                    send = ReadCount(source.SendBytes);
                }
            }
            rows.Add(new(sample.ProcessId, birth,
                definitions.Select(field => CreateProcessNetworkValue(field, receive, send)).ToArray()));
        }
        return new(window?.CapturedAt, window?.WindowMilliseconds, definitions, rows.ToArray())
        { Sources = [new("windows.etw.network", window?.CapturedAt, window?.WindowMilliseconds)] };
    }

    private static ulong? ReadCount(string? value)
        => ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var count) ? count : null;

    private static ProcessMetricFieldValue CreateProcessNetworkValue(ProcessMetricFieldDefinition field,
        ulong? receive, ulong? send)
    {
        var count = field.Code is ProcessMetricFieldCode.NetworkReceiveBytes or ProcessMetricFieldCode.NetworkReceiveRate
            ? receive : send;
        return field.Code is ProcessMetricFieldCode.NetworkReceiveBytes or ProcessMetricFieldCode.NetworkSendBytes
            ? new(field.Name, count is { } integer ? new MetricInteger(integer) : null, null)
            : new(field.Name, null, count is { } observed ? observed / 2d : null);
    }
}

internal sealed record ProcessFieldProjectionSample(int ProcessId, long? StartKey, ProcessBasicMetricObservation BasicMetrics, double CpuPercent, bool HasCpuPercent);
