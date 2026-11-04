using System.Runtime.InteropServices;

namespace ResourceManager.ReadWrite.Windows.Devices.Npu;

// Full inherited vtable order from Microsoft's DirectX-Headers dxcore_interface.h.
internal enum WindowsNpuDxCoreFactorySlot
{
    QueryInterface, AddRef, Release, CreateAdapterList, GetAdapterByLuid,
    IsNotificationTypeSupported, RegisterEventNotification, UnregisterEventNotification
}

internal enum WindowsNpuDxCoreListSlot
{
    QueryInterface, AddRef, Release, GetAdapter, GetAdapterCount, IsStale, GetFactory,
    Sort, IsAdapterPreferenceSupported
}

internal enum WindowsNpuDxCoreAdapterSlot
{
    QueryInterface, AddRef, Release, IsValid, IsAttributeSupported, IsPropertySupported,
    GetProperty, GetPropertySize, IsQueryStateSupported, QueryState,
    IsSetStateSupported, SetState, GetFactory
}

// The inherited 0..12 slots are identical to IDXCoreAdapter; Adapter1 appends slot13.
internal enum WindowsNpuDxCoreAdapter1Slot
{
    QueryInterface, AddRef, Release, IsValid, IsAttributeSupported, IsPropertySupported,
    GetProperty, GetPropertySize, IsQueryStateSupported, QueryState,
    IsSetStateSupported, SetState, GetFactory, GetPropertyWithInput
}

internal sealed unsafe class WindowsNpuDxCoreNativeApi : IWindowsNpuDxCoreEngineApi
{
    private static readonly Guid FactoryIid = new("78EE5945-C36E-4B13-A669-005DD11C0F06");
    private static readonly Guid ListIid = new("526C7776-40E9-459B-B711-F32AD76DFC28");
    private static readonly Guid AdapterIid = new("F0DB4C7F-FE5A-42A2-BD62-F2A6CF6FC83E");

    private static readonly Guid Adapter1Iid = new("A0783366-CFA3-43BE-9D79-55B2DA97C63C");

    [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.Fixed)]
    public int QueryAdapter1(nint adapter, out nint adapter1)
    {
        var iid = Adapter1Iid;
        nint value = 0;
        var status = ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)
            Slot(adapter, (int)WindowsNpuDxCoreAdapterSlot.QueryInterface))(adapter, &iid, &value);
        adapter1 = value;
        return status;
    }

    [ResourceManager.ReadWrite.RuntimeReadTransport]
    public int GetPropertyWithInput(nint adapter1, WindowsNpuDxCoreProperty property,
        nuint inputSize, nint input, nuint outputSize, nint output)
        => ((delegate* unmanaged[Stdcall]<nint, WindowsNpuDxCoreProperty, nuint, nint, nuint, nint, int>)
            Slot(adapter1, (int)WindowsNpuDxCoreAdapter1Slot.GetPropertyWithInput))
                (adapter1, property, inputSize, input, outputSize, output);

    public int CreateFactory(out nint factory)
    {
        factory = 0;
        if (!OperatingSystem.IsWindows()) return unchecked((int)0x80004001);
        var iid = FactoryIid;
        return DXCoreCreateAdapterFactory(in iid, out factory);
    }

    [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.EventDriven, "Windows PnP device properties changed")]
    public int CreateAdapterList(nint factory, Guid attribute, out nint list)
    {
        var iid = ListIid;
        nint value = 0;
        var call = (delegate* unmanaged[Stdcall]<nint, uint, Guid*, Guid*, nint*, int>)
            Slot(factory, (int)WindowsNpuDxCoreFactorySlot.CreateAdapterList);
        var status = call(factory, 1, &attribute, &iid, &value);
        list = value;
        return status;
    }

    [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.EventDriven, "Windows PnP device properties changed")]
    public uint GetAdapterCount(nint list)
        => ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(list, (int)WindowsNpuDxCoreListSlot.GetAdapterCount))(list);

    [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.Sampled)]
    public bool IsStale(nint list)
        => ((delegate* unmanaged[Stdcall]<nint, byte>)Slot(list, (int)WindowsNpuDxCoreListSlot.IsStale))(list) != 0;

    [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.EventDriven, "Windows PnP device properties changed")]
    public int GetAdapter(nint list, uint index, out nint adapter)
    {
        var iid = AdapterIid;
        nint value = 0;
        var status = ((delegate* unmanaged[Stdcall]<nint, uint, Guid*, nint*, int>)
            Slot(list, (int)WindowsNpuDxCoreListSlot.GetAdapter))(list, index, &iid, &value);
        adapter = value;
        return status;
    }

    [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.Sampled)]
    public bool IsValid(nint adapter)
        => ((delegate* unmanaged[Stdcall]<nint, byte>)Slot(adapter, (int)WindowsNpuDxCoreAdapterSlot.IsValid))(adapter) != 0;

    [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.Fixed)]
    public bool IsAttributeSupported(nint adapter, Guid attribute)
        => ((delegate* unmanaged[Stdcall]<nint, Guid*, byte>)
            Slot(adapter, (int)WindowsNpuDxCoreAdapterSlot.IsAttributeSupported))(adapter, &attribute) != 0;

    [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.Fixed)]
    public bool IsPropertySupported(nint adapter, WindowsNpuDxCoreProperty property)
        => ((delegate* unmanaged[Stdcall]<nint, WindowsNpuDxCoreProperty, byte>)
            Slot(adapter, (int)WindowsNpuDxCoreAdapterSlot.IsPropertySupported))(adapter, property) != 0;

    [ResourceManager.ReadWrite.RuntimeReadTransport]
    public int GetProperty(nint adapter, WindowsNpuDxCoreProperty property, nuint size, nint data)
        => ((delegate* unmanaged[Stdcall]<nint, WindowsNpuDxCoreProperty, nuint, nint, int>)
            Slot(adapter, (int)WindowsNpuDxCoreAdapterSlot.GetProperty))(adapter, property, size, data);

    [ResourceManager.ReadWrite.RuntimeReadTransport]
    public int GetPropertySize(nint adapter, WindowsNpuDxCoreProperty property, out nuint size)
    {
        nuint value = 0;
        var status = ((delegate* unmanaged[Stdcall]<nint, WindowsNpuDxCoreProperty, nuint*, int>)
            Slot(adapter, (int)WindowsNpuDxCoreAdapterSlot.GetPropertySize))(adapter, property, &value);
        size = value;
        return status;
    }

    [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.Fixed)]
    public bool IsQueryStateSupported(nint adapter, WindowsNpuDxCoreState state)
        => ((delegate* unmanaged[Stdcall]<nint, WindowsNpuDxCoreState, byte>)
            Slot(adapter, (int)WindowsNpuDxCoreAdapterSlot.IsQueryStateSupported))(adapter, state) != 0;

    [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.Sampled)]
    public int QueryState(nint adapter, WindowsNpuDxCoreState state, nuint inputSize, nint input,
        nuint outputSize, nint output)
        => ((delegate* unmanaged[Stdcall]<nint, WindowsNpuDxCoreState, nuint, nint, nuint, nint, int>)
            Slot(adapter, (int)WindowsNpuDxCoreAdapterSlot.QueryState))(adapter, state,
                inputSize, input, outputSize, output);

    public void Release(nint instance)
        => _ = ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(instance, 2))(instance);

    private static nint Slot(nint instance, int index) => (*(nint**)instance)[index];

    [DllImport("dxcore.dll", ExactSpelling = true)]
    private static extern int DXCoreCreateAdapterFactory(in Guid iid, out nint factory);
}
