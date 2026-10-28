using System.Text.Json.Serialization;
using ResourceManager.App.Domain.Messages;

namespace ResourceManager.App.Domain.KernelLeak;

// Published resource kinds contain only resources the observer can actually report.
[JsonConverter(typeof(JsonStringEnumConverter<KernelLeakResourceKind>))]
public enum KernelLeakResourceKind { ProcessHandles }
[JsonConverter(typeof(JsonStringEnumConverter<KernelLeakUnit>))]
public enum KernelLeakUnit { [JsonStringEnumMemberName("count")] Count }
[JsonConverter(typeof(JsonStringEnumConverter<KernelLeakReportStatus>))]
public enum KernelLeakReportStatus { NotObserved, Observing, Empty, Suspected, Unavailable }
[JsonConverter(typeof(JsonStringEnumConverter<KernelLeakItemState>))]
public enum KernelLeakItemState
{
    [JsonStringEnumMemberName("active")] Active,
    [JsonStringEnumMemberName("history")] History
}
[JsonConverter(typeof(JsonStringEnumConverter<KernelLeakFailure>))]
public enum KernelLeakFailure { None, SourceUnavailable, CapacityExceeded, InvalidObservation }
[JsonConverter(typeof(JsonStringEnumConverter<KernelLeakMetadataStatus>))]
public enum KernelLeakMetadataStatus { Current, Unavailable, ProcessEnded }
[JsonConverter(typeof(JsonStringEnumConverter<KernelLeakRestartPolicy>))]
public enum KernelLeakRestartPolicy { NextInterval }
[JsonConverter(typeof(JsonStringEnumConverter<KernelLeakActionKind>))]
public enum KernelLeakActionKind { RestartHolder, ForceReleaseHandles }
[JsonConverter(typeof(JsonStringEnumConverter<KernelLeakRiskLevel>))]
public enum KernelLeakRiskLevel { Warning, High }

// Factories keep unavailable/ended metadata free of contradictory values.
public sealed record KernelLeakImage
{
    public KernelLeakMetadataStatus Status { get; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Name { get; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Path { get; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? ProductName { get; }
    [JsonConstructor]
    internal KernelLeakImage(KernelLeakMetadataStatus status, string? name = null, string? path = null, string? productName = null)
        => (Status, Name, Path, ProductName) = (status, name, path, productName);
    public static KernelLeakImage Current(string name, string path, string? productName) => new(KernelLeakMetadataStatus.Current, name, path, productName);
    public static KernelLeakImage Unavailable { get; } = new(KernelLeakMetadataStatus.Unavailable);
    public static KernelLeakImage ProcessEnded { get; } = new(KernelLeakMetadataStatus.ProcessEnded);
}
public sealed record KernelLeakServices(KernelLeakMetadataStatus Status, IReadOnlyList<string> Names);
public sealed record KernelLeakHolder(int ProcessId, string StartKey, KernelLeakImage Image, KernelLeakServices Services)
{
    public static KernelLeakHolder Unknown(int processId, string startKey) => new(processId, startKey,
        KernelLeakImage.Unavailable, new(KernelLeakMetadataStatus.Unavailable, []));
}
public sealed record KernelLeakAction(KernelLeakActionKind Action, bool Enabled, BackendMessage? DisabledReason,
    BackendMessage Risk, KernelLeakRiskLevel RiskLevel);
public sealed record KernelLeakSuspect(KernelLeakResourceKind ResourceKind, ulong CurrentValue,
    double GrowthPerSecond, double ObservationSeconds, int SampleCount, ulong Growth,
    KernelLeakHolder Holder)
{
    [JsonRequired] public KernelLeakItemState State { get; init; }
    [JsonRequired] public DateTimeOffset FirstObservedAt { get; init; }
    [JsonRequired] public DateTimeOffset LastObservedAt { get; init; }
    public KernelLeakUnit Unit => KernelLeakUnit.Count;
    // Contract only: phase 1 provides no command for either action. Target identity is Holder's PID + start key.
    public IReadOnlyList<KernelLeakAction> Actions => ReservedActions;
    private static readonly IReadOnlyList<KernelLeakAction> ReservedActions = Array.AsReadOnly(new[]
    {
        new KernelLeakAction(KernelLeakActionKind.RestartHolder, false,
            BackendMessage.Create(BackendMessageDomains.KernelLeak, BackendMessageCodes.KernelLeak.ActionNotProvided),
            BackendMessage.Create(BackendMessageDomains.KernelLeak, BackendMessageCodes.KernelLeak.RestartHolderRisk), KernelLeakRiskLevel.Warning),
        new KernelLeakAction(KernelLeakActionKind.ForceReleaseHandles, false,
            BackendMessage.Create(BackendMessageDomains.KernelLeak, BackendMessageCodes.KernelLeak.ActionNotProvided),
            BackendMessage.Create(BackendMessageDomains.KernelLeak, BackendMessageCodes.KernelLeak.ForceReleaseHandlesRisk), KernelLeakRiskLevel.High)
    });
}
public sealed record KernelLeakRunFailure(DateTimeOffset StartedAt, DateTimeOffset FailedAt, KernelLeakFailure Failure);
// No start while disabled, awaiting the first frame, or unavailable before the first frame.
public sealed record KernelLeakObservation(DateTimeOffset? StartedAt, double ElapsedSeconds, int SampleCount,
    int RequiredSeconds, int RequiredSampleCount, KernelLeakRunFailure? PreviousRunFailure)
{
    public KernelLeakRestartPolicy RestartPolicy => KernelLeakRestartPolicy.NextInterval;
}
public sealed record KernelLeakReport(KernelLeakReportStatus Status, DateTimeOffset? CapturedAt,
    IReadOnlyList<KernelLeakSuspect> Items, KernelLeakFailure Failure, KernelLeakObservation Observation)
{
    public int UnavailableHolderCount { get; init; }
    public int UnrecordedItemCount { get; init; }
}

// Sampling input is separate from the HTTP report; cache-like inputs are testable exclusions.
public enum KernelLeakSampleKind { ProcessHandles, FileCachePoolBytes }
public sealed record KernelLeakSample(int ProcessId, string StartKey, KernelLeakSampleKind ResourceKind, ulong Value);
public sealed record KernelLeakSampleFrame(DateTimeOffset CapturedAt, bool Available, IReadOnlyList<KernelLeakSample> Samples)
{
    public int UnavailableHolderCount { get; init; }
    // A missing count is not an absent process. A missing start key cannot prove that PID's old instance ended.
    public IReadOnlyList<KernelLeakUnavailableHolder> UnavailableHolders { get; init; } = [];
}
public sealed record KernelLeakUnavailableHolder(int ProcessId, string? StartKey);
public sealed record KernelLeakDiagnostics(int HolderCount, long SeriesBytes, long PeakSeriesBytes,
    long ObservedFrames, double LastJudgementMilliseconds, double LastAttributionMilliseconds);
public sealed record KernelLeakDebugSnapshot(KernelLeakReport Report, KernelLeakDiagnostics Diagnostics);
