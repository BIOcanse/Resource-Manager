using System.Buffers.Binary;
using Microsoft.Win32.SafeHandles;

namespace ResourceManager.App.Infrastructure.DiskUsage;

/// <summary>
/// 按字节偏移读一个 NTFS 卷。
///
/// 打开 <c>\\.\X:</c> 需要管理员权限；拿不到就说拿不到，不退而求其次。
/// 读取必须按扇区对齐，所以这里统一做对齐和裁剪，调用方给什么偏移都行。
/// </summary>
internal sealed class NtfsVolumeReader : IDisposable
{
    private readonly SafeFileHandle handle;
    private readonly FileStream stream;

    private NtfsVolumeReader(SafeFileHandle handle, NtfsVolumeLayout layout)
    {
        this.handle = handle;
        stream = new FileStream(handle, FileAccess.Read);
        Layout = layout;
    }

    public NtfsVolumeLayout Layout { get; }

    /// <summary>
    /// 打开一个卷并读出它的布局。不是 NTFS、没权限、或者引导扇区看不懂时返回 null。
    /// </summary>
    public static NtfsVolumeReader? TryOpen(string volumeId)
    {
        // 形如 \\.\C: —— 结尾不能带反斜杠，否则打开的是根目录而不是卷。
        var devicePath = $@"\\.\{volumeId.TrimEnd('\\')}";
        SafeFileHandle? handle = null;
        try
        {
            handle = File.OpenHandle(
                devicePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                FileOptions.None);
            var boot = new byte[512];
            if (RandomAccess.Read(handle, boot, 0) != boot.Length)
            {
                handle.Dispose();
                return null;
            }
            var layout = NtfsVolumeLayout.TryParse(boot);
            if (layout is null)
            {
                handle.Dispose();
                return null;
            }
            return new NtfsVolumeReader(handle, layout.Value);
        }
        catch (Exception error) when (error is UnauthorizedAccessException
            or IOException
            or ArgumentException
            or NotSupportedException)
        {
            handle?.Dispose();
            return null;
        }
    }

    /// <summary>
    /// 读一段字节。偏移和长度会被扩到扇区边界再读，返回请求的那一段。
    /// 读不满就返回读到的部分。
    /// </summary>
    public int Read(long offset, Span<byte> destination)
    {
        var sector = Layout.BytesPerSector;
        var alignedOffset = offset / sector * sector;
        var skew = (int)(offset - alignedOffset);
        var needed = skew + destination.Length;
        var alignedLength = (needed + sector - 1) / sector * sector;

        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(alignedLength);
        try
        {
            var read = RandomAccess.Read(handle, buffer.AsSpan(0, alignedLength), alignedOffset);
            if (read <= skew)
            {
                return 0;
            }
            var available = Math.Min(destination.Length, read - skew);
            buffer.AsSpan(skew, available).CopyTo(destination);
            return available;
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// 主文件表自己占哪些簇。它存在 0 号记录的 $DATA 属性的数据运行表里，
    /// 主文件表通常是分片的，所以是一组区段而不是一段。
    /// </summary>
    public IReadOnlyList<NtfsDataRun> ReadMasterFileTableRuns()
    {
        var record = new byte[Layout.BytesPerFileRecord];
        if (Read(Layout.MasterFileTableOffset, record) != record.Length)
        {
            return [];
        }
        return ParseDataRuns(record);
    }

    /// <summary>
    /// 从 0 号记录里把无名 $DATA 的数据运行表解出来。
    /// 运行表是一串「长度字节数 + 起始簇字节数」打头的变长记录，簇号是相对上一段的增量。
    /// </summary>
    private static IReadOnlyList<NtfsDataRun> ParseDataRuns(Span<byte> record)
    {
        if (!NtfsFileRecordParser.TryParseHeaderForRuns(record, out var attributeOffset))
        {
            return [];
        }

        var runs = new List<NtfsDataRun>();
        var offset = attributeOffset;
        var guard = 0;
        while (offset + 8 <= record.Length && guard++ < 512)
        {
            var type = BinaryPrimitives.ReadUInt32LittleEndian(record[offset..]);
            if (type == 0xFFFFFFFF)
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
            if (type != 0x80 || nameLength != 0 || !nonResident)
            {
                offset += length;
                continue;
            }

            var runOffset = BinaryPrimitives.ReadUInt16LittleEndian(record[(offset + 32)..]);
            var cursor = offset + runOffset;
            long cluster = 0;
            var runGuard = 0;
            while (cursor < offset + length && runGuard++ < 4096)
            {
                var header = record[cursor];
                if (header == 0)
                {
                    break;
                }
                var lengthBytes = header & 0x0F;
                var offsetBytes = (header >> 4) & 0x0F;
                cursor++;
                if (lengthBytes == 0 || cursor + lengthBytes + offsetBytes > offset + length)
                {
                    break;
                }

                var runLength = ReadVariableLength(record.Slice(cursor, lengthBytes), signed: false);
                cursor += lengthBytes;
                if (offsetBytes == 0)
                {
                    // 稀疏段：没有实际簇，跳过但要保持增量基准。
                    continue;
                }
                cluster += ReadVariableLength(record.Slice(cursor, offsetBytes), signed: true);
                cursor += offsetBytes;
                if (runLength > 0 && cluster > 0)
                {
                    runs.Add(new NtfsDataRun(cluster, runLength));
                }
            }
            break;
        }
        return runs;
    }

    /// <summary>小端变长整数。带符号的那一路用于簇号增量，可能是负的。</summary>
    private static long ReadVariableLength(ReadOnlySpan<byte> bytes, bool signed)
    {
        long value = 0;
        for (var index = bytes.Length - 1; index >= 0; index--)
        {
            value = (value << 8) | bytes[index];
        }
        if (signed && bytes.Length > 0 && (bytes[^1] & 0x80) != 0)
        {
            value -= 1L << (bytes.Length * 8);
        }
        return value;
    }

    public void Dispose()
    {
        stream.Dispose();
        handle.Dispose();
    }
}
