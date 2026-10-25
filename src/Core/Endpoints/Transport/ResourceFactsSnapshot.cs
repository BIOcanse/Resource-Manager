using System.Globalization;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.ResourceBreakdown;

namespace ResourceManager.App.Endpoints.Transport;

// Resource attribution facts have no row, column, sort or view identity.
internal sealed record ResourceFactsSnapshot(
    int Version,
    DateTimeOffset? CapturedAt,
    IReadOnlyList<ResourceSoftwareIdentity> Software,
    IReadOnlyList<ResourceProcessIdentity> Processes,
    IReadOnlyList<ResourceMetricFacts> Metrics,
    IReadOnlyList<ResourceProcessAttribution> Attributions)
{
    internal static ResourceFactsSnapshot Empty { get; } = new(2, null, [], [], [], []);

    internal static ResourceFactsSnapshot Create(ResourceBreakdownSnapshot source, IReadOnlyList<string> items,
        SchedulingProcessFactSnapshot? foundations = null)
    {
        var requested = items.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var current = source.Datasets.Where(static dataset =>
            dataset.Status == ResourceBreakdownSamplingStatuses.Ready && dataset.CapturedAt is not null)
            .ToDictionary(static dataset => dataset.DatasetId, StringComparer.OrdinalIgnoreCase);
        var bars = source.Bars.Where(bar => requested.Contains(bar.MetricId)
                && current.ContainsKey(SamplingDatasetIds.ForProcessMetric(bar.MetricId)))
            .ToArray();
        var software = bars.SelectMany(static bar => bar.Software).DistinctBy(static item => item.SoftwareId)
            .Select(static item => new ResourceSoftwareIdentity(item.SoftwareId, item.Name, item.Kind, item.DisplayKind)).ToArray();
        var processes = new List<ResourceProcessIdentity>();
        var indexes = new Dictionary<(int, long?, string, string), int>();
        int ProcessIndex(ResourceProcessSegment process)
        {
            var key = (process.ProcessId, process.ProcessStartKey, process.AttributionKind, process.Name);
            if (indexes.TryGetValue(key, out var index)) return index;
            index = processes.Count;
            indexes.Add(key, index);
            processes.Add(new ResourceProcessIdentity(process.ProcessId, process.ProcessStartKey is > 0
                ? process.ProcessStartKey.Value.ToString(CultureInfo.InvariantCulture) : null,
                process.Name, process.ExecutablePath, process.UserName, process.Architecture, process.AttributionKind));
            return index;
        }
        var metrics = bars.Select(bar => new ResourceMetricFacts(bar.MetricId, bar.Unit,
                bar.TotalValue, bar.CapacityValue, bar.TotalSystemPercent, bar.SharedValue,
                bar.Software.Select(software => new ResourceSoftwareFacts(
                    software.SoftwareId,
                    software.Value, software.SystemPercent, software.ProcessCount, software.BaseScore,
                    software.SharedValue, software.Processes.Select(process => new ResourceProcessFacts(
                        ProcessIndex(process), process.Value, process.SystemPercent,
                        process.SoftwarePercent, process.BaseScore, process.SharedValue)).ToArray())).ToArray()))
            .ToArray();
        var attributions = new List<ResourceProcessAttribution>();
        if (foundations is { InventoryStatus: SamplingObservationStatus.Current, HasSeparateFoundationPayloads: true })
        {
            var inventory = foundations.InventoryProcesses.ToDictionary(static process => (process.ProcessId, process.ProcessStartKey));
            var softwareById = software.ToDictionary(static item => item.SoftwareId, StringComparer.Ordinal);
            var processIndexes = processes.Select((process, index) => (process, index))
                .Where(static item => item.process.ProcessId > 0 && item.process.ProcessStartKey is not null)
                .GroupBy(static item => (item.process.ProcessId, item.process.ProcessStartKey))
                .ToDictionary(static group => group.Key, static group => group.First().index);
            foreach (var fact in foundations.AttributionProcesses)
            {
                if (!inventory.TryGetValue((fact.ProcessId, fact.ProcessStartKey), out var process)
                    || process.ProcessId <= 0 || process.ProcessStartKey == 0 || string.IsNullOrWhiteSpace(fact.SoftwareId)) continue;
                softwareById.TryAdd(fact.SoftwareId, new(fact.SoftwareId, fact.SoftwareName, fact.SoftwareKind, fact.SoftwareDisplayKind));
                var startKey = process.ProcessStartKey.ToString(CultureInfo.InvariantCulture);
                if (!processIndexes.TryGetValue((process.ProcessId, startKey), out var processIndex))
                {
                    processIndex = processes.Count;
                    processIndexes.Add((process.ProcessId, startKey), processIndex);
                    processes.Add(new(process.ProcessId, startKey,
                        process.ProcessName, process.ExecutablePath, process.UserName, process.Architecture, fact.SoftwareDisplayKind));
                }
                attributions.Add(new(processIndex, fact.SoftwareId, fact.BaseScore));
            }
            software = softwareById.Values.ToArray();
        }
        var capturedAt = metrics.Select(metric => current[SamplingDatasetIds.ForProcessMetric(metric.MetricId)].CapturedAt)
            .DefaultIfEmpty(null).Max();
        if (foundations is { ObservedAtUtcTicks: > 0 } && attributions.Count > 0)
            capturedAt = new DateTimeOffset(foundations.ObservedAtUtcTicks, TimeSpan.Zero);
        return new ResourceFactsSnapshot(2, capturedAt, software, processes, metrics, attributions);
    }
}

internal sealed record ResourceMetricFacts(string MetricId, string Unit, double? TotalValue,
    double? CapacityValue, double? TotalSystemPercent, double? SharedValue, IReadOnlyList<ResourceSoftwareFacts> Software);
internal sealed record ResourceSoftwareIdentity(string SoftwareId, string Name, string Kind, string AttributionKind);
internal sealed record ResourceProcessIdentity(int ProcessId, string? ProcessStartKey, string Name,
    string? ExecutablePath, string? UserName, string? Architecture, string AttributionKind);
internal sealed record ResourceSoftwareFacts(string SoftwareId,
    double Value, double SystemPercent, int ProcessCount, double BaseScore, double? SharedValue,
    IReadOnlyList<ResourceProcessFacts> Processes);
internal sealed record ResourceProcessFacts(int ProcessIndex,
    double Value, double SystemPercent, double SoftwarePercent, double BaseScore, double? SharedValue);
internal sealed record ResourceProcessAttribution(int ProcessIndex, string SoftwareId, double BaseScore);
