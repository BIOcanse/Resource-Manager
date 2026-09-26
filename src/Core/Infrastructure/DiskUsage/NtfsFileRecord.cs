using System.Buffers.Binary;

namespace ResourceManager.App.Infrastructure.DiskUsage;

/// <summary>从一条主文件表记录里读出来的、画方格图需要的那点信息。</summary>
internal readonly record struct NtfsFileRecord(
    bool InUse,
    bool IsDirectory,
    /// <summary>父目录的记录号。根目录的父是它自己。</summary>
    long ParentRecordNumber,
    string Name,
    long SizeBytes,
    long AllocatedBytes);

/// <summary>
/// 一条扩展记录：属性多到一条记录装不下时，NTFS 会另开记录接着放，
/// 并在扩展记录里写明基记录是谁。它没有自己的名字，大小要算到基记录头上。
/// </summary>
internal readonly record struct NtfsExtensionRecord(
    long BaseRecordNumber,
    long SizeBytes,
    long AllocatedBytes);

/// <summary>
/// 主文件表记录的解析。
///
/// 只取四样东西：在不在用、是不是目录、父目录是谁、叫什么名字多大。
/// 其余属性一律跳过 —— 这里不是要实现一个 NTFS 驱动。
/// </summary>
internal static class NtfsFileRecordParser
{
    private const uint AttributeFileName = 0x30;
    private const uint AttributeData = 0x80;
    private const uint AttributeEnd = 0xFFFFFFFF;
    private const ushort FlagInUse = 0x0001;
    private const ushort FlagDirectory = 0x0002;

    /// <summary>名字空间：2 是 DOS 短名，能避开就避开。</summary>
    private const byte NamespaceDos = 2;

    /// <summary>这条记录是什么。</summary>
    public enum RecordKind
    {
        /// <summary>没有 FILE 签名：从没用过的槽位，正常现象。</summary>
        Empty,
        /// <summary>解析失败：这条记录确实坏了。</summary>
        Unreadable,
        /// <summary>正常的文件或目录记录。</summary>
        File,
        /// <summary>扩展记录，大小要归到基记录头上。</summary>
        Extension
    }

    /// <summary>
    /// 就地修复并解析一条记录。缓冲区会被就地改写（修复序列要写回去），
    /// 所以调用方传进来的必须是自己的可写副本。
    /// </summary>
    public static RecordKind Classify(
        Span<byte> record,
        out NtfsFileRecord file,
        out NtfsExtensionRecord extension)
    {
        file = default;
        extension = default;
        if (record.Length < 48 || !record[..4].SequenceEqual("FILE"u8))
        {
            return RecordKind.Empty;
        }
        if (!ApplyFixups(record))
        {
            return RecordKind.Unreadable;
        }

        // 0x20 是基记录的文件引用号。非零就说明这是一条扩展记录。
        var baseReference = BinaryPrimitives.ReadUInt64LittleEndian(record[32..]);
        var baseRecord = (long)(baseReference & 0x0000_FFFF_FFFF_FFFFUL);
        var parsed = ParseAttributes(record, out var inUse, out var isDirectory,
            out var parent, out var name, out var size, out var allocated);
        if (baseRecord != 0)
        {
            extension = new NtfsExtensionRecord(baseRecord, size, allocated);
            return RecordKind.Extension;
        }
        if (!parsed)
        {
            return RecordKind.Unreadable;
        }
        file = new NtfsFileRecord(
            inUse,
            isDirectory,
            parent,
            name!,
            size,
            allocated > 0 ? allocated : size);
        return RecordKind.File;
    }

    private static bool ParseAttributes(
        Span<byte> record,
        out bool inUse,
        out bool isDirectory,
        out long parent,
        out string? bestName,
        out long size,
        out long allocated)
    {

        var flags = BinaryPrimitives.ReadUInt16LittleEndian(record[22..]);
        inUse = (flags & FlagInUse) != 0;
        isDirectory = (flags & FlagDirectory) != 0;
        parent = -1;
        bestName = null;
        size = 0;
        allocated = 0;
        var firstAttributeOffset = BinaryPrimitives.ReadUInt16LittleEndian(record[20..]);
        if (firstAttributeOffset >= record.Length)
        {
            return false;
        }

        var bestNamespace = byte.MaxValue;

        var offset = (int)firstAttributeOffset;
        var guard = 0;
        while (offset + 8 <= record.Length && guard++ < 512)
        {
            var type = BinaryPrimitives.ReadUInt32LittleEndian(record[offset..]);
            if (type == AttributeEnd)
            {
                break;
            }
            var length = BinaryPrimitives.ReadInt32LittleEndian(record[(offset + 4)..]);
            if (length <= 0 || offset + length > record.Length)
            {
                break;
            }

            var nonResident = record[offset + 8] != 0;
            var nameLength = record[offset + 9];

            if (type == AttributeFileName && !nonResident)
            {
                ReadFileNameAttribute(
                    record.Slice(offset, length),
                    ref parent,
                    ref bestName,
                    ref bestNamespace);
            }
            else if (type == AttributeData && nameLength == 0)
            {
                // 只认无名的 $DATA，也就是文件本体；备用数据流不计入。
                ReadDataAttribute(record.Slice(offset, length), nonResident, ref size, ref allocated);
            }

            offset += length;
        }

        return bestName is not null && parent >= 0;
    }

    private static void ReadFileNameAttribute(
        ReadOnlySpan<byte> attribute,
        ref long parent,
        ref string? bestName,
        ref byte bestNamespace)
    {
        var contentOffset = BinaryPrimitives.ReadUInt16LittleEndian(attribute[20..]);
        var contentLength = BinaryPrimitives.ReadInt32LittleEndian(attribute[16..]);
        if (contentOffset + contentLength > attribute.Length || contentLength < 66)
        {
            return;
        }

        var content = attribute.Slice(contentOffset, contentLength);
        // 前 8 字节是父目录的文件引用号：低 48 位是记录号，高 16 位是序列号。
        var parentReference = BinaryPrimitives.ReadUInt64LittleEndian(content);
        var nameLength = content[64];
        var nameNamespace = content[65];
        var nameBytes = nameLength * 2;
        if (66 + nameBytes > content.Length)
        {
            return;
        }

        // 一个文件可能有多个 $FILE_NAME（长名加 DOS 短名）。要长名，DOS 短名垫底。
        if (bestName is not null && NameRank(nameNamespace) >= NameRank(bestNamespace))
        {
            return;
        }

        parent = (long)(parentReference & 0x0000_FFFF_FFFF_FFFFUL);
        bestName = System.Text.Encoding.Unicode.GetString(content.Slice(66, nameBytes));
        bestNamespace = nameNamespace;
    }

    /// <summary>
    /// 只做修复并给出第一个属性的位置，供解数据运行表用。
    /// 解运行表需要原始属性字节，所以不能复用 <see cref="Classify"/> 的结果。
    /// </summary>
    public static bool TryParseHeaderForRuns(Span<byte> record, out int firstAttributeOffset)
    {
        firstAttributeOffset = 0;
        if (record.Length < 48 || !record[..4].SequenceEqual("FILE"u8) || !ApplyFixups(record))
        {
            return false;
        }
        var offset = BinaryPrimitives.ReadUInt16LittleEndian(record[20..]);
        if (offset >= record.Length)
        {
            return false;
        }
        firstAttributeOffset = offset;
        return true;
    }

    /// <summary>名字空间的优先级，越小越想要。DOS 短名永远垫底。</summary>
    private static int NameRank(byte nameNamespace)
        => nameNamespace == NamespaceDos ? int.MaxValue : nameNamespace;

    private static void ReadDataAttribute(
        ReadOnlySpan<byte> attribute,
        bool nonResident,
        ref long size,
        ref long allocated)
    {
        if (!nonResident)
        {
            // 小文件直接住在记录里，长度就是内容长度。
            var contentLength = BinaryPrimitives.ReadInt32LittleEndian(attribute[16..]);
            if (contentLength >= 0)
            {
                size = Math.Max(size, contentLength);
                allocated = Math.Max(allocated, contentLength);
            }
            return;
        }

        if (attribute.Length < 56)
        {
            return;
        }
        // 0x10 是这一段从第几簇开始。同一个 $DATA 被拆到多条记录时，
        // 只有第一段（起始簇为 0）的头部写着整个文件的大小，后面几段的字段是无效的。
        // 不看这个就会把一个大文件按分片数重复累加。
        if (BinaryPrimitives.ReadInt64LittleEndian(attribute[16..]) != 0)
        {
            return;
        }
        // 非常驻头部：0x28 是已分配大小，0x30 是真实大小。
        var allocatedSize = BinaryPrimitives.ReadInt64LittleEndian(attribute[40..]);
        var realSize = BinaryPrimitives.ReadInt64LittleEndian(attribute[48..]);
        if (realSize >= 0)
        {
            size = Math.Max(size, realSize);
        }
        if (allocatedSize >= 0)
        {
            allocated = Math.Max(allocated, allocatedSize);
        }
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
