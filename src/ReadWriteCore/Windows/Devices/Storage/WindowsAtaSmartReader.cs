using ResourceManager.ReadWrite.Windows.Devices.Models;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using ResourceManager.App.Domain.Metrics;

namespace ResourceManager.ReadWrite.Windows.Devices;

[Flags]
public enum AtaSmartPages { None = 0, Data = 1, Thresholds = 2 }
public sealed record AtaSmartRead(IReadOnlyDictionary<MetricFieldCode, byte[]> BytesValues);

public sealed record AtaSmartAttributeDataPage(ushort Revision, IReadOnlyList<AtaSmartAttributeDataSlot> Slots);
public sealed record AtaSmartAttributeDataSlot(byte SlotIndex, byte AttributeId, ushort Flags,
    byte CurrentNormalized, byte WorstNormalized, byte[] RawSixBytes, byte ReservedByte);
public sealed record AtaSmartAttributeThresholdPage(ushort Revision, IReadOnlyList<AtaSmartAttributeThresholdSlot> Slots);
public sealed record AtaSmartAttributeThresholdSlot(byte SlotIndex, byte AttributeId, byte Threshold, byte[] ReservedTenBytes);

// The Windows receive IOCTL requires a read/write handle. Only the two read commands below are constructed.
// There is no SMART enable, save, test, write, or arbitrary register input.
public static class WindowsAtaSmartReader
{
    internal const uint ReceiveIoctl = 0x0007C088;
    internal const uint QueryPropertyIoctl = 0x002D1400;
    internal const uint DeviceAccess = 0xC0000000; // GENERIC_READ | GENERIC_WRITE required by SMART_RCV_DRIVE_DATA.
    internal const int PageSize = 512;
    internal const int InputHeaderSize = 32; // sizeof(SENDCMDINPARAMS)-1, packed in ntdddisk.h.
    internal const int OutputHeaderSize = 16; // sizeof(SENDCMDOUTPARAMS)-1.
    private const int SmartPageRevisionSize = 2;
    private const int SmartSlotSize = 12;
    private const int SmartSlotCount = 30;

    internal static bool IsAtaBus(int? bus) => bus is 3 or 11; // BusTypeAta / BusTypeSata; no USB, RAID or SAS guess.
    internal static AtaSmartPages PageFor(MetricFieldCode code) => code switch
    {
        MetricFieldCode.AtaSmartDataRaw => AtaSmartPages.Data,
        MetricFieldCode.AtaSmartThresholdsRaw => AtaSmartPages.Thresholds,
        _ => AtaSmartPages.None
    };

    public static AtaSmartRead? Read(int diskNumber, string expectedSerial, AtaSmartPages pages,
        string? expectedUniqueId, int? uniqueIdFormat)
        => OperatingSystem.IsWindows() ? Read(diskNumber, expectedSerial, pages, Open, expectedUniqueId, uniqueIdFormat) : null;

    internal static AtaSmartRead? Read(int diskNumber, string expectedSerial, AtaSmartPages pages,
        Func<int, IWindowsAtaSmartQuery?> open, string? expectedUniqueId = null, int? uniqueIdFormat = null)
    {
        if (diskNumber < 0 || string.IsNullOrWhiteSpace(expectedSerial) || !ValidSelection(pages)) return null;
        using var device = open(diskNumber);
        if (device is null) return null;
        // Verify identity and transport on the same opened object used for both reads.
        var descriptor = device.Query(QueryPropertyIoctl, WindowsStoragePropertyQuery.Create(0), 4096);
        if (descriptor is null || !string.Equals(ParseDeviceSerial(descriptor), expectedSerial.Trim(),
                StringComparison.OrdinalIgnoreCase)) return null;
        if (!string.IsNullOrWhiteSpace(expectedUniqueId))
        {
            if (uniqueIdFormat is not (0 or 1 or 2 or 3 or 8)) return null;
            var identifiers = device.Query(QueryPropertyIoctl, WindowsStoragePropertyQuery.Create(2), 4096);
            if (identifiers is null || !MatchesUniqueId(identifiers, expectedUniqueId.Trim(), uniqueIdFormat.Value)) return null;
        }
        var values = new Dictionary<MetricFieldCode, byte[]>();
        ReadPage(AtaSmartPages.Data, MetricFieldCode.AtaSmartDataRaw);
        ReadPage(AtaSmartPages.Thresholds, MetricFieldCode.AtaSmartThresholdsRaw);
        return new(values);

        void ReadPage(AtaSmartPages page, MetricFieldCode field)
        {
            if ((pages & page) == 0) return;
            var response = device.Query(ReceiveIoctl, CreateReadQuery(page), OutputHeaderSize + PageSize);
            if (response is not null && ParseReadResponse(response) is { } bytes) values.Add(field, bytes);
        }
    }

    private static bool ValidSelection(AtaSmartPages pages)
        => pages != AtaSmartPages.None && (pages & ~(AtaSmartPages.Data | AtaSmartPages.Thresholds)) == 0;

    internal static byte[] CreateReadQuery(AtaSmartPages page)
    {
        var feature = page switch
        {
            AtaSmartPages.Data => (byte)0xD0, // READ_ATTRIBUTES
            AtaSmartPages.Thresholds => (byte)0xD1, // READ_THRESHOLDS
            _ => throw new ArgumentOutOfRangeException(nameof(page))
        };
        var input = new byte[InputHeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(input, PageSize);
        input[4] = feature;
        input[5] = 1; // SectorCount
        input[6] = 1; // SectorNumber
        input[7] = 0x4F; // SMART_CYL_LOW
        input[8] = 0xC2; // SMART_CYL_HI
        input[9] = 0xA0; // Legacy drive/head format; routing is determined by the open handle.
        input[10] = 0xB0; // SMART_CMD
        // bDriveNumber is opaque/ignored by Windows. Reserved bytes remain zero.
        return input;
    }

    public static byte[]? ParseReadResponse(ReadOnlySpan<byte> response)
    {
        if (response.Length != OutputHeaderSize + PageSize
            || BinaryPrimitives.ReadUInt32LittleEndian(response) != PageSize
            || response[4] != 0) return null; // bDriverError; bIDEError is only valid when this reports an error.
        var page = response[OutputHeaderSize..];
        if (!HasValidSmartPageChecksum(page)) return null;
        // Return the validated page intact. Typed tables are separate views and do not replace the raw page.
        return page.ToArray();
    }

    public static AtaSmartAttributeDataPage? ParseAttributeDataPage(ReadOnlySpan<byte> page)
    {
        if (!HasValidSmartPageChecksum(page)) return null;
        var slots = new AtaSmartAttributeDataSlot[SmartSlotCount];
        for (var slot = 0; slot < slots.Length; slot++)
        {
            var entry = page.Slice(SmartPageRevisionSize + slot * SmartSlotSize, SmartSlotSize);
            slots[slot] = new((byte)slot, entry[0], BinaryPrimitives.ReadUInt16LittleEndian(entry[1..]),
                entry[3], entry[4], entry.Slice(5, 6).ToArray(), entry[11]);
        }
        return new(BinaryPrimitives.ReadUInt16LittleEndian(page), slots);
    }

    public static AtaSmartAttributeThresholdPage? ParseAttributeThresholdPage(ReadOnlySpan<byte> page)
    {
        if (!HasValidSmartPageChecksum(page)) return null;
        var slots = new AtaSmartAttributeThresholdSlot[SmartSlotCount];
        for (var slot = 0; slot < slots.Length; slot++)
        {
            var entry = page.Slice(SmartPageRevisionSize + slot * SmartSlotSize, SmartSlotSize);
            slots[slot] = new((byte)slot, entry[0], entry[1], entry[2..].ToArray());
        }
        return new(BinaryPrimitives.ReadUInt16LittleEndian(page), slots);
    }

    private static bool HasValidSmartPageChecksum(ReadOnlySpan<byte> page)
    {
        if (page.Length != PageSize) return false;
        var sum = 0;
        foreach (var value in page) sum = (sum + value) & 0xFF;
        return sum == 0;
    }

    internal static string? ParseDeviceSerial(ReadOnlySpan<byte> descriptor, bool allowOtherBus = false)
    {
        if (descriptor.Length < 36) return null;
        var size = BinaryPrimitives.ReadUInt32LittleEndian(descriptor[4..]);
        var offset = BinaryPrimitives.ReadUInt32LittleEndian(descriptor[24..]);
        var bus = BinaryPrimitives.ReadUInt32LittleEndian(descriptor[28..]);
        if (size < 36 || size > descriptor.Length || (!allowOtherBus && !IsAtaBus((int)bus))
            || offset < 36 || offset >= size) return null;
        var bytes = descriptor.Slice((int)offset, (int)(size - offset));
        var end = bytes.IndexOf((byte)0);
        if (end < 0) return null;
        foreach (var value in bytes[..end]) if (value < 32 || value > 126) return null;
        var serial = Encoding.ASCII.GetString(bytes[..end]).Trim();
        return serial.Length == 0 ? null : serial;
    }

    internal static bool MatchesUniqueId(ReadOnlySpan<byte> descriptor, string expected, int format)
    {
        if (descriptor.Length < 12 || string.IsNullOrWhiteSpace(expected) || format is not (0 or 1 or 2 or 3 or 8)) return false;
        var size = BinaryPrimitives.ReadUInt32LittleEndian(descriptor[4..]);
        var count = BinaryPrimitives.ReadUInt32LittleEndian(descriptor[8..]);
        if (BinaryPrimitives.ReadUInt32LittleEndian(descriptor) != 16 || size < 12 || size > descriptor.Length
            || count == 0 || count > (size - 12) / 16) return false;
        var offset = 12;
        var matched = 0;
        for (var i = 0U; i < count; i++)
        {
            if ((ulong)offset + 16 > size) return false;
            var identifier = descriptor.Slice(offset, (int)size - offset);
            var length = BinaryPrimitives.ReadUInt16LittleEndian(identifier[8..]);
            var next = BinaryPrimitives.ReadUInt16LittleEndian(identifier[10..]);
            if (length == 0 || (uint)length + 16 > identifier.Length) return false;
            var type = BinaryPrimitives.ReadUInt32LittleEndian(identifier[4..]);
            if (BinaryPrimitives.ReadUInt32LittleEndian(identifier[12..]) == 0) // Device association, never port/target.
            {
                // MSFT_Disk/PhysicalDisk must select the highest available ID type, not a lower alias.
                if (IdentifierRank(type) > IdentifierRank((uint)format)) return false;
                if (type == (uint)format)
                {
                    var text = DecodeIdentifier(identifier.Slice(16, length), BinaryPrimitives.ReadUInt32LittleEndian(identifier), type);
                    if (string.Equals(text, expected.Trim(), StringComparison.OrdinalIgnoreCase)) matched++;
                }
            }
            if (i + 1 == count)
            {
                if (next != 0) return false;
            }
            else
            {
                if (next < 16 + length || (ulong)offset + next + 16 > size) return false;
                offset += next;
            }
        }
        return matched == 1;
    }

    private static int IdentifierRank(uint type) => type switch { 8 => 5, 3 => 4, 2 => 3, 1 => 2, 0 => 1, _ => 0 };
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static string? DecodeIdentifier(ReadOnlySpan<byte> bytes, uint codeSet, uint type)
    {
        // Match the complete modern byte-to-hex representation used by Microsoft's DSM sample.
        // No legacy shortening, endian reversal or provider-specific prefixes are inferred.
        if (codeSet == 1) return type is <= 3 ? Convert.ToHexString(bytes) : null;
        if (codeSet is not (2 or 3)) return null;
        var end = bytes.IndexOf((byte)0);
        if (end >= 0)
        {
            foreach (var value in bytes[end..]) if (value != 0) return null;
            bytes = bytes[..end];
        }
        if (codeSet == 2)
        {
            foreach (var value in bytes) if (value < 32 || value > 126) return null;
            return Encoding.ASCII.GetString(bytes).Trim();
        }
        try
        {
            var text = StrictUtf8.GetString(bytes).Trim();
            return text.Any(char.IsControl) ? null : text;
        }
        catch (DecoderFallbackException) { return null; }
    }

    private static IWindowsAtaSmartQuery? Open(int number)
    {
        var handle = CreateFileW($@"\\.\PhysicalDrive{number}", DeviceAccess, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (!handle.IsInvalid) return new DeviceQuery(handle);
        handle.Dispose();
        return null;
    }

    private sealed class DeviceQuery(SafeFileHandle handle) : IWindowsAtaSmartQuery
    {
        public byte[]? Query(uint ioctl, byte[] input, int outputLength)
        {
            var output = new byte[outputLength];
            return DeviceIoControl(handle, ioctl, input, (uint)input.Length, output, (uint)output.Length,
                out var returned, IntPtr.Zero) && returned <= output.Length ? output[..(int)returned] : null;
        }
        public void Dispose() => handle.Dispose();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint controlCode, byte[] input,
        uint inputSize, byte[] output, uint outputSize, out uint bytesReturned, IntPtr overlapped);
}

internal interface IWindowsAtaSmartQuery : IDisposable
{
    byte[]? Query(uint ioctl, byte[] input, int outputLength);
}
