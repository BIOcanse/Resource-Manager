namespace ResourceManager.NativeUi.Overlay;

internal sealed record OverlaySnapshot(int Version, IReadOnlyList<OverlayTarget> Targets)
{
    // Raw field values requested with fields= (the backend's raw-field value contract).
    public IReadOnlyList<OverlayDeviceFields> Fields { get; init; } = [];
}

internal sealed record OverlayTarget(
    string SoftwareId, int ProcessId, ulong ProcessStartKey,
    OverlaySettings Settings, IReadOnlyList<OverlayMetric> Metrics)
{
    // The raw fields of the subscription this target arrived on; items with a chosen source read them.
    public IReadOnlyList<OverlayDeviceFields> Fields { get; init; } = [];
}

internal sealed record OverlayDeviceFields(string DeviceIdentity, IReadOnlyList<OverlayFieldValue> Fields);

// Status is the backend SamplingObservationStatus number; only Current (1) carries a value.
internal sealed record OverlayFieldValue(string Name, string Unit, double? NumericValue, string? IntegerValue, int Status)
{
    internal const int CurrentStatus = 1;
}

internal sealed record OverlaySettings(string SoftwareId, bool Enabled, string Mode);

internal sealed record OverlayMetric(
    string MetricId, double? Value, string Unit, string State, string? Reason);

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
