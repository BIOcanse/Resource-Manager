using System.Buffers.Binary;
using System.Text;
using ResourceManager.App.Domain.DiskUsage;

namespace ResourceManager.App.Endpoints;

/// <summary>
/// 把一份方格布局写成二进制。
///
/// 为什么不用 JSON：一次布局是十来个并列数组，每个几万到十万项。
/// 实测 4K 那一档的 JSON 要 10 MB、十一秒，瓶颈全在序列化、传输和 JSON.parse 上，
/// 而方格数据本身只是一堆定长数值 —— 正是二进制最合适的形状。
/// 前端拿到之后直接在同一块 ArrayBuffer 上开定型数组视图，不用逐项转换。
///
/// 布局（全部小端）：
///
/// <code>
/// 偏移 0   固定 64 字节头部
///   0  "RMDU"        4 字节魔数
///   4  version       u32
///   8  tileCount     u32
///  12  rootNodeId    i32
///  16  omittedCount  u32
///  20  （填充）       u32
///  24  rootSizeBytes f64
///  32  view          f32 × 4   minX, minY, maxX, maxY
///  48  view          f32 × 3   pixelWidth, pixelHeight, scale
///  60  rootPathBytes u32
///
/// 偏移 64  各列，按对齐要求从宽到窄排：
///   sizes          f64 × n
///   nodeIds        i32 × n
///   parentIds      i32 × n
///   depths         i32 × n
///   fileCounts     i32 × n
///   x, y, w, h     f32 × n（各一列）
///   directoryFlags u8  × n
///   （补齐到 4 的倍数）
///   nameByteLengths u32 × n
///   names           UTF-8 连续字节
///   rootPath        UTF-8 字节
/// </code>
///
/// 头部固定 64 字节且各列从宽到窄排，是为了让每一列的起始偏移都满足自己的对齐要求，
/// 前端才能零拷贝地开视图。变长的名字全部放在最后，不会破坏前面的对齐。
/// </summary>
public static class DiskUsageLayoutBinaryWriter
{
    /// <summary>魔数 "RMDU"，前端用它确认这确实是一份布局。</summary>
    public const uint Magic = 0x5544_4D52;

    /// <summary>线上格式版本。字段顺序或含义一变就要加。</summary>
    public const uint Version = 1;

    public const int HeaderBytes = 64;

    /// <summary>
    /// 还没扫过时的空布局：只有头部，方格数 0，根节点号 -1。
    ///
    /// 不用 204 也不混回 JSON —— 这个端点只有一种内容类型，
    /// 消费端照着同一套头部读就知道"没有结果"，不用为空态多写一条分支。
    /// </summary>
    public static byte[] WriteEmpty()
    {
        var buffer = new byte[HeaderBytes];
        var span = buffer.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span, Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], Version);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], 0u);
        BinaryPrimitives.WriteInt32LittleEndian(span[12..], -1);
        return buffer;
    }

    public static byte[] Write(
        DiskUsageTree tree,
        DiskUsageLayout layout,
        DiskUsageViewWindow view,
        string rootPath,
        long rootSizeBytes)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(rootPath);

        var tiles = layout.Tiles;
        var count = tiles.Count;

        // 名字先编码出来，长度要写进头部后面的列里。
        var nameBytes = new byte[count][];
        var namesTotal = 0;
        for (var index = 0; index < count; index++)
        {
            var name = tree.NameOf(tiles[index].NodeId);
            nameBytes[index] = Encoding.UTF8.GetBytes(new string(name));
            namesTotal += nameBytes[index].Length;
        }
        var rootPathBytes = Encoding.UTF8.GetBytes(rootPath);

        var columnsBytes = (8 * count)          // sizes
            + (4 * count * 4)                   // nodeIds, parentIds, depths, fileCounts
            + (4 * count * 4)                   // x, y, width, height
            + count;                            // directoryFlags
        var padding = (4 - (columnsBytes % 4)) % 4;
        var total = HeaderBytes
            + columnsBytes
            + padding
            + (4 * count)                       // nameByteLengths
            + namesTotal
            + rootPathBytes.Length;

        var buffer = new byte[total];
        var span = buffer.AsSpan();

        BinaryPrimitives.WriteUInt32LittleEndian(span, Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], Version);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], (uint)count);
        BinaryPrimitives.WriteInt32LittleEndian(span[12..], layout.RootNodeId);
        BinaryPrimitives.WriteUInt32LittleEndian(span[16..], (uint)layout.OmittedCount);
        BinaryPrimitives.WriteDoubleLittleEndian(span[24..], rootSizeBytes);
        BinaryPrimitives.WriteSingleLittleEndian(span[32..], view.MinX);
        BinaryPrimitives.WriteSingleLittleEndian(span[36..], view.MinY);
        BinaryPrimitives.WriteSingleLittleEndian(span[40..], view.MaxX);
        BinaryPrimitives.WriteSingleLittleEndian(span[44..], view.MaxY);
        BinaryPrimitives.WriteSingleLittleEndian(span[48..], view.PixelWidth);
        BinaryPrimitives.WriteSingleLittleEndian(span[52..], view.PixelHeight);
        BinaryPrimitives.WriteSingleLittleEndian(span[56..], view.Scale);
        BinaryPrimitives.WriteUInt32LittleEndian(span[60..], (uint)rootPathBytes.Length);

        var offset = HeaderBytes;
        for (var index = 0; index < count; index++)
        {
            BinaryPrimitives.WriteDoubleLittleEndian(
                span[(offset + (index * 8))..],
                tiles[index].SizeBytes);
        }
        offset += 8 * count;

        offset = WriteInt32Column(span, offset, count, index => tiles[index].NodeId);
        offset = WriteInt32Column(span, offset, count, index => tiles[index].ParentNodeId);
        offset = WriteInt32Column(span, offset, count, index => tiles[index].Depth);
        offset = WriteInt32Column(
            span, offset, count, index => tree.FileCountOf(tiles[index].NodeId));
        offset = WriteSingleColumn(span, offset, count, index => tiles[index].X);
        offset = WriteSingleColumn(span, offset, count, index => tiles[index].Y);
        offset = WriteSingleColumn(span, offset, count, index => tiles[index].Width);
        offset = WriteSingleColumn(span, offset, count, index => tiles[index].Height);

        for (var index = 0; index < count; index++)
        {
            span[offset + index] = tiles[index].IsDirectory ? (byte)1 : (byte)0;
        }
        offset += count + padding;

        for (var index = 0; index < count; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                span[(offset + (index * 4))..],
                (uint)nameBytes[index].Length);
        }
        offset += 4 * count;

        for (var index = 0; index < count; index++)
        {
            nameBytes[index].CopyTo(span[offset..]);
            offset += nameBytes[index].Length;
        }
        rootPathBytes.CopyTo(span[offset..]);

        return buffer;
    }

    private static int WriteInt32Column(
        Span<byte> span,
        int offset,
        int count,
        Func<int, int> read)
    {
        for (var index = 0; index < count; index++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(span[(offset + (index * 4))..], read(index));
        }
        return offset + (4 * count);
    }

    private static int WriteSingleColumn(
        Span<byte> span,
        int offset,
        int count,
        Func<int, float> read)
    {
        for (var index = 0; index < count; index++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(span[(offset + (index * 4))..], read(index));
        }
        return offset + (4 * count);
    }
}
