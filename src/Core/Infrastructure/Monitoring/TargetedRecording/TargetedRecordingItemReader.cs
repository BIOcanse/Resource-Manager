using System.Globalization;
using System.Text.Json;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Domain.Settings;

namespace ResourceManager.App.Infrastructure.Monitoring.TargetedRecording;

/// <summary>One read of one recording item. Value null means the publication carries no value, and Reason
/// then says why (the source's own code when it has one, else a TargetedRecordingReadReasons code). ObservedAt
/// is the source's observation time of the value. Unchanged (overlay reads only) means the metric publication is
/// the one read last time.</summary>
public readonly record struct TargetedRecordingItemReading(double? Value, string? ActualSource, string? Unit,
    DateTimeOffset? ObservedAt, bool Unchanged = false, string? Reason = null)
{
    /// <summary>The GPU totals' per-GPU sources that carried a share of the target in this publication
    /// (resource-breakdown:gpu.N.usage); a recording lists them in actualSources.</summary>
    public IReadOnlyList<string> ContributingSources { get; init; } = [];

    public static TargetedRecordingItemReading Missing(string reason) => new(null, null, null, null, Reason: reason);
}

/// <summary>How a recording item is subscribed and read. Targeted recordings and the performance overlay
/// both use it, so one item means the same value in both.</summary>
public static class TargetedRecordingItemReader
{
    public static int MaximumOverlayItems => ResourceManager.App.Application.Settings.AppSettingsDefaults.PerformanceOverlay.MaximumItems;

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

    /// <summary>Overlay read of the current publication. On a value it reports Unchanged when the observation
    /// is the one read last time; without a value it reads the publication's dataset observation for the source's
    /// failure code and returns the concrete reason.</summary>
    public static TargetedRecordingItemReading ReadMetric(IMetricSnapshotObservationSource metrics,
        TargetedRecordingItem item, DateTimeOffset? lastObservedAt)
    {
        var address = MetricAddress(item);
        var readings = metrics.ReadItems([address]);
        if (!readings.TryGetValue(address, out var reading)
            || reading.Status != SamplingObservationStatus.Current || reading.NumericValue is null)
        {
            // Only the no-value path reads the publication projection, for the source's failure code.
            var publication = metrics.ReadLatest(MetricRequest(item));
            return publication is null
                ? TargetedRecordingItemReading.Missing(reading is null ? TargetedRecordingReadReasons.MetricWarming
                    : MetricReason(reading.Status, null))
                : ReadMetric(publication, address);
        }
        if (reading.ObservedAt is { } observedAt && lastObservedAt == observedAt)
            return new(null, null, null, observedAt, Unchanged: true);
        return new(reading.NumericValue, SourceName(reading, address), reading.Unit, reading.ObservedAt);
    }

    /// <summary>The item's value in one delivered metric publication, timestamped at the dataset's observation
    /// time (the publication time when the dataset carries none). Without a value: the dataset's failure code,
    /// else metric-unknown (no field binding), metric-warming, metric-unsupported or metric-unavailable by the
    /// dataset status, else value-unavailable.</summary>
    public static TargetedRecordingItemReading ReadMetric(HardwareMetricSnapshot publication, string address)
    {
        var binding = publication.FieldBindings.GetValueOrDefault(address);
        if (binding is null)
            foreach (var candidate in publication.FieldBindings.Values)
                if (candidate.RequestAliases.Contains(address, StringComparer.OrdinalIgnoreCase)) { binding = candidate; break; }
        var key = publication.Items.ContainsKey(address) || binding is null ? address : binding.RequestAddress;
        var value = publication.Items.GetValueOrDefault(key);
        if (binding is null && value is null) return TargetedRecordingItemReading.Missing(TargetedRecordingReadReasons.MetricUnknown);
        var observation = publication.Datasets.GetValueOrDefault(address) ?? publication.Datasets.GetValueOrDefault(key);
        var status = observation?.Status ?? value?.Status ?? SamplingObservationStatus.Warming;
        if (observation?.FailureCode is { Length: > 0 } failure) return TargetedRecordingItemReading.Missing(failure);
        if (status != SamplingObservationStatus.Current) return TargetedRecordingItemReading.Missing(MetricReason(status, null));
        if (value?.NumericValue is not { } number) return TargetedRecordingItemReading.Missing(TargetedRecordingGapReasons.ValueUnavailable);
        var observedAt = observation is { ObservedAtUtcTicks: > 0 } ? new DateTimeOffset(observation.ObservedAtUtcTicks, TimeSpan.Zero)
            : value.ObservedAt ?? publication.CapturedAt;
        var named = value with
        {
            FieldName = value.FieldName ?? binding?.Field.Name,
            DeviceIdentity = value.DeviceIdentity ?? binding?.DeviceIdentity,
            SourceId = value.SourceId ?? binding?.SourceId
        };
        return new(number, SourceName(named, address), value.Unit is { Length: > 0 } unit ? unit : binding?.Field.Unit, observedAt);
    }

    private static string SourceName(MetricReadingValue reading, string address)
        => reading.FieldName is { Length: > 0 } field && reading.DeviceIdentity is { Length: > 0 } device
            ? $"{field}@{device}" : reading.SourceId ?? address;

    private static string MetricReason(SamplingObservationStatus status, string? failureCode)
        => failureCode is { Length: > 0 } ? failureCode
            : status switch
            {
                SamplingObservationStatus.Warming or SamplingObservationStatus.NotRequested => TargetedRecordingReadReasons.MetricWarming,
                SamplingObservationStatus.Unsupported => TargetedRecordingReadReasons.MetricUnsupported,
                _ => TargetedRecordingReadReasons.MetricUnavailable
            };

    /// <summary>The target software's value of a software item in one breakdown publication: a breakdown bar
    /// summed over the matched segments, or a process field summed over the target's processes, timestamped at
    /// the publication. Without a value, the concrete reason (TargetedRecordingTargets.Value).</summary>
    public static TargetedRecordingItemReading ReadSoftware(ResourceBreakdownSnapshot? snapshot,
        TargetedRecordingItem item, IReadOnlySet<string> softwareIds, Func<int, long, bool> isTargetProcess)
    {
        var usageTotal = string.Equals(item.Id, TargetedRecordingItemCatalogRules.GpuUsageTotalId, StringComparison.OrdinalIgnoreCase);
        if (usageTotal || string.Equals(item.Id, TargetedRecordingItemCatalogRules.GpuVramTotalId, StringComparison.OrdinalIgnoreCase))
        {
            if (TargetedRecordingTargets.NotReadyReason(snapshot) is { } notReady) return TargetedRecordingItemReading.Missing(notReady);
            // The request holds exactly the per-GPU bars this total sums; every contributing bar is a source.
            var suffix = usageTotal ? ".usage" : ".vram";
            double? gpuTotal = null;
            string? totalUnit = null;
            string? firstReason = null;
            var sources = new List<string>();
            foreach (var bar in snapshot!.Bars)
            {
                if (TargetedRecordingTargets.GpuIndex(bar.MetricId, suffix) is null) continue;
                if (TargetedRecordingTargets.Value(snapshot, bar.MetricId, softwareIds, out var unit, out var reason) is not { } part)
                {
                    firstReason ??= reason;
                    continue;
                }
                gpuTotal = (gpuTotal ?? 0) + part;
                totalUnit ??= unit;
                if (part > 0) sources.Add($"{TargetedRecordingItemCatalogRules.ResourceBreakdownSource}:{bar.MetricId}");
            }
            if (gpuTotal is null)
                return TargetedRecordingItemReading.Missing(firstReason ?? TargetedRecordingReadReasons.BreakdownBarMissing);
            return new(usageTotal ? Math.Min(100, gpuTotal.Value) : gpuTotal, TargetedRecordingItemCatalogRules.ResourceBreakdownSource,
                totalUnit, snapshot.CapturedAt) { ContributingSources = sources };
        }
        if (ResourceBreakdownMetricIds.IsSupported(item.Id))
        {
            var value = TargetedRecordingTargets.Value(snapshot, item.Id, softwareIds, out var unit, out var reason);
            return value is null ? TargetedRecordingItemReading.Missing(reason!)
                : new(value, TargetedRecordingItemCatalogRules.ResourceBreakdownSource, unit, snapshot!.CapturedAt);
        }
        if (TargetedRecordingTargets.NotReadyReason(snapshot) is { } fieldsNotReady)
            return TargetedRecordingItemReading.Missing(fieldsNotReady);
        if (snapshot!.ProcessFields is not { } fields)
            return TargetedRecordingItemReading.Missing(TargetedRecordingReadReasons.ProcessMetricUnavailable);
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
        if (total is null) return TargetedRecordingItemReading.Missing(TargetedRecordingReadReasons.ProcessMetricUnavailable);
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
        var candidate = new TargetedRecordingProfile("request", "request", TargetedRecordingProfileDefaults.MinimumIntervalMs, items);
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
