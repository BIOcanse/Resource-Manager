using System.Text.Json.Serialization;

namespace ResourceManager.App.Domain.KernelLeak;

[JsonConverter(typeof(JsonStringEnumConverter<KernelLeakResourceKind>))]
public enum KernelLeakResourceKind { ProcessHandles, FileCachePoolBytes }
[JsonConverter(typeof(JsonStringEnumConverter<KernelLeakReportStatus>))]
public enum KernelLeakReportStatus { Empty, Suspected, Unavailable }
[JsonConverter(typeof(JsonStringEnumConverter<KernelLeakFailure>))]
public enum KernelLeakFailure { None, NotObserved, SourceUnavailable, CapacityExceeded, InvalidObservation }
[JsonConverter(typeof(JsonStringEnumConverter<KernelLeakMetadataStatus>))]
public enum KernelLeakMetadataStatus { Current, Unavailable, ProcessEnded }

public sealed record KernelLeakHolder(int ProcessId, string StartKey, string? Name,
    string? ImagePath, string? ProductName, IReadOnlyList<string> ServiceNames,
    KernelLeakMetadataStatus ImageStatus, KernelLeakMetadataStatus ServiceStatus);
public sealed record KernelLeakSuspect(KernelLeakResourceKind ResourceKind, ulong CurrentValue,
    double GrowthPerSecond, double ObservationSeconds, int SampleCount, ulong Growth,
    KernelLeakHolder Holder);
public sealed record KernelLeakReport(KernelLeakReportStatus Status, DateTimeOffset? CapturedAt,
    IReadOnlyList<KernelLeakSuspect> Items, KernelLeakFailure Failure)
{ public int UnavailableHolderCount { get; init; } }
public sealed record KernelLeakSample(int ProcessId, string StartKey, KernelLeakResourceKind ResourceKind, ulong Value);
public sealed record KernelLeakSampleFrame(DateTimeOffset CapturedAt, bool Available, IReadOnlyList<KernelLeakSample> Samples)
{ public int UnavailableHolderCount { get; init; } }
public sealed record KernelLeakDiagnostics(int HolderCount, long SeriesBytes, long PeakSeriesBytes,
    long ObservedFrames, double LastJudgementMilliseconds, double LastAttributionMilliseconds);public sealed record KernelLeakDebugSnapshot(KernelLeakReport Report, KernelLeakDiagnostics Diagnostics);
