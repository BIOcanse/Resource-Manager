using System.Text.Json;
using System.Text.Json.Serialization;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Domain.Monitoring;

namespace ResourceManager.App.Endpoints.Transport;

[JsonConverter(typeof(ResourceProcessDetailsWireSnapshotJsonConverter))]
internal sealed class ResourceProcessDetailsWireSnapshot
{
    internal const int CurrentVersion = 1;

    private ResourceProcessDetailsWireSnapshot(
        DateTimeOffset? capturedAt,
        IReadOnlyList<ResourceProcessDetailsMetric> metrics)
    {
        CapturedAt = capturedAt;
        Metrics = metrics;
    }

    internal DateTimeOffset? CapturedAt { get; }

    internal IReadOnlyList<ResourceProcessDetailsMetric> Metrics { get; }

    internal static ResourceProcessDetailsWireSnapshot Create(
        ResourceBreakdownSnapshot source,
        IReadOnlyList<string> metricIds,
        IReadOnlySet<string> requestedSoftwareIds)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(metricIds);
        ArgumentNullException.ThrowIfNull(requestedSoftwareIds);

        var softwareIds = requestedSoftwareIds
            .Select(static id => id.Trim())
            .Where(static id => id.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (softwareIds.Count == 0)
        {
            return new ResourceProcessDetailsWireSnapshot(null, []);
        }

        var currentDatasets = source.Datasets
            .Where(static dataset =>
                dataset.Status == ResourceBreakdownSamplingStatuses.Ready
                && dataset.CapturedAt is not null)
            .ToDictionary(
                static dataset => dataset.DatasetId,
                StringComparer.OrdinalIgnoreCase);
        var barsByMetric = source.Bars.ToDictionary(
            static bar => bar.MetricId,
            StringComparer.OrdinalIgnoreCase);
        var metrics = new List<ResourceProcessDetailsMetric>();
        var seenMetricIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        DateTimeOffset? capturedAt = null;

        foreach (var requestedMetricId in metricIds)
        {
            if (string.IsNullOrWhiteSpace(requestedMetricId)
                || !seenMetricIds.Add(requestedMetricId)
                || !barsByMetric.TryGetValue(requestedMetricId, out var bar)
                || !currentDatasets.TryGetValue(
                    SamplingDatasetIds.ForProcessMetric(bar.MetricId),
                    out var dataset))
            {
                continue;
            }

            var software = bar.Software
                .Where(segment => softwareIds.Contains(segment.SoftwareId))
                .Select(static segment => new ResourceProcessDetailsSoftware(
                    segment.SoftwareId,
                    segment.Processes))
                .ToArray();
            if (software.Length == 0)
            {
                continue;
            }

            metrics.Add(new ResourceProcessDetailsMetric(bar.MetricId, software));
            if (capturedAt is null || dataset.CapturedAt > capturedAt)
            {
                capturedAt = dataset.CapturedAt;
            }
        }

        return new ResourceProcessDetailsWireSnapshot(capturedAt, metrics);
    }
}

internal sealed record ResourceProcessDetailsMetric(
    string MetricId,
    IReadOnlyList<ResourceProcessDetailsSoftware> Software);

internal sealed record ResourceProcessDetailsSoftware(
    string SoftwareId,
    IReadOnlyList<ResourceProcessSegment> Processes);

internal sealed class ResourceProcessDetailsWireSnapshotJsonConverter
    : JsonConverter<ResourceProcessDetailsWireSnapshot>
{
    public override ResourceProcessDetailsWireSnapshot Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
        => throw new NotSupportedException(
            "Resource process detail wire snapshots are write-only.");

    public override void Write(
        Utf8JsonWriter writer,
        ResourceProcessDetailsWireSnapshot value,
        JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteNumber("version", ResourceProcessDetailsWireSnapshot.CurrentVersion);
        if (value.CapturedAt is { } capturedAt)
        {
            writer.WriteString("capturedAt", capturedAt);
        }
        else
        {
            writer.WriteNull("capturedAt");
        }

        writer.WritePropertyName("metrics");
        writer.WriteStartArray();
        foreach (var metric in value.Metrics)
        {
            writer.WriteStartObject();
            writer.WriteString("metricId", metric.MetricId);
            writer.WritePropertyName("software");
            writer.WriteStartArray();
            foreach (var software in metric.Software)
            {
                writer.WriteStartObject();
                writer.WriteString("softwareId", software.SoftwareId);
                writer.WritePropertyName("processes");
                ResourceBreakdownWireSnapshotJsonConverter.WriteProcessRows(
                    writer,
                    software.Processes,
                    includeProcesses: true);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }
}
