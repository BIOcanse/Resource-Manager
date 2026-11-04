namespace ResourceManager.App.Domain.Operations;

public sealed record HostManagerOperationProgress(
    ulong Sequence,
    double? Percent,
    ulong? BytesDone,
    ulong? BytesTotal,
    ulong? SpeedBytesPerSecond,
    HostManagerOperationStage? Stage);

[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<HostManagerOperationStage>))]
public enum HostManagerOperationStage : byte
{
    Download = 1
}
