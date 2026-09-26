namespace ResourceManager.App.Application.ResourceBreakdown;

public interface IPhysicalDiskIoAttributionReader
{
    PhysicalDiskIoAttributionSnapshot Read();
}

public sealed record PhysicalDiskIoAttributionSnapshot(
    DateTimeOffset CapturedAt,
    IReadOnlyDictionary<int, PhysicalDiskProcessAttribution> Processes,
    PhysicalDiskIoProviderState ProviderState)
{
    public static PhysicalDiskIoAttributionSnapshot NotRequested { get; } = new(
        DateTimeOffset.UtcNow,
        new Dictionary<int, PhysicalDiskProcessAttribution>(),
        new PhysicalDiskIoProviderState(
            "etw-fileio-disk",
            "NotRequested",
            "当前快照未请求 ETW 真实磁盘归因。"));
}

public sealed record PhysicalDiskProcessAttribution(
    int ProcessId,
    double ReadBytesPerSecond,
    double WriteBytesPerSecond,
    int ReadEventCount,
    int WriteEventCount)
{
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
