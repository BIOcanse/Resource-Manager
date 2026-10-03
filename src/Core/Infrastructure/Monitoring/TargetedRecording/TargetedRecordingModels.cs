using ResourceManager.App.Domain.FrameTiming;

namespace ResourceManager.App.Infrastructure.Monitoring.TargetedRecording;

public sealed record TargetedRecordingHeader(
    string Id,
    string SoftwareId,
    string SoftwareName,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    int MaximumDurationSeconds,
    string Status,
    string? StopReason,
    bool Incomplete);

public sealed record TargetedProcessIdentity(int ProcessId, long ProcessStartKey);

public sealed record TargetedResourceSample(
    DateTimeOffset CapturedAt,
    IReadOnlyDictionary<string, double?> Software,
    IReadOnlyDictionary<string, double?> System,
    IReadOnlyList<TargetedProcessIdentity> Processes);

public sealed record FpsDistributionBucket(int FromFps, int ToFps, int Count);

public sealed record TargetedRecordingReport(
    TargetedRecordingHeader Recording,
    FrameIntervalStatistics? Summary,
    IReadOnlyList<FrameIntervalSample> FrameIntervals,
    IReadOnlyList<FpsDistributionBucket> FpsDistribution,
    IReadOnlyList<TargetedResourceSample> ResourceSamples);
