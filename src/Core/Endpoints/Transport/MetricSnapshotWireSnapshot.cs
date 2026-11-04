using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;

namespace ResourceManager.App.Endpoints.Transport;

public sealed record MetricSnapshotWireSnapshot(
    int Version,
    DateTimeOffset? CapturedAt,
    IReadOnlyDictionary<string, MetricValue> Items,
    string CatalogRevision = "0")
{
    public const int CurrentVersion = 5;

    public static MetricSnapshotWireSnapshot From(
        HardwareMetricSnapshot snapshot,
        MetricSampleRequest request,
        string catalogRevision = "0")
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(request);

        var definitions = MetricCatalog.FromSnapshot(snapshot)
            .ToDictionary(
                static definition => definition.Id,
                StringComparer.OrdinalIgnoreCase);
        var requestedMetricIds = ResolveRequestedMetricIds(
            snapshot,
            request,
            definitions.Values.Where(static definition => definition.Selectable)
                .Select(static definition => definition.Id));
        var items = requestedMetricIds.ToDictionary(
            static metricId => metricId,
            metricId => CurrentValueOrPlaceholder(
                snapshot,
                definitions.GetValueOrDefault(metricId),
                metricId),
            StringComparer.OrdinalIgnoreCase);
        var capturedAt = requestedMetricIds
            .Select(SamplingDatasetIds.ForSystemMetric)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(datasetId => snapshot.Datasets.TryGetValue(
                    datasetId,
                    out var observation)
                && observation.Status == SamplingObservationStatus.Current
                && observation.ObservedAtUtcTicks > 0
                    ? new DateTimeOffset(
                        observation.ObservedAtUtcTicks,
                        TimeSpan.Zero)
                    : (DateTimeOffset?)null)
            .Where(static observedAt => observedAt is not null)
            .DefaultIfEmpty(null)
            .Max();
        return new MetricSnapshotWireSnapshot(
            CurrentVersion,
            capturedAt,
            items,
            catalogRevision);
    }

    private static IReadOnlyList<string> ResolveRequestedMetricIds(
        HardwareMetricSnapshot snapshot,
        MetricSampleRequest request,
        IEnumerable<string> catalogMetricIds)
    {
        if (request.IsAll || request.IsCatalogProbe)
        {
            return snapshot.Items.Keys
                .Concat(catalogMetricIds)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        var canonicalIds = snapshot.Items.Keys
            .Concat(catalogMetricIds)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                static metricId => metricId,
                static metricId => metricId,
                StringComparer.OrdinalIgnoreCase);
        var ids = request.Ids
            .Select(metricId => canonicalIds.GetValueOrDefault(metricId, metricId))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (request.IncludesAllGpuCoreMetrics)
        {
            ids.UnionWith(catalogMetricIds.Where(IsGpuCoreMetric));
        }
        return ids.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static MetricValue CurrentValueOrPlaceholder(
        HardwareMetricSnapshot snapshot,
        MetricDefinition? definition,
        string metricId)
    {
        var datasetId = SamplingDatasetIds.ForSystemMetric(metricId);
        if (snapshot.Datasets.TryGetValue(datasetId, out var observation)
            && observation.Status == SamplingObservationStatus.Current
            && snapshot.Items.TryGetValue(metricId, out var current))
        {
            return MetricValue.From(current) with
            {
                Status = observation.Status,
                ObservedAt = current.ObservedAt ?? (observation.ObservedAtUtcTicks > 0
                    ? new DateTimeOffset(observation.ObservedAtUtcTicks, TimeSpan.Zero) : null)
            };
        }

        if (snapshot.Items.TryGetValue(metricId, out var known))
        {
            return MetricValue.From(known) with
            {
                NumericValue = null,
                IntegerValue = null,
                BytesValue = null,
                SampleDurationMilliseconds = null,
                Percent = null,
                BooleanValue = null,
                Total = null,
                Status = observation?.Status ?? SamplingObservationStatus.NotRequested,
                ObservedAt = null
            };
        }

        return new MetricValue(Id: metricId, NumericValue: null, Unit: definition?.Unit ?? string.Empty, Percent: null)
        {
            FieldCode = definition?.FieldCode ?? default,
            FieldName = definition?.FieldName, DeviceIdentity = definition?.DeviceIdentity,
            ValueKind = definition?.ValueKind, SourceId = definition?.SourceId,
            Status = observation?.Status ?? SamplingObservationStatus.NotRequested
        };
    }

    private static bool IsGpuCoreMetric(string metricId)
    {
        if (!metricId.StartsWith("gpu.", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        var separator = metricId.IndexOf('.', "gpu.".Length);
        if (separator <= "gpu.".Length)
        {
            return false;
        }
        var name = metricId[(separator + 1)..];
        return name.Equals("usage", StringComparison.OrdinalIgnoreCase)
            || name.Equals("vram", StringComparison.OrdinalIgnoreCase)
            || name.Equals("vramPercent", StringComparison.OrdinalIgnoreCase);
    }
}
