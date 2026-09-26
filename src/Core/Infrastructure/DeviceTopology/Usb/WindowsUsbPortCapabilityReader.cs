using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

namespace ResourceManager.App.Infrastructure.DeviceTopology;

internal static class WindowsUsbPortCapabilityReader
{
    private const uint IoctlUsbGetNodeConnectionInformationExV2 = 0x0022045C;
    private const uint UsbProtocol11 = 1u << 0;
    private const uint UsbProtocol20 = 1u << 1;
    private const uint UsbProtocol30 = 1u << 2;
    private const uint OperatingAtSuperSpeedOrHigher = 1u << 0;
    private const uint SuperSpeedCapableOrHigher = 1u << 1;
    private const uint OperatingAtSuperSpeedPlusOrHigher = 1u << 2;
    private const uint SuperSpeedPlusCapableOrHigher = 1u << 3;

    public static DeviceTopologyUsbPortCapability? Read(SafeFileHandle hub, uint portNumber)
    {
        var info = new UsbNodeConnectionInformationExV2
        {
            ConnectionIndex = portNumber,
            Length = (uint)Marshal.SizeOf<UsbNodeConnectionInformationExV2>(),
            SupportedUsbProtocols = UsbProtocol30
        };
        var size = info.Length;
        if (!DeviceIoControl(
                hub,
                IoctlUsbGetNodeConnectionInformationExV2,
                ref info,
                size,
                ref info,
                size,
                out var bytesReturned,
                nint.Zero)
            || bytesReturned < size)
        {
            return null;
        }

        return new DeviceTopologyUsbPortCapability(
            (info.SupportedUsbProtocols & UsbProtocol11) != 0,
            (info.SupportedUsbProtocols & UsbProtocol20) != 0,
            (info.SupportedUsbProtocols & UsbProtocol30) != 0,
            (info.Flags & OperatingAtSuperSpeedOrHigher) != 0,
            (info.Flags & SuperSpeedCapableOrHigher) != 0,
            (info.Flags & OperatingAtSuperSpeedPlusOrHigher) != 0,
            (info.Flags & SuperSpeedPlusCapableOrHigher) != 0);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device,
        uint ioControlCode,
        ref UsbNodeConnectionInformationExV2 inBuffer,
        uint inBufferSize,
        ref UsbNodeConnectionInformationExV2 outBuffer,
        uint outBufferSize,
        out uint bytesReturned,
        nint overlapped);

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct UsbNodeConnectionInformationExV2
    {
        public uint ConnectionIndex;
        public uint Length;
        public uint SupportedUsbProtocols;
        public uint Flags;
    }
}
