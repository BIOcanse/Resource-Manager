using System.Runtime.InteropServices;
using ResourceManager.ReadWrite.Windows.Gpu;

namespace ResourceManager.ReadWrite.Windows.Devices.Npu;

internal readonly record struct WindowsNpuDxCoreAdapter(AdapterLuid Luid, uint VendorId, uint DeviceId);

internal static class WindowsNpuDxCoreEnumeration
{
    internal const uint MaximumAdapterCount = 256;
    internal static readonly Guid NpuAttribute = new("D46140C4-ADD7-451B-9E56-06FE8C3B58ED");

    internal static IReadOnlyList<WindowsNpuDxCoreAdapter>? Read(IWindowsNpuDxCoreApi api)
    {
        nint factory = 0, list = 0;
        try
        {
            if (api.CreateFactory(out factory) < 0 || factory == 0
                || api.CreateAdapterList(factory, NpuAttribute, out list) < 0 || list == 0) return null;
            if (api.IsStale(list)) return null;
            var count = api.GetAdapterCount(list);
            if (count > MaximumAdapterCount) return null;
            var result = new List<WindowsNpuDxCoreAdapter>(checked((int)count));
            var luids = new HashSet<ulong>();
            for (uint index = 0; index < count; index++)
            {
                nint adapter = 0;
                try
                {
                    if (api.GetAdapter(list, index, out adapter) < 0 || adapter == 0 || !api.IsValid(adapter)) return null;
                    if (!api.IsAttributeSupported(adapter, NpuAttribute)) continue;
                    if (!TryReadProperty<byte>(api, adapter, WindowsNpuDxCoreProperty.IsHardware, out var hardware)) return null;
                    if (hardware == 0) continue;
                    if (!TryReadProperty<AdapterLuid>(api, adapter, WindowsNpuDxCoreProperty.InstanceLuid, out var luid)
                        || (luid.LowPart == 0 && luid.HighPart == 0)
                        || !luids.Add(WindowsGpuAdapterIdentity.Pack(luid))
                        || !TryReadProperty<WindowsNpuDxCoreHardwareId>(api, adapter, WindowsNpuDxCoreProperty.HardwareId, out var ids)) return null;
                    if (!api.IsValid(adapter)) return null;
                    result.Add(new WindowsNpuDxCoreAdapter(luid, ids.VendorId, ids.DeviceId));
                }
                finally
                {
                    if (adapter != 0) api.Release(adapter);
                }
            }
            return api.IsStale(list) ? null : result;
        }
        finally
        {
            if (list != 0) api.Release(list);
            if (factory != 0) api.Release(factory);
        }
    }

    private static unsafe bool TryReadProperty<T>(IWindowsNpuDxCoreApi api, nint adapter,
        WindowsNpuDxCoreProperty property, out T result) where T : unmanaged
    {
        result = default;
        if (!api.IsPropertySupported(adapter, property)) return false;
        T value = default;
        if (api.GetProperty(adapter, property, checked((nuint)sizeof(T)), (nint)(&value)) < 0) return false;
        result = value;
        return true;
    }
}

// DirectX-Headers dxcore_interface.h: DriverDescription is 2; HardwareID is 3.
internal enum WindowsNpuDxCoreProperty : uint { InstanceLuid = 0, DriverVersion = 1, HardwareId = 3, IsHardware = 11, PhysicalAdapterCount = 15, AdapterEngineCount = 16, AdapterEngineName = 17 }

[StructLayout(LayoutKind.Sequential)]
internal struct WindowsNpuDxCoreHardwareId
{
    public uint VendorId;
    public uint DeviceId;
    public uint SubSystemId;
    public uint Revision;
}

internal interface IWindowsNpuDxCoreApi
{
    int CreateFactory(out nint factory);
    int CreateAdapterList(nint factory, Guid attribute, out nint list);
    uint GetAdapterCount(nint list);
    bool IsStale(nint list);
    int GetAdapter(nint list, uint index, out nint adapter);
    bool IsValid(nint adapter);
    bool IsAttributeSupported(nint adapter, Guid attribute);
    bool IsPropertySupported(nint adapter, WindowsNpuDxCoreProperty property);
    int GetProperty(nint adapter, WindowsNpuDxCoreProperty property, nuint size, nint data);
    void Release(nint instance);
}

// Kept separate so the existing identity-only recorded API need not implement dynamic queries.
internal interface IWindowsNpuDxCoreMemoryApi : IWindowsNpuDxCoreApi
{
    int GetPropertySize(nint adapter, WindowsNpuDxCoreProperty property, out nuint size);
    bool IsQueryStateSupported(nint adapter, WindowsNpuDxCoreState state);
    int QueryState(nint adapter, WindowsNpuDxCoreState state, nuint inputSize, nint input,
        nuint outputSize, nint output);
}

// Kept separate from the memory recording seam; Adapter1 is optional at runtime.
internal interface IWindowsNpuDxCoreEngineApi : IWindowsNpuDxCoreMemoryApi
{
    int QueryAdapter1(nint adapter, out nint adapter1);
    int GetPropertyWithInput(nint adapter1, WindowsNpuDxCoreProperty property,
        nuint inputSize, nint input, nuint outputSize, nint output);
}

internal enum WindowsNpuDxCoreState : uint { AdapterMemoryUsageBytes = 2, AdapterEngineRunningTimeMicroseconds = 4 }

[StructLayout(LayoutKind.Sequential)]
internal struct WindowsNpuDxCoreEngineIndex
{
    public uint PhysicalAdapterIndex;
    public uint EngineIndex;
}

[StructLayout(LayoutKind.Sequential)]
internal struct WindowsNpuDxCoreEngineQueryInput
{
    public WindowsNpuDxCoreEngineIndex AdapterEngineIndex;
    public uint ProcessId;
}

[StructLayout(LayoutKind.Sequential)]
internal struct WindowsNpuDxCoreEngineNameInput
{
    public WindowsNpuDxCoreEngineIndex AdapterEngineIndex;
    public uint EngineNameLength;
    public nint EngineName;
}

[StructLayout(LayoutKind.Sequential)]
internal struct WindowsNpuDxCoreEngineNameOutput
{
    public uint EngineNameLength;
}
internal enum WindowsNpuDxCoreMemoryType : uint { Dedicated, Shared }

[StructLayout(LayoutKind.Sequential)]
internal struct WindowsNpuDxCoreMemoryQueryInput
{
    public uint PhysicalAdapterIndex;
    public WindowsNpuDxCoreMemoryType MemoryType;
}

[StructLayout(LayoutKind.Sequential)]
internal struct WindowsNpuDxCoreMemoryUsage
{
    public ulong Committed;
    public ulong Resident;
}
