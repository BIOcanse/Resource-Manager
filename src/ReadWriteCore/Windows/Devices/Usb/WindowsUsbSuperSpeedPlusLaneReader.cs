using Microsoft.Win32.SafeHandles;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace ResourceManager.ReadWrite.Windows.Devices.Usb;

/// <summary>Raw output of IOCTL_USB_GET_NODE_CONNECTION_SUPERSPEEDPLUS_INFORMATION.</summary>
internal readonly record struct UsbSuperSpeedPlusLaneInformation(
    uint ConnectionIndex,
    uint Length,
    uint RxRateAttribute,
    uint RxLaneCountZeroBased,
    uint TxRateAttribute,
    uint TxLaneCountZeroBased);

internal delegate bool UsbHubIoctlTransport(
    SafeFileHandle hub,
    uint controlCode,
    byte[] inOutBuffer,
    out uint bytesReturned);

/// <summary>Reads the V2 lane ABI from the same hub handle and one-based port.</summary>
internal static class WindowsUsbSuperSpeedPlusLaneReader
{
    // CTL_CODE(FILE_DEVICE_USB=0x22, function=289, METHOD_BUFFERED=0, FILE_ANY_ACCESS=0)
    internal const uint IoctlCode = 0x00220484;
    internal const uint StructureLength = 24;

    public static UsbSuperSpeedPlusLaneInformation? Read(
        SafeFileHandle hub,
        uint oneBasedPortNumber,
        UsbHubIoctlTransport? transport = null)
    {
        if (hub.IsInvalid || oneBasedPortNumber == 0)
        {
            return null;
        }

        var buffer = new byte[StructureLength];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0, 4), oneBasedPortNumber);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4, 4), StructureLength);
        var ioctl = transport ?? InvokeDeviceIoControl;
        if (!ioctl(hub, IoctlCode, buffer, out var bytesReturned)
            || bytesReturned != StructureLength
            || BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(0, 4)) != oneBasedPortNumber
            || BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(4, 4)) != StructureLength)
        {
            return null;
        }

        return new UsbSuperSpeedPlusLaneInformation(
            oneBasedPortNumber,
            StructureLength,
            BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(8, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(12, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(16, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(20, 4)));
    }

    private static bool InvokeDeviceIoControl(
        SafeFileHandle hub,
        uint controlCode,
        byte[] inOutBuffer,
        out uint bytesReturned)
    {
        ref var start = ref MemoryMarshal.GetArrayDataReference(inOutBuffer);
        return DeviceIoControl(hub, controlCode, ref start, (uint)inOutBuffer.Length,
            ref start, (uint)inOutBuffer.Length, out bytesReturned, nint.Zero);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device,
        uint ioControlCode,
        ref byte inBuffer,
        uint inBufferSize,
        ref byte outBuffer,
        uint outBufferSize,
        out uint bytesReturned,
        nint overlapped);
}
