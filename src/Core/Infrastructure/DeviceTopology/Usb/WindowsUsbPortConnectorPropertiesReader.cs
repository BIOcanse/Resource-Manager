using Microsoft.Win32.SafeHandles;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace ResourceManager.App.Infrastructure.DeviceTopology;

internal static class WindowsUsbPortConnectorPropertiesReader
{
    private const uint IoctlUsbGetPortConnectorProperties = 0x00220458;
    private const int FixedBufferLength = 18;
    private const int CompanionNameOffset = 16;
    private const int MaximumBufferLength = 64 * 1024;
    private const int MaximumCompanionCount = 16;
    private const uint PortIsUserConnectable = 1u << 0;
    private const uint PortIsDebugCapable = 1u << 1;
    private const uint PortHasMultipleCompanions = 1u << 2;
    private const uint PortConnectorIsTypeC = 1u << 3;

    public static DeviceTopologyUsbConnectorProperties? Read(SafeFileHandle hub, uint portNumber)
    {
        var first = ReadEntry(hub, portNumber, companionIndex: 0);
        if (first is null)
        {
            return null;
        }

        var companions = new List<DeviceTopologyUsbCompanionPortInfo>();
        AddCompanion(companions, first);
        if ((first.PropertyFlags & PortHasMultipleCompanions) != 0)
        {
            for (ushort companionIndex = 1; companionIndex < MaximumCompanionCount; companionIndex++)
            {
                var next = ReadEntry(hub, portNumber, companionIndex);
                if (next is null
                    || (next.CompanionPortNumber == 0 && string.IsNullOrWhiteSpace(next.CompanionHubSymbolicLinkName)))
                {
                    break;
                }

                AddCompanion(companions, next);
            }
        }

        return new DeviceTopologyUsbConnectorProperties(
            (first.PropertyFlags & PortIsUserConnectable) != 0,
            (first.PropertyFlags & PortIsDebugCapable) != 0,
            (first.PropertyFlags & PortHasMultipleCompanions) != 0,
            (first.PropertyFlags & PortConnectorIsTypeC) != 0,
            companions);
    }

    private static void AddCompanion(
        ICollection<DeviceTopologyUsbCompanionPortInfo> companions,
        DeviceTopologyUsbConnectorPropertiesEntry entry)
    {
        if (entry.CompanionPortNumber == 0 && string.IsNullOrWhiteSpace(entry.CompanionHubSymbolicLinkName))
        {
            return;
        }

        companions.Add(new DeviceTopologyUsbCompanionPortInfo(
            entry.CompanionIndex,
            entry.CompanionPortNumber,
            entry.CompanionHubSymbolicLinkName));
    }

    private static DeviceTopologyUsbConnectorPropertiesEntry? ReadEntry(
        SafeFileHandle hub,
        uint portNumber,
        ushort companionIndex)
    {
        var initial = new byte[FixedBufferLength];
        var initialSuccess = Query(hub, initial, portNumber, companionIndex, out var initialBytesReturned);
        var actualLength = BinaryPrimitives.ReadUInt32LittleEndian(initial.AsSpan(4, sizeof(uint)));
        if (actualLength < FixedBufferLength || actualLength > MaximumBufferLength)
        {
            if (!initialSuccess)
            {
                return null;
            }

            actualLength = FixedBufferLength;
        }

        if (actualLength == FixedBufferLength)
        {
            return initialSuccess && initialBytesReturned >= CompanionNameOffset
                ? ParseEntry(initial, initialBytesReturned)
                : null;
        }

        var buffer = new byte[(int)actualLength];
        return Query(hub, buffer, portNumber, companionIndex, out var bytesReturned)
            && bytesReturned >= CompanionNameOffset
            ? ParseEntry(buffer, bytesReturned)
            : null;
    }

    private static bool Query(
        SafeFileHandle hub,
        byte[] buffer,
        uint portNumber,
        ushort companionIndex,
        out uint bytesReturned)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0, sizeof(uint)), portNumber);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(12, sizeof(ushort)), companionIndex);
        ref var start = ref MemoryMarshal.GetArrayDataReference(buffer);
        return DeviceIoControl(
            hub,
            IoctlUsbGetPortConnectorProperties,
            ref start,
            (uint)buffer.Length,
            ref start,
            (uint)buffer.Length,
            out bytesReturned,
            nint.Zero);
    }

    internal static DeviceTopologyUsbConnectorPropertiesEntry? ParseEntry(
        ReadOnlySpan<byte> buffer,
        uint bytesReturned)
    {
        if (buffer.Length < CompanionNameOffset || bytesReturned < CompanionNameOffset)
        {
            return null;
        }

        var availableLength = Math.Min(buffer.Length, (int)Math.Min(bytesReturned, (uint)buffer.Length));
        var nameByteLength = Math.Max(0, availableLength - CompanionNameOffset);
        nameByteLength -= nameByteLength & 1;
        var companionName = nameByteLength == 0
            ? null
            : Encoding.Unicode.GetString(buffer.Slice(CompanionNameOffset, nameByteLength)).TrimEnd('\0');

        return new DeviceTopologyUsbConnectorPropertiesEntry(
            BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(8, sizeof(uint))),
            BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(12, sizeof(ushort))),
            BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(14, sizeof(ushort))),
            string.IsNullOrWhiteSpace(companionName) ? null : companionName);
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
