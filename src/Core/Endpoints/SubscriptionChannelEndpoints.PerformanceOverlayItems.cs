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

        public PerformanceOverlayItemValue Value(double? value, string? actualSource, string? unit,
            DateTimeOffset? observedAt, string? reason)
            => new(Item.Kind, Item.Id, unit ?? Entry?.Unit, Item.Source, actualSource, IntervalMs, IntervalOrigin,
                Item.SyncPrecision, value, value is null ? "unavailable" : "current", value is null ? reason : null, observedAt);
    }

    /// <summary>Parses and validates the items query. No items query means no items (the single-interval
    /// request is unchanged). A malformed or invalid list rejects the subscription with the reason code.</summary>
    internal static IReadOnlyList<PerformanceOverlayItemPlan> ParsePerformanceOverlayItems(
        ParsedFrontendSubscription subscription, IServiceProvider services, TimeSpan interval)
    {
        if (!subscription.Query.TryGetValue("items", out var values)) return [];
        if (values.Count != 1 || string.IsNullOrWhiteSpace(values[0]))
            throw new ArgumentException("targeted-recording-item-invalid-kind");
        IReadOnlyList<TargetedRecordingItem> items;
        try
        {
            items = TargetedRecordingItemReader.ParseItems(values[0]!);
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
        if (TargetedRecordingItemReader.ValidateList(items, catalog, TargetedRecordingItemReader.MaximumOverlayItems) is { } reason)
            throw new ArgumentException(reason);
        var defaultMs = (int)Math.Round(interval.TotalMilliseconds);
        return items.Select(item =>
        {
            var entry = TargetedRecordingItemCatalogRules.Find(catalog, item.Kind, item.Id);
            var (intervalMs, origin) = TargetedRecordingItemReader.Interval(item, entry, defaultMs);
            return new PerformanceOverlayItemPlan(item, entry, intervalMs, origin);
        }).ToArray();
    }

    /// <summary>Acquires or releases each item's holder by whether the target runs, reads the items that are
    /// due, and returns the current values (items not due echo their last read). due is true when any item
    /// was read on this tick.</summary>
    internal static IReadOnlyList<PerformanceOverlayItemValue> UpdatePerformanceOverlayItems(
        IReadOnlyList<PerformanceOverlayItemPlan> plans, string subscriptionId, string softwareId,
        IReadOnlyList<SchedulingProcessFact> running, IMetricSnapshotObservationSource metrics,
        IResourceBreakdownObservationSource breakdown, IFrameTimingObservationSource frames, TimeSpan frameWindow,
        DateTimeOffset now, TimeSpan slack, out bool due)
    {
        due = false;
        var hasTargets = running.Count > 0;
        var softwareIds = new HashSet<string>([softwareId], StringComparer.OrdinalIgnoreCase);
        var targetKeys = running.Select(static process => (process.ProcessId, (long)process.ProcessStartKey)).ToHashSet();
        FrameTimingSnapshot? frameSnapshot = null;
        var frameRead = false;
        var values = new PerformanceOverlayItemValue[plans.Count];
        for (var index = 0; index < plans.Count; index++)
        {
            var plan = plans[index];
            var item = plan.Item;
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
                    TargetedRecordingItemReader.SoftwareRequest(item), period),
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
                        TargetedRecordingGapReasons.ValueUnavailable);
                    break;
                }
                case TargetedRecordingItemKinds.Software:
                {
                    var reading = TargetedRecordingItemReader.ReadSoftware(
                        breakdown.ReadLatest(TargetedRecordingItemReader.SoftwareRequest(item)), item, softwareIds,
                        (processId, startKey) => targetKeys.Contains((processId, startKey)));
                    plan.Last = plan.Value(reading.Value, reading.ActualSource, reading.Unit, reading.ObservedAt,
                        TargetedRecordingGapReasons.ValueUnavailable);
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
                    plan.Last = stream is null
                        ? plan.Value(null, null, null, null, TargetedRecordingGapReasons.ValueUnavailable)
                        : plan.Value(stream.Statistics.DurationMs / stream.Statistics.IntervalCount, stream.Source.ToString(),
                            TargetedRecordingItemCatalogRules.FramesUnit, frameSnapshot!.ObservedAt, null);
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
