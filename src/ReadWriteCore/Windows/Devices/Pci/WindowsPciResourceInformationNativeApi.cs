using ResourceManager.ReadWrite.Windows.Devices.Models;
using System.Runtime.InteropServices;
using System.Text;

namespace ResourceManager.ReadWrite.Windows.Devices;

internal sealed class WindowsPciResourceInformationNativeApi : IWindowsPciResourceInformationApi
{
    // CM_RESDES_WIDTH_DEFAULT follows the native client, not the buffer length.
    public int DescriptorPointerBytes => IntPtr.Size;
    [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.Fixed)]
    public string? GetIdentity(uint devnode)
    {
        if (CM_Get_Device_ID_Size(out var length, devnode, 0) != 0 || length == 0 || length >= 16384) return null;
        var value = new StringBuilder((int)length + 1);
        return CM_Get_Device_IDW(devnode, value, (uint)value.Capacity, 0) == 0 ? value.ToString() : null;
    }
    [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.EventDriven, "Windows PnP device properties changed")]
    public uint FirstConfiguration(uint devnode, out nuint configuration) => CM_Get_First_Log_Conf(out configuration, devnode, 2);
    [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.EventDriven, "Windows PnP device properties changed")]
    public uint NextConfiguration(nuint previous, out nuint configuration) => CM_Get_Next_Log_Conf(out configuration, previous, 0);
    [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.EventDriven, "Windows PnP device properties changed")]
    public uint NextResource(nuint previous, out nuint resource, out uint type) => CM_Get_Next_Res_Des(out resource, previous, 0, out type, 0);
    [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.EventDriven, "Windows PnP device properties changed")]
    public uint DataSize(nuint resource, out uint bytes) => CM_Get_Res_Des_Data_Size(out bytes, resource, 0);
    [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.EventDriven, "Windows PnP device properties changed")]
    public uint Data(nuint resource, byte[] buffer) => CM_Get_Res_Des_Data(resource, buffer, (uint)buffer.Length, 0);
    public uint ReleaseConfiguration(nuint configuration) => CM_Free_Log_Conf_Handle(configuration);
    public uint ReleaseResource(nuint resource) => CM_Free_Res_Des_Handle(resource);

    [DllImport("cfgmgr32.dll", ExactSpelling = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint CM_Get_Device_ID_Size(out uint length, uint devnode, uint flags);
    [DllImport("cfgmgr32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint CM_Get_Device_IDW(uint devnode, StringBuilder value, uint length, uint flags);
    [DllImport("cfgmgr32.dll", ExactSpelling = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint CM_Get_First_Log_Conf(out nuint configuration, uint devnode, uint flags);
    [DllImport("cfgmgr32.dll", ExactSpelling = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint CM_Get_Next_Log_Conf(out nuint configuration, nuint previous, uint flags);
    [DllImport("cfgmgr32.dll", ExactSpelling = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint CM_Get_Next_Res_Des(out nuint resource, nuint previous, uint requestedType, out uint type, uint flags);
    [DllImport("cfgmgr32.dll", ExactSpelling = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint CM_Get_Res_Des_Data_Size(out uint size, nuint resource, uint flags);
    [DllImport("cfgmgr32.dll", ExactSpelling = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint CM_Get_Res_Des_Data(nuint resource, [Out] byte[] buffer, uint size, uint flags);
    [DllImport("cfgmgr32.dll", ExactSpelling = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint CM_Free_Log_Conf_Handle(nuint configuration);
    [DllImport("cfgmgr32.dll", ExactSpelling = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint CM_Free_Res_Des_Handle(nuint resource);
}
