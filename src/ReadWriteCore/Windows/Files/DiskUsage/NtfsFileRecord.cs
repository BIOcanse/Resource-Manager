using System.Buffers.Binary;

namespace ResourceManager.App.Infrastructure.DiskUsage;

/// <summary>A preferred NTFS file-name link. Parent sequence is the high 16 bits
/// of the original file reference; it is not an inferred directory identity.</summary>
public sealed record NtfsFileNameLink(long ParentRecordNumber, string Name, ushort ParentSequenceNumber = 0)
{
    public byte Namespace { get; init; }
}

/// <summary>Original six positional values, with passive exact name/size metadata.</summary>
public readonly record struct NtfsFileRecord(bool InUse, bool IsDirectory,
    long ParentRecordNumber, string Name, long SizeBytes, long AllocatedBytes)
{
    public IReadOnlyList<NtfsFileNameLink> Names { get; init; } = [];
    public ushort SequenceNumber { get; init; }
    public bool LogicalSizeKnown { get; init; } = true;
    public bool AllocationKnown { get; init; } = true;
    public bool HasPrimaryData { get; init; } = true;
    public bool IsReparsePoint { get; init; }
}

/// <summary>Original extension contribution values. A later VCN is not counted
/// again; extension/base sequence facts let the original merger refuse stale records.</summary>
public readonly record struct NtfsExtensionRecord(long BaseRecordNumber, long SizeBytes, long AllocatedBytes)
{
    public IReadOnlyList<NtfsFileNameLink> Names { get; init; } = [];
    public ushort SequenceNumber { get; init; }
    public ushort BaseSequenceNumber { get; init; }
    public bool InUse { get; init; }
    public bool IsDirectory { get; init; }
    public bool LogicalSizeKnown { get; init; } = true;
    public bool AllocationKnown { get; init; } = true;
    public bool HasPrimaryData { get; init; } = true;
    public bool IsReparsePoint { get; init; }
}

/// <summary>Bounded parsing of the original MFT record copy. No volume access,
/// tree, alternate scanner, current cache or traversal policy lives here.</summary>
public static class NtfsFileRecordParser
{
    private const uint AttributeFileName = 0x30;
    private const uint AttributeData = 0x80;
    private const uint AttributeReparsePoint = 0xC0;
    private const uint AttributeEnd = 0xFFFFFFFF;
    private const ushort FlagInUse = 0x0001;
    private const ushort FlagDirectory = 0x0002;
    private const ushort AttributeCompressed = 0x0001;
    private const ushort AttributeSparse = 0x8000;
    private const uint FileAttributeReparsePoint = 0x0400;
    private const byte NamespaceDos = 2;

    public enum RecordKind { Empty, Unreadable, File, Extension }

    /// <summary>Fixups rewrite the supplied writable record copy, as before.
    /// Malformed attributes are unreadable rather than a successful partial record.</summary>
    public static RecordKind Classify(Span<byte> record, out NtfsFileRecord file, out NtfsExtensionRecord extension)
    {
        file = default; extension = default;
        if (record.Length < 4 || !record[..4].SequenceEqual("FILE"u8)) return RecordKind.Empty;
        if (record.Length < 48 || !ApplyFixups(record)) return RecordKind.Unreadable;
        var baseReference = BinaryPrimitives.ReadUInt64LittleEndian(record[32..]);
        var baseRecord = (long)(baseReference & 0x0000_FFFF_FFFF_FFFFUL);
        var sequence = BinaryPrimitives.ReadUInt16LittleEndian(record[16..]);
        if (!ParseAttributes(record, out var inUse, out var isDirectory, out var names,
            out var size, out var allocated, out var logicalKnown, out var allocationKnown,
            out var hasInitialData, out var reparse)) return RecordKind.Unreadable;
        if (isDirectory) { size = 0; allocated = 0; logicalKnown = true; allocationKnown = true; }
        if (baseRecord != 0)
        {
            // No initial DATA is a known zero extension contribution, not a claim
            // about the whole file. Full file size remains the base/Vcn0 owner's value.
            extension = new(baseRecord, size, allocated)
            {
                Names = names, SequenceNumber = sequence,
                BaseSequenceNumber = (ushort)(baseReference >> 48), InUse = inUse, IsDirectory = isDirectory,
                LogicalSizeKnown = logicalKnown || !hasInitialData,
                AllocationKnown = allocationKnown || !hasInitialData, HasPrimaryData = hasInitialData,
                IsReparsePoint = reparse
            };
            return RecordKind.Extension;
        }
        // A valid base can carry its FILE_NAME in an extension. Preserve the
        // unnamed base for the original sequence-checked merge; do not invent a link.
        file = new(inUse, isDirectory, names.Count == 0 ? 0 : names[0].ParentRecordNumber,
            names.Count == 0 ? "" : names[0].Name, size, allocated)
        {
            Names = names, SequenceNumber = sequence, LogicalSizeKnown = logicalKnown,
            AllocationKnown = allocationKnown, HasPrimaryData = hasInitialData, IsReparsePoint = reparse
        };
        return RecordKind.File;
    }

    private static bool ParseAttributes(Span<byte> record, out bool inUse, out bool isDirectory,
        out IReadOnlyList<NtfsFileNameLink> names, out long size, out long allocated,
        out bool logicalKnown, out bool allocationKnown, out bool hasInitialData, out bool reparse)
    {
        var flags = BinaryPrimitives.ReadUInt16LittleEndian(record[22..]);
        inUse = (flags & FlagInUse) != 0; isDirectory = (flags & FlagDirectory) != 0;
        names = []; size = 0; allocated = 0; logicalKnown = false; allocationKnown = false;
        hasInitialData = false; reparse = false;
        var first = BinaryPrimitives.ReadUInt16LittleEndian(record[20..]);
        if (first < 48 || first > record.Length - 4) return false;
        var links = new List<(NtfsFileNameLink Link, byte Namespace)>();
        var offset = (int)first;
        var complete = false;
        var allocationMetadataComplete = true;
        var guard = 0;
        while (offset <= record.Length - 4 && guard++ < 512)
        {
            var type = BinaryPrimitives.ReadUInt32LittleEndian(record[offset..]);
            if (type == AttributeEnd) { complete = true; break; }
            if (offset > record.Length - 16) return false;
            var length = BinaryPrimitives.ReadInt32LittleEndian(record[(offset + 4)..]);
            if (length < 16 || length > record.Length - offset) return false;
            var attribute = record.Slice(offset, length);
            var form = attribute[8];
            if (form > 1) return false;
            var nonResident = form == 1;
            var headerSize = nonResident ? 64 : 24;
            if (length < headerSize) return false;
            var nameLength = attribute[9];
            if (nameLength > 0)
            {
                var nameOffset = BinaryPrimitives.ReadUInt16LittleEndian(attribute[10..]);
                if (nameOffset < headerSize || nameOffset > length - nameLength * 2) return false;
            }
            ReadOnlySpan<byte> content = default;
            if (!nonResident && !TryResidentContent(attribute, out content)) return false;
            if (type == AttributeFileName)
            {
                if (nonResident || !ReadFileName(content, links, ref reparse)) return false;
            }
            else if (type == AttributeData && nameLength == 0)
            {
                if (!ReadDataAttribute(attribute, nonResident, content, ref size, ref allocated,
                    ref hasInitialData, ref allocationMetadataComplete)) return false;
            }
            else if (type == AttributeReparsePoint) reparse = true;
            offset += length;
        }
        if (!complete) return false;
        // Keep every non-DOS link. A DOS link is only a fallback for a parent
        // lacking any non-DOS name, regardless of where attributes were ordered.
        var preferredParents = links.Where(item => item.Namespace != NamespaceDos)
            .Select(item => (item.Link.ParentRecordNumber, item.Link.ParentSequenceNumber)).ToHashSet();
        var seen = new HashSet<(long Parent, ushort Sequence, string Name)>();
        names = Array.AsReadOnly(links
            .Where(item => item.Namespace != NamespaceDos || !preferredParents.Contains((item.Link.ParentRecordNumber, item.Link.ParentSequenceNumber)))
            .OrderBy(item => NameRank(item.Namespace))
            .Where(item => seen.Add((item.Link.ParentRecordNumber, item.Link.ParentSequenceNumber, item.Link.Name)))
            .Select(item => item.Link).ToArray());
        logicalKnown = hasInitialData;
        allocationKnown = hasInitialData && allocationMetadataComplete;
        return true;
    }

    private static bool TryResidentContent(ReadOnlySpan<byte> attribute, out ReadOnlySpan<byte> content)
    {
        content = default;
        var length = BinaryPrimitives.ReadInt32LittleEndian(attribute[16..]);
        var offset = BinaryPrimitives.ReadUInt16LittleEndian(attribute[20..]);
        if (length < 0 || offset < 24 || length > attribute.Length - offset) return false;
        content = attribute.Slice(offset, length);
        return true;
    }

    private static bool ReadFileName(ReadOnlySpan<byte> content,
        List<(NtfsFileNameLink Link, byte Namespace)> links, ref bool reparse)
    {
        if (content.Length < 66) return false;
        var parentReference = BinaryPrimitives.ReadUInt64LittleEndian(content);
        var nameLength = content[64];
        if (nameLength * 2 > content.Length - 66) return false;
        reparse |= (BinaryPrimitives.ReadUInt32LittleEndian(content[56..]) & FileAttributeReparsePoint) != 0;
        links.Add((new((long)(parentReference & 0x0000_FFFF_FFFF_FFFFUL),
            System.Text.Encoding.Unicode.GetString(content.Slice(66, nameLength * 2)),
            (ushort)(parentReference >> 48)) { Namespace = content[65] }, content[65]));
        return true;
    }

    private static int NameRank(byte nameNamespace) => nameNamespace == NamespaceDos ? int.MaxValue : nameNamespace;

    /// <summary>Original fixup/header entry used by the volume reader's raw DATA run parser.</summary>
    public static bool TryParseHeaderForRuns(Span<byte> record, out int firstAttributeOffset)
    {
        firstAttributeOffset = 0;
        if (record.Length < 48 || !record[..4].SequenceEqual("FILE"u8) || !ApplyFixups(record)) return false;
        var offset = BinaryPrimitives.ReadUInt16LittleEndian(record[20..]);
        if (offset < 48 || offset > record.Length - 4) return false;
        firstAttributeOffset = offset;
        return true;
    }

    private static bool ReadDataAttribute(ReadOnlySpan<byte> attribute, bool nonResident, ReadOnlySpan<byte> content,
        ref long size, ref long allocated, ref bool hasInitialData, ref bool allocationMetadataComplete)
    {
        if (!nonResident)
        {
            // Resident DATA has logical content bytes and no separate data-cluster allocation.
            size = Math.Max(size, content.Length); hasInitialData = true;
            return true;
        }
        var lowestVcn = BinaryPrimitives.ReadInt64LittleEndian(attribute[16..]);
        if (lowestVcn < 0) return false;
        var mappingOffset = BinaryPrimitives.ReadUInt16LittleEndian(attribute[32..]);
        if (mappingOffset < 64 || mappingOffset > attribute.Length) return false;
        if (lowestVcn != 0) return true; // Original whole-file-size ownership: first extent only.
        var allocatedLength = BinaryPrimitives.ReadInt64LittleEndian(attribute[40..]);
        var logicalLength = BinaryPrimitives.ReadInt64LittleEndian(attribute[48..]);
        if (allocatedLength < 0 || logicalLength < 0) return false;
        size = Math.Max(size, logicalLength); hasInitialData = true;
        var flags = BinaryPrimitives.ReadUInt16LittleEndian(attribute[12..]);
        if ((flags & (AttributeCompressed | AttributeSparse)) != 0)
        {
            // TotalAllocated is independent of mapping pairs only with its full
            // header slot. Never substitute logical size or ordinary AllocatedLength.
            if (attribute.Length < 72 || mappingOffset < 72) { allocationMetadataComplete = false; return true; }
            var totalAllocated = BinaryPrimitives.ReadInt64LittleEndian(attribute[64..]);
            if (totalAllocated < 0) return false;
            allocated = Math.Max(allocated, totalAllocated);
        }
        else allocated = Math.Max(allocated, allocatedLength);
        return true;
    }


    /// <summary>
    /// 应用更新序列（fixup）。
    ///
    /// NTFS 把每个扇区最后两字节换成了一个校验值，真正的两字节存在记录头的数组里。
    /// 不还原的话每 512 字节就有两个字节是错的，解析出来全是垃圾。
    /// </summary>
    private static bool ApplyFixups(Span<byte> record)
    {
        var arrayOffset = BinaryPrimitives.ReadUInt16LittleEndian(record[4..]);
        var arrayCount = BinaryPrimitives.ReadUInt16LittleEndian(record[6..]);
        // 数组第一项是校验值本身，之后每个扇区一项，所以至少要有两项。
        if (arrayCount < 2 || arrayOffset + arrayCount * 2 > record.Length)
        {
            return false;
        }

        var signature = record.Slice(arrayOffset, 2);
        var sectorSize = record.Length / (arrayCount - 1);
        if (sectorSize < 4)
        {
            return false;
        }

        for (var sector = 1; sector < arrayCount; sector++)
        {
            var tail = sector * sectorSize - 2;
            if (tail + 2 > record.Length)
            {
                return false;
            }
            // 扇区尾部现在应该是校验值；不是就说明这条记录读坏了。
            if (!record.Slice(tail, 2).SequenceEqual(signature))
            {
                return false;
            }
            // 把真正的两字节写回扇区尾部。
            record.Slice(arrayOffset + sector * 2, 2).CopyTo(record.Slice(tail, 2));
        }
        return true;
    }
}
