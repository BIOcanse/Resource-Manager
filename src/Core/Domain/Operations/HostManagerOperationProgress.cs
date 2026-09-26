namespace ResourceManager.App.Domain.Operations;

public sealed record HostManagerOperationProgress(
    ulong Sequence,
    double? Percent,
    ulong? BytesDone,
    ulong? BytesTotal,
    ulong? SpeedBytesPerSecond,
    string? Stage,
    string? Message);
