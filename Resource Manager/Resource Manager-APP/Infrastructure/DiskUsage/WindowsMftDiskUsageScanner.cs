using System.Diagnostics;
using ResourceManager.App.Application.DiskUsage;
using ResourceManager.App.Domain.DiskUsage;

namespace ResourceManager.App.Infrastructure.DiskUsage;

/// <summary>
/// 快速扫描：直接读 NTFS 的主文件表。
///
/// 这是整盘几秒扫完的原因 —— 不逐个打开文件，而是把主文件表整块读进来，
/// 每条记录里就写着名字、父目录和大小。代价是只支持 NTFS 且要管理员权限，
/// 两者缺一就扫不了；这时不偷偷改用遍历，而是明说这个卷快速扫描够不着。
///
/// 做法分两趟：
/// 1. 顺序读主文件表，把每条在用的记录收成「父记录号 + 名字 + 大小」。
/// 2. 从根目录（5 号记录）出发，用显式栈做深度优先，按先序喂给树构造器。
///    这样父一定先于子，汇总只要反向扫一遍。
/// </summary>
internal sealed class WindowsMftDiskUsageScanner(
    IDiskUsageVolumeCatalog volumes,
    IDiskUsageScanner fallbackScanner) : IDiskUsageScanner
{
    /// <summary>根目录固定是 5 号记录。</summary>
    private const long RootRecordNumber = 5;

    /// <summary>一次从卷上读这么多条记录，减少系统调用。</summary>
    private const int RecordsPerRead = 1024;

    private const int ProgressRecordInterval = 65536;

    public async Task<DiskUsageScanResult> ScanAsync(
        DiskUsageScanRequest request,
        IProgress<DiskUsageScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        // 全扫描不归这里管，直接交给遍历式扫描器。
        if (!string.Equals(request.Mode, DiskUsageScanModes.Fast, StringComparison.Ordinal))
        {
            return await fallbackScanner
                .ScanAsync(request, progress, cancellationToken)
                .ConfigureAwait(false);
        }

        return await Task.Run(
            () => ScanFast(request, progress, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    private DiskUsageScanResult ScanFast(
        DiskUsageScanRequest request,
        IProgress<DiskUsageScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var skipped = new List<DiskUsageSkippedTarget>();
        var builder = new DiskUsageTreeBuilder(capacityHint: 1 << 18);
        var totals = new ScanTotals();
        ulong knownTotalBytes = 0;

        foreach (var volume in ResolveVolumes(request, skipped, ref knownTotalBytes))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var folderPath = request.Scope == DiskUsageScanScopes.Folder ? request.Target : null;
            if (folderPath is not null && !IsPhysicalFolderPath(volume.VolumeId, folderPath))
            {
                skipped.Add(new DiskUsageSkippedTarget(folderPath, DiskUsageSkipReasons.TargetUnavailable));
                continue;
            }
            using var reader = NtfsVolumeReader.TryOpen(volume.VolumeId);
            if (reader is null)
            {
                // 打不开就是没权限 —— 文件系统本身在卷清单里已经筛过了。
                skipped.Add(new DiskUsageSkippedTarget(
                    volume.VolumeId,
                    DiskUsageSkipReasons.NeedsElevation));
                continue;
            }

            var records = ReadAllRecords(reader, progress, totals, cancellationToken);
            if (records.Count == 0)
            {
                skipped.Add(new DiskUsageSkippedTarget(
                    volume.VolumeId,
                    DiskUsageSkipReasons.TargetUnavailable));
                continue;
            }
            if (!BuildSubtree(volume.VolumeId, folderPath, records, builder, totals, cancellationToken))
            {
                skipped.Add(new DiskUsageSkippedTarget(folderPath!, DiskUsageSkipReasons.TargetUnavailable));
            }
        }

        var tree = builder.Build();
        var roots = tree.Roots.Select(tree.PathOf).ToArray();
        return new DiskUsageScanResult(tree, new DiskUsageScanSummary(
            request.Scope,
            request.Mode,
            request.Target,
            roots,
            DiskUsageScanKinds.MasterFileTable,
            skipped,
            DateTimeOffset.UtcNow,
            stopwatch.Elapsed.TotalSeconds,
            knownTotalBytes,
            totals.Bytes >= 0 ? (ulong)totals.Bytes : 0,
            totals.Files,
            totals.Directories,
            totals.Unreadable + (builder.Truncated ? 1 : 0)));
    }

    /// <summary>
    /// 这次请求要扫哪些卷。快速扫描只能覆盖支持主文件表的卷，
    /// 其余的进 skipped 并说明原因 —— 覆盖它们是全扫描的职责。
    /// </summary>
    private IReadOnlyList<DiskUsageVolume> ResolveVolumes(
        DiskUsageScanRequest request,
        List<DiskUsageSkippedTarget> skipped,
        ref ulong knownTotalBytes)
    {
        var all = volumes.ReadVolumes();
        var candidates = request.Scope switch
        {
            DiskUsageScanScopes.Volume => all
                .Where(volume => volume.VolumeId.Equals(
                    request.Target,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray(),
            DiskUsageScanScopes.Folder => all
                .Where(volume => string.Equals(
                    Path.GetPathRoot(request.Target)?.TrimEnd(Path.DirectorySeparatorChar),
                    volume.VolumeId,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray(),
            _ => all
                .Where(static volume => volume.VolumeKind is not DiskUsageVolumeKinds.Network
                    and not DiskUsageVolumeKinds.Optical)
                .ToArray()
        };

        var selected = new List<DiskUsageVolume>();
        foreach (var volume in candidates)
        {
            if (!volume.IsReady)
            {
                skipped.Add(new DiskUsageSkippedTarget(
                    volume.VolumeId,
                    DiskUsageSkipReasons.VolumeNotReady));
                continue;
            }
            if (!volume.SupportsMasterFileTable)
            {
                skipped.Add(new DiskUsageSkippedTarget(
                    volume.VolumeId,
                    volume.FileSystem.Equals("NTFS", StringComparison.OrdinalIgnoreCase)
                        ? DiskUsageSkipReasons.NeedsElevation
                        : DiskUsageSkipReasons.NoFileSystemIndex));
                continue;
            }
            if (request.Scope != DiskUsageScanScopes.Folder)
            {
                knownTotalBytes += volume.TotalBytes - volume.FreeBytes;
            }
            selected.Add(volume);
        }

        if (candidates.Length == 0)
        {
            skipped.Add(new DiskUsageSkippedTarget(
                request.Target,
                DiskUsageSkipReasons.TargetUnavailable));
        }
        return selected;
    }

    /// <summary>
    /// 顺序读整张主文件表。返回按记录号索引的条目表；
    /// 空洞（未用或解析不了的记录）留空，后面建树时跳过。
    /// </summary>
    private static Dictionary<long, NtfsFileRecord> ReadAllRecords(
        NtfsVolumeReader reader,
        IProgress<DiskUsageScanProgress>? progress,
        ScanTotals totals,
        CancellationToken cancellationToken)
    {
        var runs = reader.ReadMasterFileTableRuns();
        if (runs.Count == 0)
        {
            return [];
        }

        var recordSize = reader.Layout.BytesPerFileRecord;
        var clusterSize = reader.Layout.BytesPerCluster;
        var chunk = new byte[recordSize * RecordsPerRead];
        var records = new Dictionary<long, NtfsFileRecord>(1 << 16);
        // 扩展记录先攒着：它引用的基记录可能还没读到，等整张表读完再合并。
        var extensionSizes = new Dictionary<long, (long Size, long Allocated)>();
        long recordNumber = 0;

        foreach (var run in runs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var runBytes = run.ClusterCount * clusterSize;
            var runOffset = run.StartCluster * clusterSize;
            long consumed = 0;

            while (consumed < runBytes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var wanted = (int)Math.Min(chunk.Length, runBytes - consumed);
                var read = reader.Read(runOffset + consumed, chunk.AsSpan(0, wanted));
                if (read < recordSize)
                {
                    break;
                }

                for (var offset = 0; offset + recordSize <= read; offset += recordSize)
                {
                    switch (NtfsFileRecordParser.Classify(
                        chunk.AsSpan(offset, recordSize),
                        out var entry,
                        out var extension))
                    {
                        case NtfsFileRecordParser.RecordKind.File when entry.InUse:
                            records[recordNumber] = entry;
                            break;
                        case NtfsFileRecordParser.RecordKind.Extension:
                            // 属性装不下时另开的记录，本身没有名字。
                            // 它带的大小要记到基记录头上，否则那个文件会显示成 0。
                            AccumulateExtension(extensionSizes, extension);
                            break;
                        case NtfsFileRecordParser.RecordKind.Unreadable:
                            // 有签名却解析不出来：这条记录确实读坏了，如实计数。
                            totals.Unreadable++;
                            break;
                    }
                    recordNumber++;

                    if (recordNumber % ProgressRecordInterval == 0)
                    {
                        progress?.Report(new DiskUsageScanProgress(
                            recordNumber,
                            totals.Bytes,
                            string.Empty,
                            // 主文件表能读到多少条事先不知道，所以不报比例。
                            null));
                    }
                }
                consumed += read;
            }
        }

        MergeExtensionSizes(records, extensionSizes);
        return records;
    }

    private static void AccumulateExtension(
        Dictionary<long, (long Size, long Allocated)> extensionSizes,
        NtfsExtensionRecord extension)
    {
        if (extension.SizeBytes <= 0 && extension.AllocatedBytes <= 0)
        {
            return;
        }
        extensionSizes.TryGetValue(extension.BaseRecordNumber, out var current);
        extensionSizes[extension.BaseRecordNumber] = (
            current.Size + extension.SizeBytes,
            current.Allocated + extension.AllocatedBytes);
    }

    /// <summary>
    /// 把扩展记录带来的大小补回基记录。
    /// 同一个 $DATA 无论拆到几条记录，只有起始簇为 0 的那一段写着真实大小，
    /// 解析时已经把其余段挡掉了，所以这里直接相加不会重复。
    /// </summary>
    private static void MergeExtensionSizes(
        Dictionary<long, NtfsFileRecord> records,
        Dictionary<long, (long Size, long Allocated)> extensionSizes)
    {
        foreach (var (baseRecord, extra) in extensionSizes)
        {
            if (!records.TryGetValue(baseRecord, out var entry) || entry.IsDirectory)
            {
                continue;
            }
            records[baseRecord] = entry with
            {
                SizeBytes = entry.SizeBytes + extra.Size,
                AllocatedBytes = entry.AllocatedBytes + extra.Allocated
            };
        }
    }

    /// <summary>
    /// 从根目录出发按先序把记录喂进树构造器。
    /// 显式栈，不递归；带访问集合，防止父子引用成环时转不出来。
    /// </summary>
    internal static bool BuildSubtree(
        string volumeId,
        string? folderPath,
        Dictionary<long, NtfsFileRecord> records,
        DiskUsageTreeBuilder builder,
        ScanTotals totals,
        CancellationToken cancellationToken)
    {
        // 先按父记录号把孩子归堆，建树时才不用整表扫。
        var childrenByParent = new Dictionary<long, List<long>>(records.Count / 2 + 1);
        foreach (var (number, record) in records)
        {
            if (number == RootRecordNumber)
            {
                continue;
            }
            if (!childrenByParent.TryGetValue(record.ParentRecordNumber, out var list))
            {
                childrenByParent[record.ParentRecordNumber] = list = [];
            }
            list.Add(number);
        }

        var rootRecord = RootRecordNumber;
        if (folderPath is not null)
        {
            var relative = Path.GetRelativePath(volumeId + Path.DirectorySeparatorChar, folderPath);
            foreach (var component in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                if (component == ".") continue;
                if (!childrenByParent.TryGetValue(rootRecord, out var children)) return false;
                var next = children.FirstOrDefault(number => records.TryGetValue(number, out var entry)
                    && entry.IsDirectory && entry.Name.Equals(component, StringComparison.OrdinalIgnoreCase));
                if (next == 0) return false;
                rootRecord = next;
            }
        }

        var rootNode = builder.Add(-1, folderPath ?? volumeId + Path.DirectorySeparatorChar, true, 0, 0);
        if (rootNode < 0)
        {
            return true;
        }
        totals.Directories++;

        var visited = new HashSet<long> { rootRecord };
        var pending = new Stack<(long Record, int Node)>();
        pending.Push((rootRecord, rootNode));

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (parentRecord, parentNode) = pending.Pop();
            if (!childrenByParent.TryGetValue(parentRecord, out var children))
            {
                continue;
            }

            foreach (var childRecord in children)
            {
                if (!records.TryGetValue(childRecord, out var entry)
                    || !visited.Add(childRecord))
                {
                    continue;
                }

                var node = builder.Add(
                    parentNode,
                    entry.Name,
                    entry.IsDirectory,
                    entry.IsDirectory ? 0 : entry.SizeBytes,
                    entry.IsDirectory ? 0 : entry.AllocatedBytes);
                if (node < 0)
                {
                    return true;
                }

                if (entry.IsDirectory)
                {
                    totals.Directories++;
                    pending.Push((childRecord, node));
                }
                else
                {
                    totals.Files++;
                    totals.Bytes += entry.SizeBytes;
                }
            }
        }
        return true;
    }

    private static bool IsPhysicalFolderPath(string volumeId, string folderPath)
    {
        var current = volumeId + Path.DirectorySeparatorChar;
        var relative = Path.GetRelativePath(current, folderPath);
        if (Path.IsPathRooted(relative) || relative == ".."
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            return false;
        try
        {
            foreach (var component in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                if (component == ".") continue;
                current = Path.Combine(current, component);
                var attributes = File.GetAttributes(current);
                if (!attributes.HasFlag(FileAttributes.Directory)
                    || attributes.HasFlag(FileAttributes.ReparsePoint)) return false;
            }
            return Directory.Exists(current);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal sealed class ScanTotals
    {
        public long Bytes;
        public long Files;
        public long Directories;
        public long Unreadable;
    }
}
