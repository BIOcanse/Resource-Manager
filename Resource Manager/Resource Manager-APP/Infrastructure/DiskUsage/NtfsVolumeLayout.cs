using System.Buffers.Binary;

namespace ResourceManager.App.Infrastructure.DiskUsage;

/// <summary>
/// NTFS 引导扇区里跟遍历主文件表有关的那几个字段。
///
/// 只读这几个：扇区大小、每簇扇区数、主文件表起始簇、每条文件记录多大。
/// 有了它们就能算出主文件表在卷上的字节位置和每条记录的边界。
/// </summary>
internal readonly record struct NtfsVolumeLayout(
    int BytesPerSector,
    int SectorsPerCluster,
    long MasterFileTableCluster,
    int BytesPerFileRecord)
{
    public int BytesPerCluster => BytesPerSector * SectorsPerCluster;

    public long MasterFileTableOffset => MasterFileTableCluster * BytesPerCluster;

    /// <summary>
    /// 从引导扇区解析。不是 NTFS、或者字段不合理时返回 null —— 不猜也不硬来。
    /// </summary>
    public static NtfsVolumeLayout? TryParse(ReadOnlySpan<byte> bootSector)
    {
        if (bootSector.Length < 512)
        {
            return null;
        }
        // OEM 标识固定是 "NTFS    "。
        if (!bootSector.Slice(3, 8).SequenceEqual("NTFS    "u8))
        {
            return null;
        }

        var bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(bootSector[11..]);
        var sectorsPerCluster = bootSector[13];
        var mftCluster = BinaryPrimitives.ReadInt64LittleEndian(bootSector[48..]);
        // 0x40 处是每条文件记录的簇数；为负时表示 2 的幂字节数（常见是 -10 → 1024 字节）。
        var clustersPerRecord = (sbyte)bootSector[64];

        if (bytesPerSector is < 256 or > 8192
            || sectorsPerCluster == 0
            || mftCluster <= 0)
        {
            return null;
        }

        var bytesPerCluster = bytesPerSector * sectorsPerCluster;
        var bytesPerRecord = clustersPerRecord >= 0
            ? clustersPerRecord * bytesPerCluster
            : 1 << -clustersPerRecord;
        if (bytesPerRecord is < 256 or > 65536)
        {
            return null;
        }

        return new NtfsVolumeLayout(
            bytesPerSector,
            sectorsPerCluster,
            mftCluster,
            bytesPerRecord);
    }
}

/// <summary>主文件表自己占用的一段连续簇。主文件表通常是分片的，所以是一组。</summary>
internal readonly record struct NtfsDataRun(long StartCluster, long ClusterCount);
