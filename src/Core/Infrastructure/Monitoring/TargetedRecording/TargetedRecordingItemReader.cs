using System.Globalization;
using System.Text.Json;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Domain.Settings;

namespace ResourceManager.App.Infrastructure.Monitoring.TargetedRecording;

/// <summary>One read of one recording item. Value null means the source published no current value;
/// Unchanged means the metric publication is the one read last time (not a new point, not a gap).</summary>
public readonly record struct TargetedRecordingItemReading(double? Value, string? ActualSource, string? Unit,
    DateTimeOffset? ObservedAt, bool Unchanged = false);

/// <summary>How a recording item is subscribed and read. Targeted recordings and the performance overlay
/// both use it, so one item means the same value in both.</summary>
public static class TargetedRecordingItemReader
{
    public const int MaximumOverlayItems = 64;

    /// <summary>A specific metric source is a catalog id fieldName@deviceIdentity; auto reads the metric id.</summary>
    public static string MetricAddress(TargetedRecordingItem item)
        => string.Equals(item.Source, TargetedRecordingProfileDefaults.AutoSource, StringComparison.Ordinal)
            ? item.Id : item.Source;

    public static MetricSampleRequest MetricRequest(TargetedRecordingItem item)
        => MetricSampleRequest.ForIds([MetricAddress(item)]);

    /// <summary>The software item's breakdown request: the breakdown metric, or the process field, with
    /// process detail so field values can be matched to the target's processes.</summary>
    public static ResourceBreakdownSampleRequest SoftwareRequest(TargetedRecordingItem item,
        IReadOnlyList<TargetedRecordingCatalogItem> catalog)
        => new(TargetedRecordingItemCatalogRules.TotalParts(item.Id, catalog) ?? [item.Id],
            new Dictionary<string, string>(), ProcessSampleDetailLevel.ResourceTableFull);

    /// <summary>The item interval: its own, else the source's natural interval, else the caller's default.</summary>
    public static (int IntervalMs, string Origin) Interval(TargetedRecordingItem item,
        TargetedRecordingCatalogItem? entry, int defaultIntervalMs)
        => item.IntervalMs is { } own ? (own, TargetedRecordingIntervalOrigins.Item)
            : entry?.NaturalIntervalMs is { } natural ? (natural, TargetedRecordingIntervalOrigins.Natural)
            : (defaultIntervalMs, TargetedRecordingIntervalOrigins.ProfileDefault);

    public static TargetedRecordingItemReading ReadMetric(IMetricSnapshotObservationSource metrics,
        TargetedRecordingItem item, DateTimeOffset? lastObservedAt)
    {
        var address = MetricAddress(item);
        var readings = metrics.ReadItems([address]);
        if (!readings.TryGetValue(address, out var reading)
            || reading.Status != SamplingObservationStatus.Current || reading.NumericValue is not { } value)
            return new(null, null, null, null);
        if (reading.ObservedAt is { } observedAt && lastObservedAt == observedAt)
            return new(null, null, null, observedAt, Unchanged: true);
        var source = reading.FieldName is { Length: > 0 } field && reading.DeviceIdentity is { Length: > 0 } device
            ? $"{field}@{device}" : reading.SourceId ?? address;
        return new(value, source, reading.Unit, reading.ObservedAt);
    }

    /// <summary>The target software's value of a software item: a breakdown bar summed over the matched
    /// segments, or a process field summed over the target's processes.</summary>
    public static TargetedRecordingItemReading ReadSoftware(ResourceBreakdownSnapshot? snapshot,
        TargetedRecordingItem item, IReadOnlySet<string> softwareIds, Func<int, long, bool> isTargetProcess)
    {
        var usageTotal = string.Equals(item.Id, TargetedRecordingItemCatalogRules.GpuUsageTotalId, StringComparison.OrdinalIgnoreCase);
        if (usageTotal || string.Equals(item.Id, TargetedRecordingItemCatalogRules.GpuVramTotalId, StringComparison.OrdinalIgnoreCase))
        {
            // The request holds exactly the per-GPU bars this total sums.
            var suffix = usageTotal ? ".usage" : ".vram";
            double? gpuTotal = null;
            string? totalUnit = null;
            foreach (var bar in snapshot?.Bars ?? [])
            {
                if (!bar.MetricId.StartsWith(ResourceBreakdownMetricIds.GpuPrefix, StringComparison.OrdinalIgnoreCase)
                    || !bar.MetricId.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
                if (TargetedRecordingTargets.Value(snapshot, bar.MetricId, softwareIds, out var unit) is not { } part) continue;
                gpuTotal = (gpuTotal ?? 0) + part;
                totalUnit ??= unit;
            }
            return gpuTotal is null ? new(null, null, null, null)
                : new(usageTotal ? Math.Min(100, gpuTotal.Value) : gpuTotal, TargetedRecordingItemCatalogRules.ResourceBreakdownSource,
                    totalUnit, snapshot!.CapturedAt);
        }
        if (ResourceBreakdownMetricIds.IsSupported(item.Id))
        {
            var value = TargetedRecordingTargets.Value(snapshot, item.Id, softwareIds, out var unit);
            return value is null ? new(null, null, null, null)
                : new(value, TargetedRecordingItemCatalogRules.ResourceBreakdownSource, unit, snapshot!.CapturedAt);
        }
        if (snapshot is null || snapshot.Sampling.Status != ResourceBreakdownSamplingStatuses.Ready
            || snapshot.ProcessFields is not { } fields)
            return new(null, null, null, null);
        double? total = null;
        foreach (var process in fields.Processes)
        {
            var startKey = long.TryParse(process.CreationWindowsFileTime100Ns, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
            if (!isTargetProcess(process.ProcessId, startKey)) continue;
            foreach (var field in process.Values)
            {
                if (string.Equals(field.Field, item.Id, StringComparison.OrdinalIgnoreCase) && field.NumericValue is { } number)
                    total = (total ?? 0) + number;
            }
        }
        if (total is null) return new(null, null, null, null);
        var definition = fields.Fields.FirstOrDefault(entry => string.Equals(entry.Name, item.Id, StringComparison.OrdinalIgnoreCase));
        return new(total, definition?.SourceId ?? TargetedRecordingItemCatalogRules.ResourceBreakdownSource,
            definition?.Unit, fields.CapturedAt ?? snapshot.CapturedAt);
    }

    /// <summary>Validates a request's own item list exactly like a saved profile's: shape, duplicates, count,
    /// then catalog membership and source ownership. Returns the reason code, or null.</summary>
    public static string? ValidateList(IReadOnlyList<TargetedRecordingItem> items,
        IReadOnlyList<TargetedRecordingCatalogItem> catalog, int maximumItems = TargetedRecordingProfileDefaults.MaximumItems,
        string use = TargetedRecordingItemUses.Recording)
    {
        if (ValidateShape(items, maximumItems) is { } shape) return shape;
        foreach (var item in items)
        {
            if (TargetedRecordingItemCatalogRules.Validate(item, catalog, use) is { } reason) return reason;
        }
        return null;
    }

    /// <summary>Count, shape and duplicates of a request's own item list, without the catalog.</summary>
    public static string? ValidateShape(IReadOnlyList<TargetedRecordingItem> items,
        int maximumItems = TargetedRecordingProfileDefaults.MaximumItems)
    {
        if (items.Count > maximumItems) return "targeted-recording-profile-items-too-many";
        var candidate = new TargetedRecordingProfile("request", "request", TargetedRecordingProfileDefaults.DefaultIntervalMs, items);
        return TargetedRecordingProfileShape.ValidateProfile(candidate);
    }

    /// <summary>Parses a JSON array of recording items ({kind, id, source?, intervalMs?, syncPrecision?}),
    /// the same shape a profile stores. Missing source is auto and missing precision is mid.</summary>
    public static IReadOnlyList<TargetedRecordingItem> ParseItems(string json)
    {
        using var document = JsonDocument.Parse(json);
        return ParseItems(document.RootElement);
    }

    /// <summary>The same array already parsed, as a request body carries it.</summary>
    public static IReadOnlyList<TargetedRecordingItem> ParseItems(JsonElement array)
    {
        if (array.ValueKind != JsonValueKind.Array)
            throw new FormatException("targeted-recording-item-invalid-kind");
        var items = new List<TargetedRecordingItem>();
        foreach (var element in array.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object) throw new FormatException("targeted-recording-item-invalid-kind");
            items.Add(new TargetedRecordingItem(
                String(element, "kind") ?? string.Empty,
                String(element, "id") ?? string.Empty,
                String(element, "source") ?? TargetedRecordingProfileDefaults.AutoSource,
                IntervalMs(element),
                String(element, "syncPrecision") ?? TargetedRecordingSyncPrecisions.Mid));
        }
        return items;
    }

    /// <summary>Absent or null is no interval; a non-integer is out of range (-1) so validation rejects it.</summary>
    private static int? IntervalMs(JsonElement element)
    {
        if (!element.TryGetProperty("intervalMs", out var value) || value.ValueKind == JsonValueKind.Null) return null;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : -1;
    }

    private static string? String(JsonElement element, string name)
        => element.TryGetProperty(name, out var value)
            ? value.ValueKind == JsonValueKind.String ? value.GetString()
                : value.ValueKind == JsonValueKind.Null ? null : throw new FormatException("targeted-recording-item-invalid-kind")
            : null;
}
