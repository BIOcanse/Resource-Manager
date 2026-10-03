namespace ResourceManager.NativeUi.Overlay;

internal sealed record OverlaySnapshot(int Version, IReadOnlyList<OverlayTarget> Targets);

internal sealed record OverlayTarget(
    string SoftwareId, string DisplayName, int ProcessId, ulong ProcessStartKey,
    OverlaySettings Settings, IReadOnlyList<OverlayMetric> Metrics);

internal sealed record OverlaySettings(
    string SoftwareId, bool Enabled, string Mode, IReadOnlyList<string> Metrics,
    string SizeMode, int FontSizePx, double RegionWidthRatio,
    double RegionHeightRatio, string Anchor, int MarginPx);

internal sealed record OverlayMetric(
    string MetricId, double? Value, string Unit, string State, string? Reason,
    IReadOnlyDictionary<string, string> Labels);

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
