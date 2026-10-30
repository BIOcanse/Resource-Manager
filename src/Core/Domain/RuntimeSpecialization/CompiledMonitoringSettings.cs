using System.Text.Json.Serialization;
using ResourceManager.App.Domain.RuntimeSpecialization.FreedomPoints;

namespace ResourceManager.App.Domain.RuntimeSpecialization;

/// <summary>Build-time monitoring choices from the monitoring freedom-point table.</summary>
public sealed record CompiledMonitoringSettings(
    [property: global::FreedomPoint.FreedomPointBinding(BackendFreedomPointPositions.OverlayFrameWindowMs)]
    TimeSpan OverlayFrameWindow,
    [property: global::FreedomPoint.FreedomPointBinding(BackendFreedomPointPositions.TargetedRecording)]
    TargetedRecordingOptions TargetedRecording);

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
