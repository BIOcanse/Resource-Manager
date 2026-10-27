using System.Collections.Frozen;
using ResourceManager.ReadWrite.Information;

namespace ResourceManager.ReadWrite.Windows.Frames;

/// <summary>The exact frame field names from required-scope matrix rows 164 and 165.</summary>
public static class FrameFieldRegistry
{
    public const string DisplayIntervalMs = "frame_all_display_interval_ms";
    public const string DisplayedFps = "frame_all_displayed_fps";
    public const string DroppedCount = "frame_all_dropped_count";
    public const string FpsDerived = "frame_all_fps_derived";
    public const string PresentIntervalMs = "frame_all_present_interval_ms";
    public const string PresentTimeActual = "frame_all_present_time_actual";
    public const string PresentedFps = "frame_all_presented_fps";
    public const string DisplayIntervalP95Ms = "frame_all_display_interval_p95_ms";
    public const string DisplayIntervalP99Ms = "frame_all_display_interval_p99_ms";
    public const string PresentIntervalP95Ms = "frame_all_present_interval_p95_ms";
    public const string PresentIntervalP99Ms = "frame_all_present_interval_p99_ms";
    public const string TimePercentileDerived = "frame_all_time_percentile_derived";
    public const string WindowDurationConfigured = "frame_all_window_duration_configured";

    private const string PresentSource = "windows.present-etw";
    public static readonly DeviceInformationDefinition FrameAllDisplayIntervalMs =
        new(DisplayIntervalMs, "frame", "all", "number", "ms", "frame.display.interval", PresentSource, ReadVolatility.Sampled);
    public static readonly DeviceInformationDefinition FrameAllDisplayedFps =
        new(DisplayedFps, "frame", "all", "number", "fps", "frame.display.average_fps", PresentSource, ReadVolatility.Sampled);
    public static readonly DeviceInformationDefinition FrameAllDroppedCount =
        new(DroppedCount, "frame", "all", "unsigned-integer", "frames", "frame.display.dropped_count", PresentSource, ReadVolatility.Sampled);
    public static readonly DeviceInformationDefinition FrameAllFpsDerived =
        new(FpsDerived, "frame", "all", "number", "fps", "frame.present.average_fps", PresentSource, ReadVolatility.Sampled);
    public static readonly DeviceInformationDefinition FrameAllPresentIntervalMs =
        new(PresentIntervalMs, "frame", "all", "number", "ms", "frame.present.mean_interval", PresentSource, ReadVolatility.Sampled);
    public static readonly DeviceInformationDefinition FrameAllPresentTimeActual =
        new(PresentTimeActual, "frame", "all", "number", "ms", "frame.present.unix_utc_milliseconds", PresentSource, ReadVolatility.Sampled);
    public static readonly DeviceInformationDefinition FrameAllPresentedFps =
        new(PresentedFps, "frame", "all", "number", "fps", "frame.present.average_fps", PresentSource, ReadVolatility.Sampled);
    public static readonly DeviceInformationDefinition FrameAllDisplayIntervalP95Ms =
        new(DisplayIntervalP95Ms, "frame", "all", "number", "ms", "frame.display.nearest_rank.p95", PresentSource, ReadVolatility.Sampled);
    public static readonly DeviceInformationDefinition FrameAllDisplayIntervalP99Ms =
        new(DisplayIntervalP99Ms, "frame", "all", "number", "ms", "frame.display.nearest_rank.p99", PresentSource, ReadVolatility.Sampled);
    public static readonly DeviceInformationDefinition FrameAllPresentIntervalP95Ms =
        new(PresentIntervalP95Ms, "frame", "all", "number", "ms", "frame.present.nearest_rank.p95", PresentSource, ReadVolatility.Sampled);
    public static readonly DeviceInformationDefinition FrameAllPresentIntervalP99Ms =
        new(PresentIntervalP99Ms, "frame", "all", "number", "ms", "frame.present.nearest_rank.p99", PresentSource, ReadVolatility.Sampled);
    public static readonly DeviceInformationDefinition FrameAllTimePercentileDerived =
        new(TimePercentileDerived, "frame", "all", "number", "ms", "frame.present.nearest_rank.query_percentile", PresentSource, ReadVolatility.Sampled);
    public static readonly DeviceInformationDefinition FrameAllWindowDurationConfigured =
        new(WindowDurationConfigured, "frame", "all", "number", "s", "frame.window.configured_duration", PresentSource, ReadVolatility.EventDriven("Frame window duration configuration changed"));

    public static IReadOnlyList<DeviceInformationDefinition> Definitions { get; } = Array.AsReadOnly(new[]
    {
        FrameAllDisplayIntervalMs, FrameAllDisplayedFps, FrameAllDroppedCount,
        FrameAllFpsDerived, FrameAllPresentIntervalMs, FrameAllPresentTimeActual,
        FrameAllPresentedFps, FrameAllDisplayIntervalP95Ms, FrameAllDisplayIntervalP99Ms,
        FrameAllPresentIntervalP95Ms, FrameAllPresentIntervalP99Ms,
        FrameAllTimePercentileDerived, FrameAllWindowDurationConfigured
    });

    private static readonly FrozenDictionary<string, DeviceInformationDefinition> ByName =
        Definitions.ToFrozenDictionary(static item => item.Name, StringComparer.Ordinal);

    public static DeviceInformationDefinition? Find(string name)
        => ByName.GetValueOrDefault(name);

    public static bool IsSupported(DeviceInformationDefinition definition)
        => ByName.TryGetValue(definition.Name, out var registered) && registered == definition;

    public static string ObjectType(DeviceInformationDefinition definition)
        => definition.Name switch
        {
            DisplayIntervalMs or DisplayedFps or DroppedCount or PresentIntervalMs or PresentedFps => "present_stream",
            FpsDerived or PresentTimeActual => "frame_stream",
            DisplayIntervalP95Ms or DisplayIntervalP99Ms or PresentIntervalP95Ms or PresentIntervalP99Ms => "present_stream_window",
            TimePercentileDerived or WindowDurationConfigured => "frame_window",
            _ => throw new ArgumentException("Unknown frame field definition.", nameof(definition))
        };
}
