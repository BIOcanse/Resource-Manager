namespace ResourceManager.NativeUi.Overlay;

// One pushed value of /api/performance-overlay/subscribe (snapshot version 3). Items are the values of the
// subscription's items in request order; every target of one subscription is the same software.
internal sealed record OverlaySnapshot(int Version, IReadOnlyList<OverlayTarget> Targets)
{
    public IReadOnlyList<OverlayItemValue> Items { get; init; } = [];
}

internal sealed record OverlayTarget(
    string SoftwareId, int ProcessId, ulong ProcessStartKey, OverlaySettings Settings)
{
    // The item values of the subscription this target arrived on.
    public IReadOnlyList<OverlayItemValue> Items { get; init; } = [];
}

// The current value of one requested item (backend PerformanceOverlayItemValue). State is current or
// unavailable; Reason says why a value is unavailable (target-not-running, frame-events-lost, no-frame-data,
// metric-unavailable, process-cpu-unavailable, process-metric-unavailable).
internal sealed record OverlayItemValue(
    string Kind, string Id, string? Unit, string SelectedSource, string? ActualSource,
    int IntervalMs, string IntervalOrigin, string SyncPrecision,
    double? Value, string State, string? Reason, DateTimeOffset? ObservedAt);

internal sealed record OverlaySettings(string SoftwareId, bool Enabled, string Mode);

internal readonly record struct OverlayTargetId(int ProcessId, ulong ProcessStartKey);

internal static class OverlayTargetSelection
{
    public static IReadOnlyDictionary<OverlayTargetId, OverlayTarget> Current(OverlaySnapshot snapshot) =>
        snapshot.Targets
            .Where(static target => target.ProcessId > 0 && target.ProcessStartKey > 0
                && target.Settings.Enabled)
            .GroupBy(static target => new OverlayTargetId(target.ProcessId, target.ProcessStartKey))
            .ToDictionary(static group => group.Key, static group => group.First());
}
