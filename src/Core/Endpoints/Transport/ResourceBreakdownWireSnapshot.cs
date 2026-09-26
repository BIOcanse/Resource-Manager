using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.ResourceBreakdown;

namespace ResourceManager.App.Endpoints.Transport;

[JsonConverter(typeof(ResourceBreakdownWireSnapshotJsonConverter))]
public sealed class ResourceBreakdownWireSnapshot
{
    public const int CurrentVersion = 8;

    private ResourceBreakdownWireSnapshot(
        DateTimeOffset? capturedAt,
        IReadOnlyList<ResourceBreakdownBar> bars,
        IReadOnlyList<ResourceSoftwareSegment> softwareCatalog,
        IReadOnlyDictionary<string, int> softwareIndexes,
        IReadOnlySet<string> processDetailSoftwareIds)
    {
        CapturedAt = capturedAt;
        Bars = bars;
        SoftwareCatalog = softwareCatalog;
        SoftwareIndexes = softwareIndexes;
        ProcessDetailSoftwareIds = processDetailSoftwareIds;
    }

    internal DateTimeOffset? CapturedAt { get; }

    internal IReadOnlyList<ResourceBreakdownBar> Bars { get; }

    internal IReadOnlyList<ResourceSoftwareSegment> SoftwareCatalog { get; }

    internal IReadOnlyDictionary<string, int> SoftwareIndexes { get; }

    internal IReadOnlySet<string> ProcessDetailSoftwareIds { get; }

    internal static ResourceBreakdownWireSnapshot Create(
        ResourceBreakdownSnapshot source,
        IReadOnlyList<string> visibleMetricIds,
        IReadOnlySet<string>? processDetailSoftwareIds = null)
    {
        var visibleIds = visibleMetricIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var currentDatasets = source.Datasets
            .Where(static dataset =>
                dataset.Status == ResourceBreakdownSamplingStatuses.Ready
                && dataset.CapturedAt is not null)
            .ToDictionary(
                static dataset => dataset.DatasetId,
                StringComparer.OrdinalIgnoreCase);
        // **只发布有数的条目。**
        //
        // 线上的合同是"条目带着数值总量"，消费端据此直接用，不做空值判断。
        // 读不到的指标不是"一条总量为 null 的条目"，而是**没有这一条** ——
        // 发一条空的出去，消费端解码就会失败，而那不是失败一条，是整帧作废：
        // 一个指标不可用会把同一帧里其它所有指标一起带走。
        // 实测就这么炸过：核显那条不可用，GPU 调度页整页无限加载。
        var bars = visibleIds.Count == 0
            ? []
            : source.Bars.Where(bar => visibleIds.Contains(bar.MetricId)
                && currentDatasets.ContainsKey(
                    SamplingDatasetIds.ForProcessMetric(bar.MetricId))
                && bar.TotalValue is not null
                && bar.CapacityValue is not null
                && bar.TotalSystemPercent is not null)
                .ToArray();
        var catalog = new List<ResourceSoftwareSegment>();
        var indexes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var segment in bars.SelectMany(static bar => bar.Software))
        {
            if (indexes.TryAdd(segment.SoftwareId, catalog.Count))
            {
                catalog.Add(segment);
            }
        }

        var capturedAt = visibleIds
            .Select(SamplingDatasetIds.ForProcessMetric)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(currentDatasets.ContainsKey)
            .Select(datasetId => currentDatasets[datasetId].CapturedAt)
            .DefaultIfEmpty(null)
            .Max();
        return new ResourceBreakdownWireSnapshot(
            capturedAt,
            bars,
            catalog,
            indexes,
            processDetailSoftwareIds
                ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    }
}

public sealed class ResourceBreakdownWireSnapshotJsonConverter
    : JsonConverter<ResourceBreakdownWireSnapshot>
{
    public override ResourceBreakdownWireSnapshot Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
        => throw new NotSupportedException(
            "Resource breakdown wire snapshots are write-only.");

    public override void Write(
        Utf8JsonWriter writer,
        ResourceBreakdownWireSnapshot value,
        JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteNumber("version", ResourceBreakdownWireSnapshot.CurrentVersion);
        WriteNullableTimestamp(writer, "capturedAt", value.CapturedAt);
        WriteSoftwareCatalog(writer, value.SoftwareCatalog);
        WriteBars(writer, value);
        writer.WriteEndObject();
    }

    private static void WriteSoftwareCatalog(
        Utf8JsonWriter writer,
        IReadOnlyList<ResourceSoftwareSegment> catalog)
    {
        writer.WritePropertyName("softwareCatalog");
        writer.WriteStartArray();
        foreach (var software in catalog)
        {
            writer.WriteStartArray();
            writer.WriteStringValue(software.SoftwareId);
            writer.WriteStringValue(software.Name);
            writer.WriteStringValue(software.Kind);
            writer.WriteStringValue(software.DisplayKind);
            writer.WriteEndArray();
        }
        writer.WriteEndArray();
    }

    private static void WriteBars(
        Utf8JsonWriter writer,
        ResourceBreakdownWireSnapshot value)
    {
        writer.WritePropertyName("bars");
        writer.WriteStartArray();
        foreach (var bar in value.Bars)
        {
            writer.WriteStartObject();
            writer.WriteString("metricId", bar.MetricId);
            writer.WriteString("label", bar.Label);
            writer.WriteString("unit", bar.Unit);
            writer.WriteString("scaleMode", bar.ScaleMode);
            WriteNullableNumber(writer, "totalValue", bar.TotalValue);
            WriteNullableNumber(writer, "capacityValue", bar.CapacityValue);
            WriteNullableNumber(writer, "totalSystemPercent", bar.TotalSystemPercent);
            WriteNullableNumber(writer, "sharedValue", bar.SharedValue);
            WriteSoftwareRows(writer, value, bar.Software);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteSoftwareRows(
        Utf8JsonWriter writer,
        ResourceBreakdownWireSnapshot value,
        IReadOnlyList<ResourceSoftwareSegment> software)
    {
        writer.WritePropertyName("software");
        writer.WriteStartArray();
        foreach (var segment in software)
        {
            writer.WriteStartArray();
            writer.WriteNumberValue(value.SoftwareIndexes[segment.SoftwareId]);
            writer.WriteNumberValue(segment.Value);
            writer.WriteNumberValue(segment.SystemPercent);
            writer.WriteNumberValue(segment.ProcessCount);
            writer.WriteNumberValue(segment.BaseScore);
            WriteProcessRows(
                writer,
                segment.Processes,
                value.ProcessDetailSoftwareIds.Contains(segment.SoftwareId));
            if (segment.SharedValue is { } shared) writer.WriteNumberValue(shared);
            writer.WriteEndArray();
        }
        writer.WriteEndArray();
    }

    private static void WriteProcessRows(
        Utf8JsonWriter writer,
        IReadOnlyList<ResourceProcessSegment> processes,
        bool includeProcesses)
    {
        writer.WriteStartArray();
        if (includeProcesses)
        {
            foreach (var process in processes)
            {
                writer.WriteStartArray();
                writer.WriteNumberValue(process.ProcessId);
                writer.WriteStringValue(process.Name);
                WriteNullableString(writer, process.ExecutablePath);
                writer.WriteNumberValue(process.Value);
                writer.WriteNumberValue(process.SystemPercent);
                writer.WriteNumberValue(process.SoftwarePercent);
                WriteNullableString(writer, process.UserName);
                WriteNullableString(writer, process.Architecture);
                writer.WriteStringValue(process.AttributionKind);
                writer.WriteNumberValue(process.BaseScore);
                if (process.ProcessStartKey is > 0)
                {
                    writer.WriteStringValue(process.ProcessStartKey.Value.ToString(
                        CultureInfo.InvariantCulture));
                }
                else
                {
                    writer.WriteNullValue();
                }
                if (process.SharedValue is { } shared) writer.WriteNumberValue(shared);
                writer.WriteEndArray();
            }
        }
        writer.WriteEndArray();
    }

    private static void WriteNullableString(Utf8JsonWriter writer, string? value)
    {
        if (value is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteStringValue(value);
        }
    }

    private static void WriteNullableTimestamp(
        Utf8JsonWriter writer,
        string propertyName,
        DateTimeOffset? value)
    {
        if (value is null)
        {
            writer.WriteNull(propertyName);
        }
        else
        {
            writer.WriteString(propertyName, value.Value);
        }
    }

    private static void WriteNullableNumber(
        Utf8JsonWriter writer,
        string propertyName,
        double? value)
    {
        if (value is null)
        {
            writer.WriteNull(propertyName);
        }
        else
        {
            writer.WriteNumber(propertyName, value.Value);
        }
    }
}

internal sealed record ResourceMonitorWireSnapshot(
    int Version,
    DateTimeOffset? CapturedAt,
    ResourceBreakdownWireSnapshot Breakdown,
    ResourceTableWireSnapshot Table)
{
    internal const int CurrentVersion = 9;
}
