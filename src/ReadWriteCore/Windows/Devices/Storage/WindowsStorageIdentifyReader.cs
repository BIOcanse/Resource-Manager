using System.Buffers.Binary;
using System.Text;
using ResourceManager.ReadWrite.Windows.Devices.Models;

namespace ResourceManager.ReadWrite.Windows.Devices;

public sealed record StorageIdentifyRead(AtaIdentifyInformation? Ata, NvmeControllerIdentifyInformation? Nvme);

// Fixed Identify queries only. No pass-through, command input, writes, enable or firmware operations.
public static partial class WindowsStorageIdentifyReader
{
    internal const int HeaderSize = 48;
    internal const int AtaSize = 512;
    internal const int NvmeSize = 4096;

    public static StorageIdentifyRead? Read(int number, StorageIdentifyProtocol protocol, string serial,
        string? uniqueId, int? uniqueIdFormat)
        => OperatingSystem.IsWindows() ? Read(number, protocol, serial, uniqueId, uniqueIdFormat, Open) : null;

    public static Information.DeviceInformationSnapshot ReadFields(string deviceIdentity, int number,
        StorageIdentifyProtocol protocol, string serial, string? uniqueId, int? uniqueIdFormat)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceIdentity);
        var result = Read(number, protocol, serial, uniqueId, uniqueIdFormat);
        var row = new DeviceTopologyStorageInformation(deviceIdentity, protocol, result?.Ata, result?.Nvme)
        { HardwareIdentityEvidence = result is null ? null : new(serial, uniqueId, uniqueIdFormat) };
        return Information.WindowsInformationFieldProjection.Storage([row], result is null ? null : DateTimeOffset.UtcNow);
    }

    internal static StorageIdentifyRead? Read(int number, StorageIdentifyProtocol protocol, string serial,
        string? uniqueId, int? uniqueIdFormat, Func<int, IWindowsStorageIdentifyQuery?> open)
    {
        if (number < 0 || string.IsNullOrWhiteSpace(serial) || !ValidProtocol(protocol)) return null;
        using var device = open(number);
        if (device is null) return null;
        var descriptor = device.Query(WindowsStoragePropertyQuery.Create(0), 4096);
        if (descriptor is null) return null;
        var actual = protocol == StorageIdentifyProtocol.Ata
            ? WindowsAtaSmartReader.ParseDeviceSerial(descriptor) : WindowsNvmeHealthReader.ParseDeviceSerial(descriptor);
        if (!EqualSerial(actual, serial)) return null;
        if (!string.IsNullOrWhiteSpace(uniqueId))
        {
            if (uniqueIdFormat is not (0 or 1 or 2 or 3 or 8)) return null;
            var ids = device.Query(WindowsStoragePropertyQuery.Create(2), 4096);
            if (ids is null || !WindowsAtaSmartReader.MatchesUniqueId(ids, uniqueId, uniqueIdFormat.Value)) return null;
        }
        var response = device.Query(CreateIdentifyQuery(protocol), HeaderSize + PayloadSize(protocol));
        if (response is null || ParseIdentifyResponse(response, protocol) is not { } raw) return null;
        if (protocol == StorageIdentifyProtocol.Ata)
        {
            var ata = ParseAta(raw);
            return ata is not null && EqualSerial(ata.SerialNumber, serial) ? new(ata, null) : null;
        }
        var nvme = ParseNvmeController(raw);
        return nvme is not null && EqualSerial(nvme.SerialNumber, serial) ? new(null, nvme) : null;
    }

    private static bool EqualSerial(string? a, string b)
        => !string.IsNullOrWhiteSpace(a) && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
    private static bool ValidProtocol(StorageIdentifyProtocol protocol)
        => protocol is StorageIdentifyProtocol.Ata or StorageIdentifyProtocol.NvmeController;
    private static int PayloadSize(StorageIdentifyProtocol protocol)
        => protocol == StorageIdentifyProtocol.Ata ? AtaSize : NvmeSize;

    internal static byte[] CreateIdentifyQuery(StorageIdentifyProtocol protocol)
    {
        if (!ValidProtocol(protocol)) throw new ArgumentOutOfRangeException(nameof(protocol));
        var bytes = new byte[HeaderSize + PayloadSize(protocol)];
        Write(bytes, 0, protocol == StorageIdentifyProtocol.Ata ? 50U : 49U);
        Write(bytes, 8, protocol == StorageIdentifyProtocol.Ata ? 2U : 3U);
        Write(bytes, 12, 1); // AtaDataTypeIdentify / NVMeDataTypeIdentify
        Write(bytes, 16, protocol == StorageIdentifyProtocol.Ata ? 0U : 1U); // NVME_IDENTIFY_CNS_CONTROLLER
        Write(bytes, 24, 40); // Offset from the start of STORAGE_PROTOCOL_SPECIFIC_DATA.
        Write(bytes, 28, (uint)PayloadSize(protocol));
        return bytes; // PropertyStandardQuery, namespace/subvalues and remaining input are zero.
    }

    internal static byte[]? ParseIdentifyResponse(ReadOnlySpan<byte> response, StorageIdentifyProtocol protocol)
    {
        if (!ValidProtocol(protocol) || response.Length < HeaderSize || Read32(response, 0) != HeaderSize
            || Read32(response, 4) != HeaderSize || Read32(response, 8) != (protocol == StorageIdentifyProtocol.Ata ? 2U : 3U)
            || Read32(response, 12) != 1 || Read32(response, 16) != (protocol == StorageIdentifyProtocol.Ata ? 0U : 1U)
            || Read32(response, 20) != 0) return null;
        var offset = Read32(response, 24);
        var length = Read32(response, 28);
        if (offset < 40 || length != PayloadSize(protocol) || (ulong)8 + offset + length > (ulong)response.Length) return null;
        return response.Slice(checked((int)(8 + offset)), (int)length).ToArray();
    }

    public static AtaIdentifyInformation? ParseAta(ReadOnlySpan<byte> raw)
    {
        if (raw.Length != AtaSize || IsEmpty(raw)) return null;
        var configuration = Word(raw, 0);
        // 848Ah is the specified CompactFlash ATA signature, not an IDENTIFY PACKET DEVICE response.
        if ((configuration & 0x8000) != 0 && configuration != 0x848A) return null;
        // Signature is the low byte of word 255; checksum is the high byte. Checksum is optional.
        if (raw[510] == 0xA5)
        {
            var sum = 0;
            foreach (var value in raw) sum = (sum + value) & 0xFF;
            if (sum != 0) return null;
        }
        ulong? logical = null;
        ulong? physical = null;
        var sector = Word(raw, 106);
        if ((sector & 0xC000) == 0x4000)
        {
            logical = (sector & 0x1000) == 0 ? 512UL : (ulong)Read32(raw, 234) * 2;
            if (logical < 512 || ((sector & 0x1000) != 0 && logical == 512)) logical = null;
            if (logical is { } size && ((sector & 0x2000) == 0 || (sector & 0xF) != 0))
                physical = size << ((sector & 0x2000) != 0 ? sector & 0xF : 0);
        }
        ulong? capacity = null;
        if ((Word(raw, 49) & 0x200) != 0 && logical is { } logicalSize)
        {
            var lba48 = (Word(raw, 83) & 0xC400) == 0x4400;
            var additionalValid = Word(raw, 53) != ushort.MaxValue && (Word(raw, 53) & 2) != 0;
            var additional = Word(raw, 69);
            var extended = additionalValid && (additional & 8) != 0;
            // ACS-3 7.12.7.87: a QWord container, with a maximum 48-bit logical-sector count.
            var sectors = extended ? BinaryPrimitives.ReadUInt64LittleEndian(raw[460..])
                : lba48 ? BinaryPrimitives.ReadUInt64LittleEndian(raw[200..]) : Read32(raw, 120);
            if ((!additionalValid || additional != ushort.MaxValue)
                && sectors > 0 && sectors <= (extended || lba48 ? 0x0000FFFFFFFFFFFFUL : 0x0FFFFFFFUL)
                && sectors <= ulong.MaxValue / logicalSize) capacity = sectors * logicalSize;
        }
        var rotation = Word(raw, 217);
        var validRotation = rotation == 1 || rotation is > 0x400 and < ushort.MaxValue;
        var trim = Word(raw, 169);
        var major = Word(raw, 80);
        return new(raw.ToArray(), AtaText(raw.Slice(54, 40)), AtaText(raw.Slice(20, 20)), AtaText(raw.Slice(46, 8)),
            capacity, logical, physical, trim == ushort.MaxValue ? null : (trim & 1) != 0,
            major is 0 or ushort.MaxValue ? null : major,
            validRotation && rotation != 1 ? rotation : null, validRotation ? rotation == 1 : null);
    }

    public static NvmeControllerIdentifyInformation? ParseNvmeController(ReadOnlySpan<byte> raw)
    {
        if (raw.Length != NvmeSize || IsEmpty(raw)) return null;
        var vendor = Word(raw, 0);
        if (vendor is 0 or ushort.MaxValue || Read32(raw, 516) == 0) return null;
        var version = Read32(raw, 80);
        var capacitiesSupported = version != uint.MaxValue && version >= 0x00010200
            && ((Word(raw, 128) & 8) != 0 // OACS namespace management/attachment.
                || (version >= 0x00020000 && (Read32(raw, 96) & 0x1800) != 0)); // CTRATT fixed/variable capacity management.
        return new(raw.ToArray(), Ascii(raw.Slice(24, 40)), Ascii(raw.Slice(4, 20)), Ascii(raw.Slice(64, 8)),
            vendor, Word(raw, 1), (uint)(raw[73] | raw[74] << 8 | raw[75] << 16),
            version is 0 or uint.MaxValue ? null : version, Word(raw, 39),
            capacitiesSupported ? BinaryPrimitives.ReadUInt128LittleEndian(raw.Slice(280, 16)) : null,
            capacitiesSupported ? BinaryPrimitives.ReadUInt128LittleEndian(raw.Slice(296, 16)) : null,
            Read32(raw, 516), (Word(raw, 260) & 4) != 0);
    }

    private static string? AtaText(ReadOnlySpan<byte> raw)
    {
        Span<byte> text = stackalloc byte[raw.Length];
        for (var i = 0; i < raw.Length; i += 2) { text[i] = raw[i + 1]; text[i + 1] = raw[i]; }
        return Ascii(text);
    }

    private static string? Ascii(ReadOnlySpan<byte> raw)
    {
        // Fixed-width protocol text is space padded. Only an all-NUL tail is accepted as termination.
        var end = raw.IndexOf((byte)0);
        if (end >= 0)
        {
            foreach (var value in raw[end..]) if (value != 0) return null;
            raw = raw[..end];
        }
        foreach (var value in raw) if (value < 32 || value > 126) return null;
        var text = Encoding.ASCII.GetString(raw).Trim();
        return text.Length == 0 ? null : text;
    }

    private static bool IsEmpty(ReadOnlySpan<byte> bytes)
    {
        var zero = true;
        var ones = true;
        foreach (var value in bytes) { zero &= value == 0; ones &= value == 255; }
        return zero || ones;
    }
    private static ushort Word(ReadOnlySpan<byte> bytes, int word) => BinaryPrimitives.ReadUInt16LittleEndian(bytes[(word * 2)..]);
    private static uint Read32(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
    private static void Write(Span<byte> bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes[offset..], value);
}

internal interface IWindowsStorageIdentifyQuery : IDisposable
{
    byte[]? Query(byte[] input, int outputLength);
}
