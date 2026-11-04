using ResourceManager.ReadWrite.Information;
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using ResourceManager.ReadWrite.Windows.Devices.Models;
using ResourceManager.ReadWrite.Windows.Devices;

namespace ResourceManager.ReadWrite.Windows.Devices;

/// <summary>One passive RSMB capture supplies Type 0/3/4/9 declarations and existing Type 17 modules.</summary>
public static class SmbiosFirmwareInformationReader
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private const int MaximumStructures = 65_536;

    public static SmbiosFirmwareCapture? Read()
        => Read(() => SmbiosMemoryReader.ReadRawSmbiosData(SmbiosMemoryReader.RawSmbiosProvider));

    public static DeviceInformationSnapshot ReadFields()
    {
        var capture = Read();
        return capture is null ? DeviceInformationSnapshot.Empty
            : WindowsInformationFieldProjection.Firmware(capture, DateTimeOffset.UtcNow);
    }

    internal static SmbiosFirmwareCapture? Read(Func<byte[]?> read)
    {
        var raw = read();
        return raw is null ? null : Parse(raw);
    }

    public static SmbiosFirmwareCapture? Parse(byte[] raw)
    {
        if (raw.Length < 8 || raw.Length > 4 * 1024 * 1024 || raw[1] < 2) return null;
        var declaredLength = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(4, 4));
        if (declaredLength > raw.Length - 8) return null;
        var end = 8 + checked((int)declaredLength);
        var offset = 8;
        var count = 0;
        var biosCount = 0;
        string? releaseDate = null;
        var slots = new List<FirmwareSystemSlot>();
        var chassisTypes = new List<byte>();
        var incompleteChassis = false;
        var processorRecords = new List<(ushort Handle, FirmwareProcessorSocket? Socket)>();
        while (offset < end)
        {
            if (++count > MaximumStructures || end - offset < 4) return null;
            var length = raw[offset + 1];
            if (length < 4 || length > end - offset) return null;
            var stringsStart = offset + length;
            var cursor = stringsStart;
            while (cursor + 1 < end && (raw[cursor] != 0 || raw[cursor + 1] != 0)) cursor++;
            if (cursor + 1 >= end) return null;
            var next = cursor + 2;
            var formatted = raw.AsSpan(offset, length);
            var strings = ReadStrings(raw, stringsStart, cursor, AtLeast(raw, 3, 5));
            if (formatted[0] == 0)
            {
                biosCount++;
                var minimum = AtLeast(raw, 3, 1) ? 0x1a : AtLeast(raw, 2, 4) ? 0x18
                    : AtLeast(raw, 2, 3) ? 0x14 : AtLeast(raw, 2, 1) ? 0x13 : 0x12;
                if (length >= minimum) releaseDate = ParseReleaseDate(StringAt(strings, formatted[8]), raw[1], raw[2]);
            }
            else if (formatted[0] == 3)
            {
                if (length < 9) incompleteChassis = true;
                else chassisTypes.Add((byte)(formatted[5] & 0x7f));
            }
            else if (formatted[0] == 4)
            {
                var handle = BinaryPrimitives.ReadUInt16LittleEndian(formatted.Slice(2, 2));
                if (handle < 0xfffe)
                {
                    var designation = length >= ProcessorMinimumLength(raw) ? StringAt(strings, formatted[4]) : null;
                    // Non-CPU and short Type4 records still participate in duplicate-handle rejection.
                    var socket = length > 5 && formatted[5] == 3
                        ? new FirmwareProcessorSocket($"smbios-cpu-socket|{handle:x4}", designation) : null;
                    processorRecords.Add((handle, socket));
                }
            }
            else if (formatted[0] == 9)
            {
                var handle = BinaryPrimitives.ReadUInt16LittleEndian(formatted.Slice(2, 2));
                if (handle < 0xfffe) slots.Add(ParseSlot(handle, formatted, strings, raw));
            }
            offset = next;
            if (formatted[0] == 127) break;
        }
        var uniqueSlots = slots.GroupBy(static slot => slot.DeviceIdentity, StringComparer.Ordinal)
            .Where(static group => group.Count() == 1).Select(static group => group.Single())
            .OrderBy(static slot => slot.DeviceIdentity, StringComparer.Ordinal).ToArray();
        var sockets = processorRecords.GroupBy(static record => record.Handle)
            .Where(static group => group.Count() == 1 && group.Single().Socket is not null)
            .Select(static group => group.Single().Socket!)
            .OrderBy(static socket => socket.DeviceIdentity, StringComparer.Ordinal).ToArray();
        return new(SmbiosMemoryReader.ParseModules(raw), new(biosCount == 1 ? releaseDate : null,
            Array.AsReadOnly(uniqueSlots)) { ProcessorSockets = Array.AsReadOnly(sockets) })
        { ChassisTypes = incompleteChassis || chassisTypes.Count == 0 ? null : chassisTypes.AsReadOnly() };
    }

    private static int ProcessorMinimumLength(byte[] raw)
        => AtLeast(raw, 3, 8) ? 0x33 : AtLeast(raw, 3, 6) ? 0x32 : AtLeast(raw, 3, 0) ? 0x30
            : AtLeast(raw, 2, 6) ? 0x2a : AtLeast(raw, 2, 5) ? 0x28 : AtLeast(raw, 2, 3) ? 0x23
            : AtLeast(raw, 2, 1) ? 0x20 : 0x1a;

    internal static string? ParseReleaseDate(string? value, byte major, byte minor)
    {
        if (value is null) return null;
        if (value.Length == 8 && major == 2 && minor < 3)
            value = value[..6] + "19" + value[6..];
        return DateOnly.TryParseExact(value, "MM/dd/yyyy", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var date) ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null;
    }

    private static FirmwareSystemSlot ParseSlot(ushort handle, ReadOnlySpan<byte> data,
        IReadOnlyList<string?> strings, byte[] raw)
    {
        var identity = "smbios-slot|" + handle.ToString("X4", CultureInfo.InvariantCulture);
        if (data.Length < 0x0c)
            return new(identity, null, null, null, null, null, null, null, null, null, null, null, null);
        byte? type = IsDefinedSlotType(data[5]) ? data[5] : null;
        byte? width = data[6] is 1 or >= 3 and <= 0x0e ? data[6] : null;
        byte? usage = data[7] is 1 or >= 3 and <= 5 ? data[7] : null;
        byte? length = data[8] is 1 or >= 3 and <= 6 ? data[8] : null;
        var id = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(9, 2));
        ushort? slotId = type is { } code && IsSlotIdDefined(code) ? id : null;
        if (type is 4 or 5 && id is < 1 or > 15) slotId = null;
        byte? characteristics1 = (data[0x0b] & 1) == 0 ? data[0x0b] : null;
        byte? characteristics2 = AtLeast(raw, 2, 1) && data.Length >= 0x0d ? data[0x0c] : null;
        var hasPciAddress = type is { } pciType && IsPciSlot(pciType)
            && AtLeast(raw, 2, 6) && data.Length >= 0x11;
        return new(identity, StringAt(strings, data[4]), type, width, usage, length, slotId,
            characteristics1, characteristics2,
            hasPciAddress ? BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(0x0d, 2)) : null,
            hasPciAddress ? data[0x0f] : null,
            hasPciAddress ? (byte)(data[0x10] >> 3) : null,
            hasPciAddress ? (byte)(data[0x10] & 7) : null);
    }

    // DSP0134 3.10.0 Tables 45/49: reserved gaps and Unknown are not usable definitions.
    private static bool IsDefinedSlotType(byte type)
        => type is 1 or >= 3 and <= 0x28 or 0x30 or >= 0xa0 and <= 0xb6 or >= 0xb8 and <= 0xc6;

    private static bool IsPciSlot(byte type)
        => type is 6 or >= 0x0e and <= 0x17 or >= 0x1f and <= 0x28
            or >= 0xa5 and <= 0xb6 or >= 0xb8 and <= 0xc6;

    private static bool IsSlotIdDefined(byte type) => type is 4 or 5 or 7 || IsPciSlot(type);

    private static bool AtLeast(byte[] raw, byte major, byte minor)
        => raw[1] > major || raw[1] == major && raw[2] >= minor;

    private static string? StringAt(IReadOnlyList<string?> strings, byte index)
        => index > 0 && index <= strings.Count ? strings[index - 1] : null;

    private static IReadOnlyList<string?> ReadStrings(byte[] raw, int start, int terminator, bool utf8)
    {
        var strings = new List<string?>();
        var cursor = start;
        while (cursor < terminator && strings.Count < byte.MaxValue)
        {
            var next = cursor;
            while (next < terminator && raw[next] != 0) next++;
            var bytes = raw.AsSpan(cursor, next - cursor);
            string? text = null;
            if (utf8)
            {
                try { text = StrictUtf8.GetString(bytes); }
                catch (DecoderFallbackException) { }
                if (text is not null && text.Any(char.IsControl)) text = null;
                else text = text?.Trim();
            }
            else
            {
                var ascii = true;
                foreach (var value in bytes) if (value is < 0x20 or > 0x7e) { ascii = false; break; }
                if (ascii) text = Encoding.ASCII.GetString(bytes).Trim();
            }
            strings.Add(string.IsNullOrWhiteSpace(text) ? null : text);
            cursor = next + 1;
        }
        return strings;
    }
}
