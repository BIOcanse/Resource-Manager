using System.Globalization;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.ProcessAttribution;
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
                && current.ContainsKey(SamplingDatasetIds.ForProcessMetric(bar.MetricId))).ToArray();
        var software = bars.SelectMany(static bar => bar.Software).DistinctBy(static item => item.SoftwareId)
            .ToDictionary(static item => item.SoftwareId,
                static item => new ResourceSoftwareIdentity(item.SoftwareId, item.Name, item.Kind, item.DisplayKind), StringComparer.Ordinal);
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
        ResourceSoftwareFacts ProjectSoftware(ResourceSoftwareSegment owner) => new(owner.SoftwareId,
            owner.Value, owner.SystemPercent, owner.ProcessCount, owner.BaseScore, owner.SharedValue,
            owner.Processes.Select(process => new ResourceProcessFacts(ProcessIndex(process), process.Value,
                process.SystemPercent, process.SoftwarePercent, process.BaseScore, process.SharedValue)).ToArray());
        var metrics = bars.Select(bar => new ResourceMetricFacts(bar.MetricId, bar.Unit,
            bar.TotalValue, bar.CapacityValue, bar.TotalSystemPercent, bar.SharedValue,
            bar.Software.Select(ProjectSoftware).ToArray()) { ProcessFieldName = bar.ProcessFieldName }).ToList();
        var attributions = new List<ResourceProcessAttribution>();
        var edges = new HashSet<(int, string)>();
        void Attribute(int index, string owner, double score)
        {
            if (edges.Add((index, owner))) attributions.Add(new(index, owner, score));
        }
        var processIndexes = processes.Select((process, index) => (process, index))
            .Where(static item => item.process.ProcessId > 0 && item.process.ProcessStartKey is not null)
            .GroupBy(static item => (item.process.ProcessId, item.process.ProcessStartKey))
            .ToDictionary(static group => group.Key, static group => group.First().index);
        int FoundationIndex(SchedulingProcessFact identity, string kind)
        {
            var key = (identity.ProcessId, identity.ProcessStartKey.ToString(CultureInfo.InvariantCulture));
            if (processIndexes.TryGetValue(key, out var index)) return index;
            index = processes.Count;
            processIndexes.Add(key, index);
            processes.Add(new(identity.ProcessId, key.Item2, identity.ProcessName, identity.ExecutablePath,
                identity.UserName, identity.Architecture, kind));
            return index;
        }
        var hasFoundations = foundations is { InventoryStatus: SamplingObservationStatus.Current, HasSeparateFoundationPayloads: true };
        var inventory = hasFoundations ? foundations!.InventoryProcesses.ToDictionary(static process => (process.ProcessId, process.ProcessStartKey)) : [];
        var owners = hasFoundations ? foundations!.AttributionProcesses.ToDictionary(static process => (process.ProcessId, process.ProcessStartKey)) : [];
        if (hasFoundations)
            foreach (var fact in foundations!.AttributionProcesses)
            {
                if (!inventory.TryGetValue((fact.ProcessId, fact.ProcessStartKey), out var process)
                    || process.ProcessId <= 0 || process.ProcessStartKey == 0 || string.IsNullOrWhiteSpace(fact.SoftwareId)) continue;
                software.TryAdd(fact.SoftwareId, new(fact.SoftwareId, fact.SoftwareName, fact.SoftwareKind, fact.SoftwareDisplayKind));
                Attribute(FoundationIndex(process, fact.SoftwareDisplayKind), fact.SoftwareId, fact.BaseScore);
            }
        foreach (var fact in source.ProcessMemoryFacts)
        {
            if (!requested.Contains(fact.MetricId) || !current.ContainsKey(SamplingDatasetIds.ForProcessMetric(fact.MetricId))) continue;
            var groups = new Dictionary<string, List<(ProcessMemoryValue Value, int Index, double Score)>>(StringComparer.Ordinal);
            foreach (var value in fact.Processes)
            {
                if (value.ProcessStartKey <= 0 || !inventory.TryGetValue((value.ProcessId, (ulong)value.ProcessStartKey), out var identity)) continue;
                owners.TryGetValue((identity.ProcessId, identity.ProcessStartKey), out var owner);
                // A current attribution foundation has no owner row for an unattributed process.
                var unassigned = RuntimeSoftwareAttribution.Unattributed;
                var ownerId = owner?.SoftwareId ?? unassigned.Id;
                var kind = owner?.SoftwareDisplayKind ?? unassigned.DisplayKind;
                software.TryAdd(ownerId, new(ownerId, owner?.SoftwareName ?? unassigned.Name,
                    owner?.SoftwareKind ?? unassigned.Kind, kind));
                var index = FoundationIndex(identity, kind);
                Attribute(index, ownerId, owner?.BaseScore ?? 0);
                if (!groups.TryGetValue(ownerId, out var group)) groups.Add(ownerId, group = []);
                group.Add((value, index, owner?.BaseScore ?? 0));
            }
            var values = groups.Select(group =>
            {
                var sum = group.Value.Sum(static row => row.Value.Value);
                return new ResourceSoftwareFacts(group.Key, sum, Percent(sum, fact.CapacityValue), group.Value.Count,
                    group.Value.Max(static row => row.Score), null, group.Value.Select(row => new ResourceProcessFacts(
                        row.Index, row.Value.Value, Percent(row.Value.Value, fact.CapacityValue),
                        sum > 0 ? row.Value.Value * 100 / sum : 0, row.Score, null)).ToArray());
            }).ToList();
            foreach (var residual in fact.Residuals)
            {
                software.TryAdd(residual.SoftwareId, new(residual.SoftwareId, residual.Name, residual.Kind, residual.DisplayKind));
                var projected = ProjectSoftware(residual);
                values.Add(projected);
                foreach (var process in projected.Processes) Attribute(process.ProcessIndex, residual.SoftwareId, process.BaseScore);
            }
            metrics.Add(new(fact.MetricId, "B", fact.TotalValue, fact.CapacityValue,
                fact.TotalValue is { } total && fact.CapacityValue is > 0 ? total * 100 / fact.CapacityValue.Value : null,
                null, values) { TotalMetricId = fact.TotalMetricId, ProcessSumValue = fact.ProcessSumValue,
                    ProcessFieldName = fact.MetricId });
        }
        var capturedAt = metrics.Select(metric => current[SamplingDatasetIds.ForProcessMetric(metric.MetricId)].CapturedAt)
            .DefaultIfEmpty(null).Max();
        if (hasFoundations && foundations!.ObservedAtUtcTicks > 0 && attributions.Count > 0)
            capturedAt = new DateTimeOffset(foundations.ObservedAtUtcTicks, TimeSpan.Zero);
        return new ResourceFactsSnapshot(2, capturedAt, software.Values.ToArray(), processes, metrics, attributions);
    }

    private static double Percent(double value, double? capacity) => capacity is > 0 ? Math.Clamp(value * 100 / capacity.Value, 0, 100) : 0;
}

internal sealed record ResourceMetricFacts(string MetricId, string Unit, double? TotalValue,
    double? CapacityValue, double? TotalSystemPercent, double? SharedValue, IReadOnlyList<ResourceSoftwareFacts> Software)
{
    public string? TotalMetricId { get; init; }
    public double? ProcessSumValue { get; init; }
    public string? ProcessFieldName { get; init; }
}
internal sealed record ResourceSoftwareIdentity(string SoftwareId, string Name, string Kind, string AttributionKind);
internal sealed record ResourceProcessIdentity(int ProcessId, string? ProcessStartKey, string Name,
    string? ExecutablePath, string? UserName, string? Architecture, string AttributionKind);
internal sealed record ResourceSoftwareFacts(string SoftwareId,
    double Value, double SystemPercent, int ProcessCount, double BaseScore, double? SharedValue,
    IReadOnlyList<ResourceProcessFacts> Processes);
internal sealed record ResourceProcessFacts(int ProcessIndex,
    double Value, double SystemPercent, double SoftwarePercent, double BaseScore, double? SharedValue);
internal sealed record ResourceProcessAttribution(int ProcessIndex, string SoftwareId, double BaseScore);
