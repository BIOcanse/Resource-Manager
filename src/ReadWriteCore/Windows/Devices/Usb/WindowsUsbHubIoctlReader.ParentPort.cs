using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace ResourceManager.ReadWrite.Windows.Devices.Usb;

public static partial class WindowsUsbHubIoctlReader
{
    internal static bool IsAttachedPort(DeviceTopologyUsbPort port) =>
        port.DeviceConnected && !string.IsNullOrWhiteSpace(port.HubDevicePath) && port.PortNumber > 0
        && !string.IsNullOrWhiteSpace(port.DriverKeyName);

    internal static IReadOnlyDictionary<string, DeviceTopologyUsbPort> CreateDriverKeyIndex(
        IReadOnlyList<DeviceTopologyUsbPort> ports) => ports
        .Where(IsAttachedPort)
        .GroupBy(static port => NormalizeDriverKey(port.DriverKeyName), StringComparer.OrdinalIgnoreCase)
        .Where(static group => group.Count() == 1)
        .ToDictionary(static group => group.Key, static group => group.Single(), StringComparer.OrdinalIgnoreCase);

    internal static UsbNodeConnectionInformationEx? ReadConnectionInformation(
        ReadOnlySpan<byte> buffer, uint bytesReturned, uint portNumber)
    {
        // The immediate parent is in the SDK fixed header; a PipeList tail is not required.
        if (portNumber == 0 || bytesReturned < Marshal.SizeOf<UsbNodeConnectionInformationEx>()
            || bytesReturned > buffer.Length)
        {
            return null;
        }

        var info = MemoryMarshal.Read<UsbNodeConnectionInformationEx>(buffer);
        return info.ConnectionIndex == portNumber ? info : null;
    }

    internal static string? ReadDriverKeyName(ReadOnlySpan<byte> buffer, uint bytesReturned, uint portNumber)
    {
        const int nameOffset = 8;
        if (portNumber == 0 || bytesReturned < nameOffset || bytesReturned > buffer.Length
            || BinaryPrimitives.ReadUInt32LittleEndian(buffer) != portNumber)
        {
            return null;
        }

        var length = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(sizeof(uint), sizeof(uint)));
        if (length < nameOffset + sizeof(char) || length > bytesReturned || (length - nameOffset) % sizeof(char) != 0)
        {
            return null;
        }

        var name = MemoryMarshal.Cast<byte, char>(buffer.Slice(nameOffset, (int)length - nameOffset));
        var terminator = name.IndexOf('\0');
        if (terminator <= 0)
        {
            return null;
        }

        var key = new string(name[..terminator]);
        return string.IsNullOrWhiteSpace(key) ? null : key;
    }
}
