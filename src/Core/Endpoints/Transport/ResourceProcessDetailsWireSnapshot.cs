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
        IReadOnlyList<ResourceProcessDetailsMetric> metrics, PhysicalMemoryResidualFrame? physicalMemoryResidual = null)
    {
        CapturedAt = capturedAt;
        Metrics = metrics;
        PhysicalMemoryResidual = physicalMemoryResidual;
    }

    internal PhysicalMemoryResidualFrame? PhysicalMemoryResidual { get; }
    internal DateTimeOffset? CapturedAt { get; }

    internal IReadOnlyList<ResourceProcessDetailsMetric> Metrics { get; }

    internal static ResourceProcessDetailsWireSnapshot Create(
        ResourceBreakdownSnapshot source,
        IReadOnlyList<string> metricIds,
        IReadOnlySet<string> requestedSoftwareIds,
        SchedulingProcessFactSnapshot? foundations = null,
        IReadOnlyDictionary<string, string>? scaleModes = null)
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
        var barsByMetric = ResourceBreakdownWireSnapshot.ProjectResourceBars(
            source, metricIds, foundations, scaleModes).ToDictionary(
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
            // Owner filtering restricts details, not the captured metric's balance.
            // A current metric can be entirely residual without a requested process.

            metrics.Add(new ResourceProcessDetailsMetric(bar.MetricId, software)
            {
                TotalMetricId = bar.TotalMetricId, TotalValue = bar.TotalValue, CapacityValue = bar.CapacityValue,
                ProcessSumValue = bar.ProcessSumValue, ResidualValue = bar.ResidualValue,
                AdditiveAcrossProcesses = bar.AdditiveAcrossProcesses, CapturedAt = bar.CapturedAt
            });
            if (capturedAt is null || dataset.CapturedAt > capturedAt)
            {
                capturedAt = dataset.CapturedAt;
            }
        }

        return new ResourceProcessDetailsWireSnapshot(capturedAt ?? source.PhysicalMemoryResidual?.CapturedAt, metrics,
            softwareIds.Contains("resource-residual:memory.usage") ? source.PhysicalMemoryResidual : null);
    }
}

internal sealed record ResourceProcessDetailsMetric(
    string MetricId,
    IReadOnlyList<ResourceProcessDetailsSoftware> Software)
{
    public string? TotalMetricId { get; init; }
    public double? TotalValue { get; init; }
    public double? CapacityValue { get; init; }
    public double? ProcessSumValue { get; init; }
    public double? ResidualValue { get; init; }
    public bool AdditiveAcrossProcesses { get; init; }
    public DateTimeOffset? CapturedAt { get; init; }
}

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

        if (value.PhysicalMemoryResidual is { } physical)
        {
            writer.WritePropertyName("physicalMemoryResidual");
            JsonSerializer.Serialize(writer, physical, options);
        }
        writer.WritePropertyName("metrics");
        writer.WriteStartArray();
        foreach (var metric in value.Metrics)
        {
            writer.WriteStartObject();
            writer.WriteString("metricId", metric.MetricId);
            writer.WriteString("totalMetricId", metric.TotalMetricId);
            WriteNumber(writer, "totalValue", metric.TotalValue);
            WriteNumber(writer, "capacityValue", metric.CapacityValue);
            WriteNumber(writer, "processSumValue", metric.ProcessSumValue);
            WriteNumber(writer, "residualValue", metric.ResidualValue);
            writer.WriteBoolean("additiveAcrossProcesses", metric.AdditiveAcrossProcesses);
            if (metric.CapturedAt is { } time) writer.WriteString("capturedAt", time);
            else writer.WriteNull("capturedAt");
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

    private static void WriteNumber(Utf8JsonWriter writer, string name, double? value)
    {
        if (value is { } number) writer.WriteNumber(name, number);
        else writer.WriteNull(name);
    }
}
