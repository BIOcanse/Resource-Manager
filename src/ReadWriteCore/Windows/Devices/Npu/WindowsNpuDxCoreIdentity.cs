using ResourceManager.App.Domain.Metrics;
using ResourceManager.ReadWrite.Windows.Gpu;

namespace ResourceManager.ReadWrite.Windows.Devices.Npu;

// One exact KMT/CM evidence contract shared by the operation-local memory reader
// and the engine reader's explicit live directory. It owns no native resources.
internal static class WindowsNpuDxCoreIdentity
{
    private const WindowsGpuIdentityEvidence RequiredEvidence = WindowsGpuIdentityEvidence.KmtLuidOpen
        | WindowsGpuIdentityEvidence.KmtHardwarePnpKey | WindowsGpuIdentityEvidence.KmtDeviceIds
        | WindowsGpuIdentityEvidence.CmExactDevNode | WindowsGpuIdentityEvidence.CmCanonicalInstanceRoundTrip;

    internal static bool ValidBridge(WindowsGpuIdentityRead read)
    {
        if (read.Status != SamplingObservationStatus.Current || read.SkippedCount != 0
            || read.Identities.Count == 0 || read.Identities.Count > WindowsNpuInformationReader.MaximumPhysicalAdapterCount) return false;
        var indices = new HashSet<uint>();
        return read.Identities.All(identity => identity.Status is WindowsGpuPhysicalIdentityStatus.Bound or WindowsGpuPhysicalIdentityStatus.NonPci
            && identity.PhysicalAdapterIndex < read.Identities.Count && indices.Add(identity.PhysicalAdapterIndex)
            && !string.IsNullOrWhiteSpace(identity.PnpInstanceId) && (identity.Evidence & RequiredEvidence) == RequiredEvidence);
    }

    [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.Fixed)]
    internal static bool TryAdapter(IWindowsNpuDxCoreApi api, nint adapter, out WindowsNpuDxCoreAdapter value)
    {
        value = default;
        if (!TryProperty<AdapterLuid>(api, adapter, WindowsNpuDxCoreProperty.InstanceLuid, out var luid)
            || (luid.LowPart == 0 && luid.HighPart == 0)
            || !TryProperty<WindowsNpuDxCoreHardwareId>(api, adapter, WindowsNpuDxCoreProperty.HardwareId, out var id)) return false;
        value = new(luid, id.VendorId, id.DeviceId);
        return true;
    }

    [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.Fixed)]
    internal static bool TryPhysicalCount(IWindowsNpuDxCoreMemoryApi api, nint adapter, out uint count)
    {
        count = 0;
        return api.IsPropertySupported(adapter, WindowsNpuDxCoreProperty.PhysicalAdapterCount)
            && api.GetPropertySize(adapter, WindowsNpuDxCoreProperty.PhysicalAdapterCount, out var size) >= 0
            && size == sizeof(uint) && TryProperty(api, adapter, WindowsNpuDxCoreProperty.PhysicalAdapterCount, out count);
    }

    [ResourceManager.ReadWrite.RuntimeReadTransport]
    internal static unsafe bool TryProperty<T>(IWindowsNpuDxCoreApi api, nint adapter,
        WindowsNpuDxCoreProperty property, out T result) where T : unmanaged
    {
        T value = default;
        var success = api.IsPropertySupported(adapter, property)
            && api.GetProperty(adapter, property, checked((nuint)sizeof(T)), (nint)(&value)) >= 0;
        result = value;
        return success;
    }
}
