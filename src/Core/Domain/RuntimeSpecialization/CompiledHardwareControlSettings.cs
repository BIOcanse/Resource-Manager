using System.Text.Json.Serialization;
using ResourceManager.App.Domain.RuntimeSpecialization.FreedomPoints;

namespace ResourceManager.App.Domain.RuntimeSpecialization;

/// <summary>Build-time hardware helper and control-writer choices from the hardware_bridge freedom-point table.</summary>
public sealed record CompiledHardwareControlSettings(
    [property: global::FreedomPoint.FreedomPointBinding(BackendFreedomPointPositions.HelperRequestTimeoutMs)]
    TimeSpan HelperRequestTimeout,
    [property: global::FreedomPoint.FreedomPointBinding(BackendFreedomPointPositions.HelperProcessTiming)]
    HardwareHelperProcessTiming HelperProcessTiming,
    [property: global::FreedomPoint.FreedomPointBinding(BackendFreedomPointPositions.FanCurveFeed)]
    FanCurveFeedTiming FanCurveFeed,
    [property: global::FreedomPoint.FreedomPointBinding(BackendFreedomPointPositions.UniwillEcChannel)]
    UniwillEcChannelOptions UniwillEcChannel);

/// <summary>Hardware helper process timing: diagnostic drain on exit, each stop wait, and the readings section interval.</summary>
public sealed record HardwareHelperProcessTiming(
    [property: JsonPropertyName("diagnostic_drain_timeout_ms")] int DiagnosticDrainTimeoutMilliseconds,
    [property: JsonPropertyName("stop_timeout_ms")] int StopTimeoutMilliseconds,
    [property: JsonPropertyName("reading_interval_ms")] int ReadingIntervalMilliseconds)
{
    public HardwareHelperProcessTiming Validate()
    {
        if (DiagnosticDrainTimeoutMilliseconds < 0 || StopTimeoutMilliseconds < 1 || ReadingIntervalMilliseconds < 1)
            throw new InvalidDataException("Invalid hardware helper process timing.");
        return this;
    }
}

/// <summary>Fan curve temperature feed: push and subscription interval, and the oldest sample it still forwards.</summary>
public sealed record FanCurveFeedTiming(
    [property: JsonPropertyName("interval_ms")] int IntervalMilliseconds,
    [property: JsonPropertyName("maximum_sample_age_ms")] int MaximumSampleAgeMilliseconds)
{
    public FanCurveFeedTiming Validate()
    {
        if (IntervalMilliseconds < 1 || MaximumSampleAgeMilliseconds < 1)
            throw new InvalidDataException("Invalid fan curve feed timing.");
        return this;
    }
}

/// <summary>Uniwill EC channel: lock wait and how many agreeing reads out of how many attempts count as one read.</summary>
public sealed record UniwillEcChannelOptions(
    [property: JsonPropertyName("lock_wait_ms")] int LockWaitMilliseconds,
    [property: JsonPropertyName("read_attempts")] int ReadAttempts,
    [property: JsonPropertyName("agreeing_reads")] int AgreeingReads)
{
    public UniwillEcChannelOptions Validate()
    {
        if (LockWaitMilliseconds < 1 || AgreeingReads < 1 || ReadAttempts < AgreeingReads)
            throw new InvalidDataException("Invalid Uniwill EC channel options.");
        return this;
    }
}
