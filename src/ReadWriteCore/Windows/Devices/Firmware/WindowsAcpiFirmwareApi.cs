using ResourceManager.ReadWrite.Information;
using System.Runtime.InteropServices;

namespace ResourceManager.ReadWrite.Windows.Devices;

internal sealed class WindowsAcpiFirmwareApi : IWindowsAcpiFirmwareApi
{
    [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.Fixed)]
    public WindowsFirmwareCallResult Enumerate(uint provider, byte[]? buffer)
    {
        var size = buffer is null
            ? WindowsFirmwareNativeMethods.EnumSystemFirmwareTablesSize(provider, IntPtr.Zero, 0)
            : WindowsFirmwareNativeMethods.EnumSystemFirmwareTables(provider, buffer, checked((uint)buffer.Length));
        return new(size, Marshal.GetLastPInvokeError());
    }

    [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.Fixed)]
    public WindowsFirmwareCallResult GetTable(uint provider, uint tableId, byte[]? buffer)
    {
        var size = buffer is null
            ? WindowsFirmwareNativeMethods.GetSystemFirmwareTableSize(provider, tableId, IntPtr.Zero, 0)
            : WindowsFirmwareNativeMethods.GetSystemFirmwareTable(provider, tableId, buffer, checked((uint)buffer.Length));
        return new(size, Marshal.GetLastPInvokeError());
    }
}
