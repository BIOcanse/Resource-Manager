namespace ResourceManager.App.Domain.GpuPlacement;

public enum GpuRemoteCallKind
{
    LoadProvider,
    ConfigureProvider,
    ReadDevices,
    LoadObservationProvider,
    StartApiObservation,
    ReadApiObservation,
    StopApiObservation,
    ArmRecreation,
    FinishRecreation,
    CancelRecreation
}

public sealed record GpuRemoteCallRequest(
    Guid CallId, GpuPlacementProcessInstance Process, GpuRemoteCallKind Kind,
    ulong FunctionAddress, int ParameterByteLength, bool ReadResponse);

public sealed record GpuRemoteCallSnapshot(
    ulong ParameterAddress, uint? ThreadId, ulong? ThreadCreationFileTimeUtc,
    uint? ExitCode, byte[]? Response, bool ResourcesReleased, string Status, int? NativeError)
{
    public bool Completed => ResourcesReleased && Status == "completed" && ExitCode.HasValue;
}
