using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;

namespace ResourceManager.App.Endpoints.Transport;

public sealed record MetricSnapshotWireSnapshot(
    int Version,
    DateTimeOffset? CapturedAt,
    IReadOnlyDictionary<string, MetricValue> Items)
{
    public const int CurrentVersion = 4;

    public static MetricSnapshotWireSnapshot From(
        HardwareMetricSnapshot snapshot,
        MetricSampleRequest request)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(request);

        var definitions = MetricCatalog.FromSnapshot(snapshot)
            .Where(static definition => definition.Selectable)
            .ToDictionary(
                static definition => definition.Id,
                StringComparer.OrdinalIgnoreCase);
        var requestedMetricIds = ResolveRequestedMetricIds(
            snapshot,
            request,
            definitions.Keys);
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
            items);
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
            return current;
        }

        if (snapshot.Items.TryGetValue(metricId, out var known))
        {
            return known with
            {
                DisplayValue = "-",
                NumericValue = null,
                Percent = null
            };
        }

        return new MetricValue(
            metricId,
            definition?.Label ?? metricId,
            definition?.Group ?? string.Empty,
            "-",
            null,
            definition?.Unit ?? string.Empty,
            null,
            definition?.Detail);
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
