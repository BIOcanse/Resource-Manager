using System.Buffers.Binary;
using System.Text;
using ResourceManager.ReadWrite.Windows.Devices.Models;

namespace ResourceManager.ReadWrite.Windows.Devices;

internal static class WindowsDiskLayoutInformationDecoder
{
    internal const int HeaderSize = 48;
    internal const int PartitionSize = 144;
    internal const int MaximumPartitions = 4096;
    internal const int MaximumBytes = HeaderSize + MaximumPartitions * PartitionSize;
    private static readonly UnicodeEncoding StrictUnicode = new(false, false, true);

    internal static uint? DiskNumber(ReadOnlySpan<byte> bytes)
        => bytes.Length >= 12 && U32(bytes) == 7 && U32(bytes[4..]) != uint.MaxValue
            && U32(bytes[8..]) is 0 or uint.MaxValue ? U32(bytes[4..]) : null;

    internal static DeviceTopologyDiskLayout? Layout(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < HeaderSize) return null;
        var style = (DiskPartitionStyle)U32(bytes); var count = U32(bytes[4..]);
        if (style is not (DiskPartitionStyle.Mbr or DiskPartitionStyle.Gpt or DiskPartitionStyle.Raw)
            || count > MaximumPartitions || bytes.Length < HeaderSize + count * PartitionSize) return null;
        if (style == DiskPartitionStyle.Raw) return count == 0 ? new(style, null, null, []) : null;
        DiskLayoutMbr? mbr = style == DiskPartitionStyle.Mbr ? new(U32(bytes[8..]), U32(bytes[12..])) : null;
        DiskLayoutGpt? gpt = null;
        if (style == DiskPartitionStyle.Gpt)
        {
            if (!Range(bytes[24..], bytes[32..], allowEmpty: true, out var start, out var length)) return null;
            gpt = new(new Guid(bytes.Slice(8, 16)), start, length, U32(bytes[40..]));
        }
        var partitions = new List<DiskLayoutPartition>(); var numbers = new HashSet<uint>();
        for (var index = 0; index < count; index++)
        {
            var entry = bytes.Slice(HeaderSize + index * PartitionSize, PartitionSize);
            if ((DiskPartitionStyle)U32(entry) != style) return null;
            if (style == DiskPartitionStyle.Mbr && entry[32] == 0) continue; // Unused table slot, not a partition.
            var number = U32(entry[24..]);
            if (number == 0 || !numbers.Add(number) || entry[28] > 1 || entry[29] > 1
                || !Range(entry[8..], entry[16..], false, out var start, out var length)) return null;
            DiskPartitionMbr? partitionMbr = null; DiskPartitionGpt? partitionGpt = null;
            if (style == DiskPartitionStyle.Mbr)
            {
                if (entry[33] > 1 || entry[34] > 1) return null;
                partitionMbr = new(entry[32], entry[33] != 0, entry[34] != 0, U32(entry[36..]), new Guid(entry.Slice(40, 16)));
            }
            else
            {
                var name = entry.Slice(72, 72); var end = name.Length;
                for (var offset = 0; offset < name.Length; offset += 2)
                    if (name[offset] == 0 && name[offset + 1] == 0) { end = offset; break; }
                string decoded;
                try { decoded = StrictUnicode.GetString(name[..end]); }
                catch (DecoderFallbackException) { return null; }
                partitionGpt = new(new Guid(entry.Slice(32, 16)), new Guid(entry.Slice(48, 16)), U64(entry[64..]), decoded);
            }
            partitions.Add(new(number, start, length, entry[28] != 0, entry[29] != 0, partitionMbr, partitionGpt));
        }
        return new(style, mbr, gpt, partitions.OrderBy(static partition => partition.Number).ToArray());
    }

    private static bool Range(ReadOnlySpan<byte> startBytes, ReadOnlySpan<byte> lengthBytes, bool allowEmpty, out ulong start, out ulong length)
    {
        var rawStart = BinaryPrimitives.ReadInt64LittleEndian(startBytes); var rawLength = BinaryPrimitives.ReadInt64LittleEndian(lengthBytes);
        start = (ulong)rawStart; length = (ulong)rawLength;
        return rawStart >= 0 && rawLength >= 0 && (allowEmpty || rawLength > 0) && start + length <= long.MaxValue;
    }
    private static uint U32(ReadOnlySpan<byte> bytes) => BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    private static ulong U64(ReadOnlySpan<byte> bytes) => BinaryPrimitives.ReadUInt64LittleEndian(bytes);
}
