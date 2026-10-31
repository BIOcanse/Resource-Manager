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
    EtwSessionBuffers PresentSessionBuffers);

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

/// <summary>Targeted recording: the duration used when a request names none, the largest accepted duration, and the sample interval.</summary>
public sealed record TargetedRecordingOptions(
    [property: JsonPropertyName("default_maximum_duration_seconds")] int DefaultMaximumDurationSeconds,
    [property: JsonPropertyName("maximum_duration_limit_seconds")] int MaximumDurationLimitSeconds,
    [property: JsonPropertyName("sample_interval_ms")] int SampleIntervalMilliseconds,
    [property: JsonPropertyName("maximum_report_kib")] int MaximumReportKib,
    [property: JsonPropertyName("exit_confirmation_ticks")] int ExitConfirmationTicks,
    [property: JsonPropertyName("no_process_ticks")] int NoProcessTicks)
{
    public TargetedRecordingOptions Validate()
    {
        if (MaximumDurationLimitSeconds < 1 || DefaultMaximumDurationSeconds < 1
            || DefaultMaximumDurationSeconds > MaximumDurationLimitSeconds || SampleIntervalMilliseconds < 1
            || MaximumReportKib < 1 || ExitConfirmationTicks < 1 || NoProcessTicks < 1)
            throw new InvalidDataException("Invalid targeted recording options.");
        return this;
    }
}
