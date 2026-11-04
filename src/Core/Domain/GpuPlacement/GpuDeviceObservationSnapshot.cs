namespace ResourceManager.App.Domain.GpuPlacement;

public enum GpuDeviceAdapterIdentityKind : uint
{
    NoDevice = 0,
    Adapter = 1,
    Unavailable = 2,
    MultipleAdapters = 3
}

public sealed record GpuDeviceObservation(
    ulong ReturnedDeviceCount,
    ulong AdapterLuid,
    GpuDeviceAdapterIdentityKind Identity);

public sealed record GpuDeviceObservationSnapshot(
    GpuDeviceObservation D3D11,
    GpuDeviceObservation D3D12,
    GpuDeviceObservation Vulkan,
    GpuDeviceObservation D3D9,
    GpuDeviceObservation OpenGL);

public sealed record GpuDeviceObservationReadResult(
    GpuDeviceObservationSnapshot? Snapshot,
    string Status,
    int? Win32Error = null)
{
    public bool Success => Snapshot is not null;
}
