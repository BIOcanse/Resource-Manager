using System.Runtime.InteropServices;

namespace ResourceManager.ReadWrite.Windows.Devices;

internal interface IWindowsNetworkInterfaceCounterApi
{
    byte[]? ReadInterfaces();
    byte[]? ReadInterface(ulong luid);
}

internal sealed class WindowsNetworkInterfaceCounterApi : IWindowsNetworkInterfaceCounterApi
{
    public byte[]? ReadInterfaces() => new WindowsNetworkInterfaceTableApi().ReadInterfaces();

    public unsafe byte[]? ReadInterface(ulong luid)
    {
        if (!OperatingSystem.IsWindows() || luid == 0) return null;
        var row = new byte[WindowsNetworkInterfaceTableDecoder.InterfaceRowSize];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(row, luid);
        fixed (byte* pointer = row)
        {
            return GetIfEntry2((nint)pointer) == 0 ? row : null;
        }
    }

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern uint GetIfEntry2(nint row);
}
