using System.Text.Json;
using ResourceManager.ReadWrite.Windows.Frames;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Application.Overlay;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Infrastructure.Monitoring.TargetedRecording;

namespace ResourceManager.App.Endpoints;

/// <summary>Overlay items use the targeted recording item contract: {kind, id, source, intervalMs,
/// syncPrecision}, validated against the same catalog, each item a named holder at its own interval.
/// Precision is stored and echoed only.</summary>
public static partial class ResourceManagerEndpointRouteBuilderExtensions
{
    internal const string PerformanceOverlayTargetNotRunning = "target-not-running";
    // The same unavailable reasons the id/field overlay reports (PerformanceOverlayProjection).
    internal const string PerformanceOverlayFrameEventsLost = "frame-events-lost";
    internal const string PerformanceOverlayNoFrameData = "no-frame-data";
    internal const string PerformanceOverlayProcessCpuUnavailable = "process-cpu-unavailable";
    internal const string PerformanceOverlayProcessMetricUnavailable = "process-metric-unavailable";
    internal const string PerformanceOverlayMetricUnavailable = "metric-unavailable";
    internal const string PerformanceOverlayUnknownMetric = "unknown-metric";

    internal sealed class PerformanceOverlayItemPlan(TargetedRecordingItem item, TargetedRecordingCatalogItem? entry,
        int intervalMs, string intervalOrigin)
    {
        public TargetedRecordingItem Item { get; } = item;
        public TargetedRecordingCatalogItem? Entry { get; } = entry;
        public int IntervalMs { get; } = intervalMs;
        public string IntervalOrigin { get; } = intervalOrigin;
        public IDisposable? Lease { get; set; }
        public DateTimeOffset NextDue { get; set; } = DateTimeOffset.MinValue;
        public DateTimeOffset? LastObservedAt { get; set; }
        public PerformanceOverlayItemValue? Last { get; set; }
        /// <summary>The item is no longer in the catalog (its device left); it answers unknown-metric and holds nothing.</summary>
        public bool Unknown { get; init; }
        public ResourceBreakdownSampleRequest? BreakdownRequest { get; init; }

        public PerformanceOverlayItemValue Value(double? value, string? actualSource, string? unit,
            DateTimeOffset? observedAt, string? reason)
            => new(Item.Kind, Item.Id, unit ?? Entry?.Unit, Item.Source, actualSource, IntervalMs, IntervalOrigin,
                Item.SyncPrecision, value, value is null ? "unavailable" : "current", value is null ? reason : null, observedAt);
    }

    /// <summary>Parses and validates the items. No items means no items (the single-interval request is
    /// unchanged). A malformed list (shape, interval, duplicates, count) rejects the subscription with the reason
    /// code; an item the catalog no longer has stays in the list and answers unknown-metric.</summary>
    private static IReadOnlyList<PerformanceOverlayItemPlan> ParsePerformanceOverlayItems(
        ParsedFrontendSubscription subscription, IServiceProvider services, TimeSpan interval)
    {
        // Items come from the body's items array or from the items= query value, never both.
        var inQuery = subscription.Query.TryGetValue("items", out var values);
        var inBody = subscription.Items.ValueKind != JsonValueKind.Undefined;
        if (!inQuery && !inBody) return [];
        if (inQuery && inBody || inQuery && (values.Count != 1 || string.IsNullOrWhiteSpace(values[0])))
            throw new ArgumentException("targeted-recording-item-invalid-kind");
        IReadOnlyList<TargetedRecordingItem> items;
        try
        {
            items = inBody ? TargetedRecordingItemReader.ParseItems(subscription.Items)
                : TargetedRecordingItemReader.ParseItems(values[0]!);
        }
        catch (FormatException error)
        {
            throw new ArgumentException(error.Message);
        }
        catch (JsonException)
        {
            throw new ArgumentException("targeted-recording-item-invalid-kind");
        }
        var catalog = services.GetRequiredService<ITargetedRecordingItemCatalog>().ReadCurrent();
        if (TargetedRecordingItemReader.ValidateShape(items, TargetedRecordingItemReader.MaximumOverlayItems) is { } reason)
            throw new ArgumentException(reason);
        var defaultMs = (int)Math.Round(interval.TotalMilliseconds);
        return items.Select(item =>
        {
            var unknown = TargetedRecordingItemCatalogRules.Validate(item, catalog, TargetedRecordingItemUses.Overlay) is not null;
            var entry = unknown ? null : TargetedRecordingItemCatalogRules.Find(catalog, item.Kind, item.Id);
            var (intervalMs, origin) = TargetedRecordingItemReader.Interval(item, entry, defaultMs);
            return new PerformanceOverlayItemPlan(item, entry, intervalMs, origin)
            {
                Unknown = unknown,
                BreakdownRequest = item.Kind == TargetedRecordingItemKinds.Software && !unknown
                    ? TargetedRecordingItemReader.SoftwareRequest(item, catalog) : null
            };
        }).ToArray();
    }

    /// <summary>Acquires or releases each item's holder by whether the target runs, reads the items that are
    /// due, and returns the current values (items not due echo their last read). due is true when any item
    /// was read on this tick.</summary>
    internal static IReadOnlyList<PerformanceOverlayItemValue> UpdatePerformanceOverlayItems(
        IReadOnlyList<PerformanceOverlayItemPlan> plans, string subscriptionId, IReadOnlyList<string> targetSoftwareIds,
        IReadOnlyList<SchedulingProcessFact> running, IMetricSnapshotObservationSource metrics,
        IResourceBreakdownObservationSource breakdown, IFrameTimingObservationSource frames, TimeSpan frameWindow,
        DateTimeOffset now, TimeSpan slack, out bool due)
    {
        due = false;
        var hasTargets = running.Count > 0;
        var softwareIds = new HashSet<string>(targetSoftwareIds, StringComparer.OrdinalIgnoreCase);
        var targetKeys = running.Select(static process => (process.ProcessId, (long)process.ProcessStartKey)).ToHashSet();
        FrameTimingSnapshot? frameSnapshot = null;
        var frameRead = false;
        var values = new PerformanceOverlayItemValue[plans.Count];
        for (var index = 0; index < plans.Count; index++)
        {
            var plan = plans[index];
            var item = plan.Item;
            if (plan.Unknown)
            {
                if (plan.Last is null) due = true;
                values[index] = plan.Last ??= plan.Value(null, null, null, null, PerformanceOverlayUnknownMetric);
                continue;
            }
            if (!hasTargets)
            {
                if (plan.Lease is not null || plan.Last?.Reason != PerformanceOverlayTargetNotRunning) due = true;
                plan.Lease?.Dispose();
                plan.Lease = null;
                plan.LastObservedAt = null;
                plan.NextDue = DateTimeOffset.MinValue;
                plan.Last = plan.Value(null, null, null, null, PerformanceOverlayTargetNotRunning);
                values[index] = plan.Last;
                continue;
            }
            var holder = $"performance-overlay:{subscriptionId}:item:{item.Kind}:{item.Id}";
            var period = TimeSpan.FromMilliseconds(plan.IntervalMs);
            plan.Lease ??= item.Kind switch
            {
                TargetedRecordingItemKinds.Metric => (IDisposable?)metrics.AcquireSubscription(holder,
                    TargetedRecordingItemReader.MetricRequest(item), period),
                TargetedRecordingItemKinds.Software => (IDisposable?)breakdown.AcquireSubscription(holder,
                    plan.BreakdownRequest!, period),
                _ => (IDisposable?)frames.AcquireSubscription()
            };
            if (plan.Last is not null && now + slack < plan.NextDue)
            {
                values[index] = plan.Last;
                continue;
            }
            plan.NextDue = now + period;
            due = true;
            switch (item.Kind)
            {
                case TargetedRecordingItemKinds.Metric:
                {
                    var reading = TargetedRecordingItemReader.ReadMetric(metrics, item, plan.LastObservedAt);
                    if (reading.Unchanged && plan.Last is { Value: not null } previous) { values[index] = previous; continue; }
                    plan.LastObservedAt = reading.ObservedAt;
                    plan.Last = plan.Value(reading.Value, reading.ActualSource, reading.Unit, reading.ObservedAt,
                        PerformanceOverlayMetricUnavailable);
                    break;
                }
                case TargetedRecordingItemKinds.Software:
                {
                    var reading = TargetedRecordingItemReader.ReadSoftware(
                        breakdown.ReadLatest(plan.BreakdownRequest!), item, softwareIds,
                        (processId, startKey) => targetKeys.Contains((processId, startKey)));
                    plan.Last = plan.Value(reading.Value, reading.ActualSource, reading.Unit, reading.ObservedAt,
                        string.Equals(item.Id, ResourceBreakdownMetricIds.CpuUsage, StringComparison.OrdinalIgnoreCase)
                            ? PerformanceOverlayProcessCpuUnavailable : PerformanceOverlayProcessMetricUnavailable);
                    break;
                }
                default:
                {
                    if (!frameRead) { frameSnapshot = frames.Read(frameWindow); frameRead = true; }
                    var stream = frameSnapshot?.Processes
                        .Where(process => process.ProcessStartKey > 0 && targetKeys.Contains((process.ProcessId, process.ProcessStartKey)))
                        .SelectMany(static process => process.Streams)
                        .Where(static candidate => candidate.Statistics.IntervalCount > 0)
                        .OrderByDescending(static candidate => candidate.PresentCount)
                        .ThenBy(static candidate => candidate.Source)
                        .ThenBy(static candidate => candidate.SwapChain)
                        .FirstOrDefault();
                    var unit = item.Id == TargetedRecordingProfileDefaults.FramesItemId
                        ? TargetedRecordingItemCatalogRules.FramesUnit : TargetedRecordingItemCatalogRules.FramesRateUnit;
                    if (frameSnapshot is { Complete: false })
                    {
                        plan.Last = plan.Value(null, null, unit, null, PerformanceOverlayFrameEventsLost);
                        break;
                    }
                    if (stream is null)
                    {
                        plan.Last = plan.Value(null, null, unit, null, PerformanceOverlayNoFrameData);
                        break;
                    }
                    var statistics = stream.Statistics;
                    var value = item.Id switch
                    {
                        TargetedRecordingProfileDefaults.FramesFpsItemId => statistics.AverageFps,
                        TargetedRecordingProfileDefaults.FramesOnePercentLowItemId => statistics.OnePercentLowFps,
                        TargetedRecordingProfileDefaults.FramesPointOnePercentLowItemId => statistics.PointOnePercentLowFps,
                        _ => statistics.DurationMs / statistics.IntervalCount
                    };
                    plan.Last = double.IsFinite(value)
                        ? plan.Value(value, stream.Source.ToString(), unit, frameSnapshot!.ObservedAt, null)
                        : plan.Value(null, null, unit, null, PerformanceOverlayMetricUnavailable);
                    break;
                }
            }
            values[index] = plan.Last;
        }
        return values;
    }

    internal static void ReleasePerformanceOverlayItems(IReadOnlyList<PerformanceOverlayItemPlan> plans)
    {
        foreach (var plan in plans)
        {
            plan.Lease?.Dispose();
            plan.Lease = null;
        }
    }
}
