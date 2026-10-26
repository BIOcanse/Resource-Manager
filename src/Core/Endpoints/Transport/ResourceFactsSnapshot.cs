using System.Globalization;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.ProcessAttribution;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.ReadWrite.ProcessFields;

namespace ResourceManager.App.Endpoints.Transport;

// Resource attribution facts have no row, column, sort or view identity.
internal sealed record ResourceFactsSnapshot(
    int Version, DateTimeOffset? CapturedAt, IReadOnlyList<ResourceSoftwareIdentity> Software,
    IReadOnlyList<ResourceProcessIdentity> Processes, IReadOnlyList<ResourceMetricFacts> Metrics,
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
        var software = new Dictionary<string, ResourceSoftwareIdentity>(StringComparer.Ordinal);
        var processes = new List<ResourceProcessIdentity>();
        var indexes = new Dictionary<(int, string?, string, string), int>();
        var attributions = new List<ResourceProcessAttribution>();
        var edges = new HashSet<(int, string)>();
        var metrics = new List<ResourceMetricFacts>();
        var hasFoundations = foundations is { InventoryStatus: SamplingObservationStatus.Current, HasSeparateFoundationPayloads: true };
        var inventory = hasFoundations ? foundations!.InventoryProcesses.ToDictionary(static process => (process.ProcessId, process.ProcessStartKey)) : [];
        var owners = hasFoundations ? foundations!.AttributionProcesses.ToDictionary(static process => (process.ProcessId, process.ProcessStartKey)) : [];
        int Index(ResourceProcessIdentity process)
        {
            var key = (process.ProcessId, process.ProcessStartKey, process.ProcessId > 0 ? "" : process.AttributionKind,
                process.ProcessId > 0 ? "" : process.Name);
            if (indexes.TryGetValue(key, out var index)) return index;
            index = processes.Count;
            indexes.Add(key, index);
            processes.Add(process);
            return index;
        }
        void Attribute(int index, string owner, double score)
        {
            if (edges.Add((index, owner))) attributions.Add(new(index, owner, score));
        }
        int FoundationIndex(SchedulingProcessFact identity, string kind) => Index(new(identity.ProcessId,
            identity.ProcessStartKey.ToString(CultureInfo.InvariantCulture), identity.ProcessName,
            identity.ExecutablePath, identity.UserName, identity.Architecture, kind));
        if (hasFoundations)
            foreach (var owner in foundations!.AttributionProcesses)
            {
                if (!inventory.TryGetValue((owner.ProcessId, owner.ProcessStartKey), out var identity)
                    || identity.ProcessId <= 0 || identity.ProcessStartKey == 0 || string.IsNullOrWhiteSpace(owner.SoftwareId)) continue;
                software.TryAdd(owner.SoftwareId, new(owner.SoftwareId, owner.SoftwareName, owner.SoftwareKind, owner.SoftwareDisplayKind));
                Attribute(FoundationIndex(identity, owner.SoftwareDisplayKind), owner.SoftwareId, owner.BaseScore);
            }

        ResourceMetricFacts Balance(ResourceMetricFacts metric, bool additive,
            IReadOnlyList<ResourceSoftwareSegment>? capturedResiduals = null)
        {
            // Sum exactly the instance values in this publication, after any newer
            // inventory has removed exited/reused instances. Never reuse a prior residual.
            var sum = metric.Software.Sum(static owner => owner.Processes.Sum(static process => process.Value));
            var residual = additive && metric.TotalValue is { } total ? total - sum : (double?)null;
            var values = metric.Software.ToList();
            if (residual is { } difference && difference != 0)
            {
                var id = "resource-residual:" + metric.TotalMetricId;
                software.TryAdd(id, new(id, string.Empty, "windows_system", "system-residual"));
                // Only the physical-memory residual has a captured counter partition.
                // Inventory filtering can change the difference, never the measured leaves.
                var physicalMemory = metric.MetricId.Equals(ResourceBreakdownMetricIds.MemoryUsage, StringComparison.OrdinalIgnoreCase)
                    || metric.MetricId.Equals("process_all_private_working_set_actual", StringComparison.OrdinalIgnoreCase);
                var counterParts = physicalMemory
                    ? capturedResiduals?.SelectMany(static owner => owner.Processes)
                        .Where(static row => row.AttributionKind.StartsWith("system-memory/", StringComparison.Ordinal)).ToArray() ?? []
                    : [];
                var measured = difference > 0
                    ? counterParts.Where(static row => row.AttributionKind != PhysicalMemoryResidualKinds.Unclassified).ToArray() : [];
                var parts = measured.Select(row => (row.Name, row.AttributionKind, row.Value)).ToList();
                var remainder = difference - parts.Sum(static row => row.Value);
                if (remainder != 0)
                    parts.Add((string.Empty, counterParts.Length > 0 && difference > 0
                        ? PhysicalMemoryResidualKinds.Unclassified : ResourceProcessAttributionKinds.SystemResidual, remainder));
                var rows = parts.Select(part =>
                {
                    var index = Index(new(-1, null, part.Name, null, null, null, part.AttributionKind));
                    Attribute(index, id, 0);
                    return new ResourceProcessFacts(index, part.Value, Percent(part.Value, metric.CapacityValue),
                        part.Value * 100 / difference, 0, null);
                }).ToArray();
                values.Add(new(id, difference, Percent(difference, metric.CapacityValue), 0, 0, null,
                    rows));
            }
            return metric with { ProcessSumValue = sum, ResidualValue = residual,
                AdditiveAcrossProcesses = additive, Software = values,
                UnavailableReason = metric.TotalValue is null ? "system-total-unavailable" : null };
        }

        static ResourceMetricFacts Unavailable(string id, string totalId, string unit, DateTimeOffset? capturedAt,
            string? quantity, string? field, string reason, bool additive = true)
            => new(id, unit, null, null, null, null, [])
            { TotalMetricId = totalId, CapturedAt = capturedAt, Quantity = quantity,
                ProcessFieldName = field, UnavailableReason = reason, AdditiveAcrossProcesses = additive };

        foreach (var bar in source.Bars)
        {
            if (!requested.Contains(bar.MetricId) || !current.TryGetValue(SamplingDatasetIds.ForProcessMetric(bar.MetricId), out var dataset)) continue;
            var isGpuMemory = bar.MetricId.StartsWith("gpu.", StringComparison.OrdinalIgnoreCase)
                && bar.MetricId.EndsWith(".vram", StringComparison.OrdinalIgnoreCase);
            if (isGpuMemory && (bar.TotalValue is null || bar.UnavailableReason is not null))
            {
                metrics.Add(Unavailable(bar.MetricId, bar.MetricId, bar.Unit, dataset.CapturedAt,
                    bar.Quantity ?? "dedicated_memory_used", bar.ProcessFieldName,
                    bar.UnavailableReason ?? "gpu-dedicated-memory-total-unavailable"));
                continue;
            }
            var values = new List<ResourceSoftwareFacts>();
            foreach (var owner in bar.Software)
            {
                var rows = new List<ResourceProcessFacts>();
                foreach (var process in owner.Processes)
                {
                    if (process.ProcessId <= 0 || process.AttributionKind == ResourceProcessAttributionKinds.SystemResidual) continue;
                    if (hasFoundations && (process.ProcessStartKey is not > 0
                        || !inventory.ContainsKey((process.ProcessId, (ulong)process.ProcessStartKey.Value)))) continue;
                    software.TryAdd(owner.SoftwareId, new(owner.SoftwareId, owner.Name, owner.Kind, owner.DisplayKind));
                    var index = Index(new(process.ProcessId, process.ProcessStartKey is > 0
                        ? process.ProcessStartKey.Value.ToString(CultureInfo.InvariantCulture) : null,
                        process.Name, process.ExecutablePath, process.UserName, process.Architecture, process.AttributionKind));
                    rows.Add(new(index, process.Value, process.SystemPercent, process.SoftwarePercent, process.BaseScore, process.SharedValue));
                }
                if (rows.Count == 0) continue;
                var sum = rows.Sum(static row => row.Value);
                values.Add(new(owner.SoftwareId, sum, Percent(sum, bar.CapacityValue), rows.Count,
                    owner.BaseScore, owner.SharedValue is null ? null : rows.Sum(static row => row.SharedValue ?? 0),
                    rows.Select(row => row with { SoftwarePercent = sum > 0 ? row.Value * 100 / sum : 0 }).ToArray()));
            }
            var field = bar.ProcessFieldName;
            var additive = field is null || ProcessMetricFieldRegistry.Find(field)?.AdditiveAcrossProcesses != false;
            metrics.Add(Balance(new(bar.MetricId, bar.Unit, bar.TotalValue, bar.CapacityValue,
                bar.TotalSystemPercent, bar.SharedValue is null ? null : values.Sum(static owner => owner.SharedValue ?? 0), values)
            { ProcessFieldName = field, TotalMetricId = bar.MetricId, CapturedAt = dataset.CapturedAt,
                ResidualSource = bar.MetricId.Equals(ResourceBreakdownMetricIds.MemoryUsage, StringComparison.OrdinalIgnoreCase) ? bar.ResidualSource : null,
                Quantity = bar.Quantity ?? (isGpuMemory ? "dedicated_memory_used" : ProcessMetricFieldRegistry.Find(field ?? string.Empty)?.Reading?.Quantity) }, additive,
                bar.Software.Where(static owner => owner.SoftwareId.Equals("resource-residual:memory.usage", StringComparison.OrdinalIgnoreCase)).ToArray()));
        }
        foreach (var fact in source.ProcessMemoryFacts)
        {
            if (!requested.Contains(fact.MetricId) || !current.TryGetValue(SamplingDatasetIds.ForProcessMetric(fact.MetricId), out var dataset)) continue;
            var definition = ProcessMetricFieldRegistry.Find(fact.MetricId);
            if (definition?.GpuIndex is not null && (fact.CapturedAt is null || fact.TotalValue is null))
            {
                metrics.Add(Unavailable(fact.MetricId, fact.TotalMetricId, fact.Unit, fact.CapturedAt,
                    definition.Reading?.Quantity, fact.MetricId,
                    fact.CapturedAt is null ? "gpu-measure-observation-unavailable" : "gpu-measure-total-unavailable",
                    definition.AdditiveAcrossProcesses == true) with
                { GpuEngine = definition.GpuEngine, EngineClass = fact.EngineClass ?? definition.EngineClass });
                continue;
            }
            var groups = new Dictionary<string, List<ResourceProcessFacts>>(StringComparer.Ordinal);
            foreach (var value in fact.Processes)
            {
                if (value.ProcessStartKey <= 0 || !inventory.TryGetValue((value.ProcessId, (ulong)value.ProcessStartKey), out var identity)) continue;
                owners.TryGetValue((identity.ProcessId, identity.ProcessStartKey), out var owner);
                var unknown = RuntimeSoftwareAttribution.Unattributed;
                var id = owner?.SoftwareId ?? unknown.Id;
                var kind = owner?.SoftwareDisplayKind ?? unknown.DisplayKind;
                software.TryAdd(id, new(id, owner?.SoftwareName ?? unknown.Name, owner?.SoftwareKind ?? unknown.Kind, kind));
                var index = FoundationIndex(identity, kind);
                Attribute(index, id, owner?.BaseScore ?? 0);
                if (!groups.TryGetValue(id, out var group)) groups.Add(id, group = []);
                group.Add(new(index, value.Value, Percent(value.Value, fact.CapacityValue), 0, owner?.BaseScore ?? 0, null));
            }
            var values = groups.Select(group =>
            {
                var sum = group.Value.Sum(static row => row.Value);
                return new ResourceSoftwareFacts(group.Key, sum, Percent(sum, fact.CapacityValue), group.Value.Count,
                    group.Value.Max(static row => row.BaseScore), null,
                    group.Value.Select(row => row with { SoftwarePercent = sum > 0 ? row.Value * 100 / sum : 0 }).ToArray());
            }).ToArray();
            metrics.Add(Balance(new(fact.MetricId, fact.Unit, fact.TotalValue, fact.CapacityValue,
                fact.TotalValue is { } total && fact.CapacityValue is > 0 ? total * 100 / fact.CapacityValue.Value : null,
                null, values)
            {
                TotalMetricId = fact.TotalMetricId, ProcessFieldName = fact.MetricId,
                ResidualSource = fact.MetricId == "process_all_private_working_set_actual" ? "counters" : null,
                CapturedAt = fact.CapturedAt ?? dataset.CapturedAt, WindowMilliseconds = fact.WindowMilliseconds,
                GpuEngine = fact.GpuEngine, EngineClass = fact.EngineClass, Quantity = definition?.Reading?.Quantity
            }, ProcessMetricFieldRegistry.Find(fact.MetricId)?.AdditiveAcrossProcesses ?? fact.AdditiveAcrossProcesses, fact.Residuals));
        }
        var capturedAt = metrics.Select(static metric => metric.CapturedAt).DefaultIfEmpty(null).Max();
        if (hasFoundations && metrics.Count == 0 && foundations!.ObservedAtUtcTicks > 0 && attributions.Count > 0)
            capturedAt = new DateTimeOffset(foundations.ObservedAtUtcTicks, TimeSpan.Zero);
        return new(2, capturedAt, software.Values.ToArray(), processes, metrics, attributions);
    }

    private static double Percent(double value, double? capacity) => capacity is > 0 ? value * 100 / capacity.Value : 0;
}

internal sealed record ResourceMetricFacts(string MetricId, string Unit, double? TotalValue,
    double? CapacityValue, double? TotalSystemPercent, double? SharedValue, IReadOnlyList<ResourceSoftwareFacts> Software)
{
    public string? ResidualSource { get; init; }
    public string? Quantity { get; init; }
    public string? UnavailableReason { get; init; }
    public string? TotalMetricId { get; init; }
    public double? ProcessSumValue { get; init; }
    public double? ResidualValue { get; init; }
    public bool AdditiveAcrossProcesses { get; init; }
    public DateTimeOffset? CapturedAt { get; init; }
    public long? WindowMilliseconds { get; init; }
    public ProcessGpuEngineScope? GpuEngine { get; init; }
    public string? EngineClass { get; init; }
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
