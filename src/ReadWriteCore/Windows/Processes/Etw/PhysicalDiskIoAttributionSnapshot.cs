using ResourceManager.ReadWrite.Windows.Processes;

namespace ResourceManager.App.Application.ResourceBreakdown;

public static class PhysicalDiskIoProviderStates
{
    public const string NotRequested = "not_requested";
    public const string Frozen = "frozen";
    public const string Idle = "idle";
    public const string Starting = "starting";
    public const string Running = "running";
    public const string Warming = "warming";
    public const string Available = "available";
    public const string Stopped = "stopped";
    public const string StopFailed = "stop_failed";
    public const string Unavailable = "unavailable";

    public static string FromKernelEtwState(string state) => state switch
    {
        "Running" => Running,
        "Idle" => Idle,
        "Starting" => Starting,
        "Stopped" => Stopped,
        "StopFailed" => StopFailed,
        "Available" => Available,
        "Unavailable" => Unavailable,
        _ => throw new InvalidDataException($"Unknown kernel ETW state '{state}'.")
    };
}

public interface IPhysicalDiskIoAttributionReader
{
    PhysicalDiskIoAttributionSnapshot Read();
    PhysicalDiskIoAttributionSnapshot Read(bool includeObservedTraffic) => Read();
}

public sealed record PhysicalDiskIoAttributionSnapshot(
    DateTimeOffset CapturedAt,
    IReadOnlyDictionary<int, PhysicalDiskProcessAttribution> Processes,
    PhysicalDiskIoProviderState ProviderState)
{
    public PhysicalDiskProcessIoWindow? ObservedTraffic { get; init; }
    public static PhysicalDiskIoAttributionSnapshot NotRequested { get; } = new(
        DateTimeOffset.UtcNow,
        new Dictionary<int, PhysicalDiskProcessAttribution>(),
        new PhysicalDiskIoProviderState(
            "etw-fileio-disk",
            PhysicalDiskIoProviderStates.NotRequested,
            "当前快照未请求 ETW 真实磁盘归因。"));
}

public sealed record PhysicalDiskProcessAttribution(
    int ProcessId,
    double ReadBytesPerSecond,
    double WriteBytesPerSecond,
    int ReadEventCount,
    int WriteEventCount)
{
    public ulong? ProcessStartKey { get; init; }
    public double TotalBytesPerSecond => ReadBytesPerSecond + WriteBytesPerSecond;
}

public sealed record PhysicalDiskIoProviderState(
    string Id,
    string State,
    string Message)
{
    public Domain.Metrics.SamplingObservationStatus ObservationStatus =>
        AttributionProviderObservationStatus.Resolve(State);
}


