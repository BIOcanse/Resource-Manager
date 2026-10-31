using System.Text.Json.Serialization;
using ResourceManager.App.Domain.RuntimeSpecialization.FreedomPoints;
using ResourceManager.App.Infrastructure.Telemetry.Etw;

namespace ResourceManager.App.Domain.RuntimeSpecialization;

/// <summary>Build-time monitoring choices from the monitoring freedom-point table.</summary>
public sealed record CompiledMonitoringSettings(
    [property: global::FreedomPoint.FreedomPointBinding(BackendFreedomPointPositions.OverlayFrameWindowMs)]
    TimeSpan OverlayFrameWindow,
    [property: global::FreedomPoint.FreedomPointBinding(BackendFreedomPointPositions.TargetedRecording)]
    TargetedRecordingOptions TargetedRecording,
    [property: global::FreedomPoint.FreedomPointBinding(BackendFreedomPointPositions.VidmmSessionBuffers)]
    EtwSessionBuffers VidMmSessionBuffers,
    [property: global::FreedomPoint.FreedomPointBinding(BackendFreedomPointPositions.GpuAllocationSessionBuffers)]
    EtwSessionBuffers GpuAllocationSessionBuffers,
    [property: global::FreedomPoint.FreedomPointBinding(BackendFreedomPointPositions.PresentSessionBuffers)]
    EtwSessionBuffers PresentSessionBuffers,
    [property: global::FreedomPoint.FreedomPointBinding(BackendFreedomPointPositions.PerformanceOverlay)]
    ResourceManager.App.Domain.Settings.PerformanceOverlayDefaults PerformanceOverlay,
    [property: global::FreedomPoint.FreedomPointBinding(BackendFreedomPointPositions.FramePipeline)]
    FramePipelineOptions FramePipeline);

/// <summary>
/// Every behaviour value of the Present frame pipeline (frame_pipeline): ledger retention, the present
/// rate one stream keeps for a whole retention (sizes the per-stream Present cap), stream cap, event flush
/// interval, the targeted recording's frame read window and lag, the PID filter cap,
/// and the GPU frame tier's tracking caps.
/// </summary>
public sealed record FramePipelineOptions(
    [property: JsonPropertyName("retention_ms")] int RetentionMilliseconds,
    [property: JsonPropertyName("present_rate_capacity_hz")] int PresentRateCapacityHz,
    [property: JsonPropertyName("stream_capacity")] int StreamCapacity,
    [property: JsonPropertyName("flush_interval_ms")] int FlushIntervalMilliseconds,
    [property: JsonPropertyName("recording_frame_window_ms")] int RecordingFrameWindowMilliseconds,
    [property: JsonPropertyName("recording_frame_read_lag_ms")] int RecordingFrameReadLagMilliseconds,
    [property: JsonPropertyName("process_filter_capacity")] int ProcessFilterCapacity,
    [property: JsonPropertyName("gpu_object_capacity")] int GpuObjectCapacity,
    [property: JsonPropertyName("gpu_pending_packet_capacity")] int GpuPendingPacketCapacity,
    [property: JsonPropertyName("gpu_pending_frame_capacity")] int GpuPendingFrameCapacity)
{
    [JsonPropertyName("diagnostic_window_ms")]
    public int DiagnosticWindowMilliseconds { get; init; }
    [JsonPropertyName("provider_enable_timeout_ms")]
    public int ProviderEnableTimeoutMilliseconds { get; init; }
    [JsonPropertyName("session_release_timeout_ms")]
    public int SessionReleaseTimeoutMilliseconds { get; init; }
    public TimeSpan DiagnosticWindow => TimeSpan.FromMilliseconds(DiagnosticWindowMilliseconds);
    // ETW accepts at most this many PIDs in one provider's PID filter (MAX_EVENT_FILTER_PID_COUNT); an OS limit, not a choice.
    public const int EtwProcessFilterLimit = 8;

    public TimeSpan Retention => TimeSpan.FromMilliseconds(RetentionMilliseconds);
    public TimeSpan FlushInterval => TimeSpan.FromMilliseconds(FlushIntervalMilliseconds);
    public TimeSpan RecordingFrameWindow => TimeSpan.FromMilliseconds(RecordingFrameWindowMilliseconds);
    public TimeSpan RecordingFrameReadLag => TimeSpan.FromMilliseconds(RecordingFrameReadLagMilliseconds);

    /// <summary>Per-stream Present cap = ceil(rate x retention) + one predecessor endpoint.</summary>
    public ResourceManager.ReadWrite.Windows.Frames.PresentFrameLedgerLimits LedgerLimits => new(
        Retention,
        checked((int)Math.Ceiling((double)PresentRateCapacityHz * RetentionMilliseconds / 1000d) + 1),
        StreamCapacity);

    public FramePipelineOptions Validate()
    {
        if (RetentionMilliseconds < 1 || PresentRateCapacityHz < 1 || StreamCapacity < 1 || FlushIntervalMilliseconds < 1
            || RecordingFrameWindowMilliseconds < 1 || RecordingFrameReadLagMilliseconds < 0
            || (long)RecordingFrameWindowMilliseconds + RecordingFrameReadLagMilliseconds > RetentionMilliseconds
            || ProcessFilterCapacity is < 0 or > EtwProcessFilterLimit
            || GpuObjectCapacity < 1 || GpuPendingPacketCapacity < 1 || GpuPendingFrameCapacity < 1
            || DiagnosticWindowMilliseconds < 1 || DiagnosticWindowMilliseconds > RetentionMilliseconds
            || ProviderEnableTimeoutMilliseconds < 1 || SessionReleaseTimeoutMilliseconds < 1
            || (double)PresentRateCapacityHz * RetentionMilliseconds / 1000d > int.MaxValue - 1)
            throw new InvalidDataException("Invalid frame_pipeline options: every value must be positive, the recording window plus "
                + "its read lag must fit in the retention, and the PID filter cap must be 0-" + EtwProcessFilterLimit + ".");
        _ = LedgerLimits.Validate();
        return this;
    }
}

/// <summary>One user-mode real-time ETW session's buffer pool as declared: buffer size in KiB and buffers at start, together whole MiB.</summary>
public sealed record EtwSessionBufferPoolOptions(
    [property: JsonPropertyName("buffer_kib")] int BufferKib,
    [property: JsonPropertyName("minimum_buffers")] int MinimumBuffers)
{
    public EtwSessionBuffers ToSessionBuffers()
    {
        // TraceEvent takes the pool as whole MiB (BufferSizeMB) and divides it by the buffer size.
        if (BufferKib <= 0 || MinimumBuffers <= 0 || (long)BufferKib * MinimumBuffers % 1024 != 0)
            throw new InvalidDataException($"Invalid ETW session buffer pool: {MinimumBuffers} buffers of {BufferKib} KiB is not a positive whole MiB.");
        return new(BufferKib, MinimumBuffers);
    }
}

/// <summary>The performance overlay point as declared, including every default item's source and precision.</summary>
public sealed record PerformanceOverlayOptions(
    [property: JsonPropertyName("default_enabled")] bool DefaultEnabled,
    [property: JsonPropertyName("default_mode")] string DefaultMode,
    [property: JsonPropertyName("default_items")] IReadOnlyList<PerformanceOverlayItemOptions> DefaultItems,
    [property: JsonPropertyName("default_size_mode")] string DefaultSizeMode,
    [property: JsonPropertyName("default_font_size_px")] int DefaultFontSizePx,
    [property: JsonPropertyName("default_region_width_ratio")] double DefaultRegionWidthRatio,
    [property: JsonPropertyName("default_region_height_ratio")] double DefaultRegionHeightRatio,
    [property: JsonPropertyName("default_anchor")] string DefaultAnchor,
    [property: JsonPropertyName("default_margin_px")] int DefaultMarginPx,
    [property: JsonPropertyName("font_size_px_minimum")] int FontSizePxMinimum,
    [property: JsonPropertyName("font_size_px_maximum")] int FontSizePxMaximum,
    [property: JsonPropertyName("margin_px_minimum")] int MarginPxMinimum,
    [property: JsonPropertyName("margin_px_maximum")] int MarginPxMaximum,
    [property: JsonPropertyName("region_ratio_minimum")] double RegionRatioMinimum,
    [property: JsonPropertyName("region_ratio_maximum")] double RegionRatioMaximum,
    [property: JsonPropertyName("region_ratio_step")] double RegionRatioStep,
    [property: JsonPropertyName("maximum_software")] int MaximumSoftware,
    [property: JsonPropertyName("maximum_items")] int MaximumItems,
    [property: JsonPropertyName("toggle_hotkey_enabled")] bool ToggleHotkeyEnabled,
    [property: JsonPropertyName("toggle_hotkey_encoding")] IReadOnlyList<int> ToggleHotkeyEncoding)
{
    public ResourceManager.App.Domain.Settings.PerformanceOverlayDefaults ToDefaults()
    {
        var items = (DefaultItems ?? throw new InvalidDataException("performance_overlay.default_items is missing."))
            .Select(item => new ResourceManager.App.Domain.Settings.TargetedRecordingItem(
                item.Kind, item.Id, item.Source, item.IntervalMs, item.SyncPrecision)).ToArray();
        var defaults = new ResourceManager.App.Domain.Settings.PerformanceOverlayDefaults(DefaultEnabled, DefaultMode, Array.AsReadOnly(items),
            DefaultSizeMode, DefaultFontSizePx, DefaultRegionWidthRatio, DefaultRegionHeightRatio, DefaultAnchor, DefaultMarginPx,
            FontSizePxMinimum, FontSizePxMaximum, MarginPxMinimum, MarginPxMaximum, RegionRatioMinimum, RegionRatioMaximum, RegionRatioStep,
            MaximumSoftware, MaximumItems, ToggleHotkeyEnabled,
            Array.AsReadOnly(ToggleHotkeyEncoding?.ToArray() ?? throw new InvalidDataException("performance_overlay.toggle_hotkey_encoding is missing.")));
        if (FontSizePxMinimum > FontSizePxMaximum || MarginPxMinimum > MarginPxMaximum || RegionRatioMinimum > RegionRatioMaximum
            || RegionRatioMinimum <= 0 || !double.IsFinite(RegionRatioStep) || RegionRatioStep <= 0
            || MaximumSoftware < 1 || MaximumItems < 1 || ToggleHotkeyEncoding.Count != 7
            || defaults.ValidateEntry(defaults.Entry()) is not null)
            throw new InvalidDataException("Invalid performance_overlay freedom point: its defaults must satisfy its own ranges.");
        return defaults;
    }
}

/// <summary>The declaration uses explicit wire names; it is independent of the settings serializer policy.</summary>
public sealed record PerformanceOverlayItemOptions(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("intervalMs")] int? IntervalMs,
    [property: JsonPropertyName("syncPrecision")] string SyncPrecision);

/// <summary>Targeted recording (freedom point backend/monitoring targeted_recording; its description names
/// every field).</summary>
public sealed record TargetedRecordingOptions(
    [property: JsonPropertyName("default_maximum_duration_seconds")] int DefaultMaximumDurationSeconds,
    [property: JsonPropertyName("maximum_duration_limit_seconds")] int MaximumDurationLimitSeconds,
    [property: JsonPropertyName("sample_interval_ms")] int SampleIntervalMilliseconds,
    [property: JsonPropertyName("maximum_report_kib")] int MaximumReportKib,
    [property: JsonPropertyName("exit_confirmation_ticks")] int ExitConfirmationTicks,
    [property: JsonPropertyName("no_process_ticks")] int NoProcessTicks,
    [property: JsonPropertyName("not_refreshed_interval_percent")] int NotRefreshedIntervalPercent,
    [property: JsonPropertyName("frame_tail_delay_ms")] int FrameTailDelayMilliseconds,
    [property: JsonPropertyName("actual_interval_samples")] int ActualIntervalSamples,
    [property: JsonPropertyName("default_profile")] TargetedRecordingDefaultProfileOptions? DefaultProfile)
{
    public TargetedRecordingOptions Validate()
    {
        if (MaximumDurationLimitSeconds < 1 || DefaultMaximumDurationSeconds < 1
            || DefaultMaximumDurationSeconds > MaximumDurationLimitSeconds || SampleIntervalMilliseconds < 1
            || MaximumReportKib < 1 || ExitConfirmationTicks < 1 || NoProcessTicks < 1
            || NotRefreshedIntervalPercent < 100
            || FrameTailDelayMilliseconds < 0 || ActualIntervalSamples < 1 || DefaultProfile is null)
            throw new InvalidDataException("Invalid targeted recording options.");
        if (Settings.TargetedRecordingProfileShape.ValidateProfile(DefaultProfile.ToProfile()) is { } reason)
            throw new InvalidDataException($"Invalid targeted recording default profile: {reason}.");
        return this;
    }
}

/// <summary>The content of the 默认 recording profile: what a settings file without a targeted recording section
/// reads as, and what a new profile copies when it names no items.</summary>
public sealed record TargetedRecordingDefaultProfileOptions(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("default_interval_ms")] int DefaultIntervalMs,
    [property: JsonPropertyName("items")] IReadOnlyList<TargetedRecordingDefaultProfileItem> Items)
{
    public Settings.TargetedRecordingProfile ToProfile() => new(Settings.TargetedRecordingProfileDefaults.DefaultProfileId,
        Name, DefaultIntervalMs, (Items ?? []).Select(static item => item.ToItem()).ToArray());
}

/// <summary>One default profile item; omitted source, interval and precision take the item model's own
/// defaults (auto, the profile interval, mid).</summary>
public sealed record TargetedRecordingDefaultProfileItem(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("source")] string? Source = null,
    [property: JsonPropertyName("interval_ms")] int? IntervalMs = null,
    [property: JsonPropertyName("sync_precision")] string? SyncPrecision = null)
{
    public Settings.TargetedRecordingItem ToItem() => new(Kind, Id,
        Source ?? Settings.TargetedRecordingProfileDefaults.AutoSource, IntervalMs,
        SyncPrecision ?? Settings.TargetedRecordingSyncPrecisions.Mid);
}
