using System.Diagnostics;
using ResourceManager.App.Application.DiskUsage;
using ResourceManager.App.Domain.DiskUsage;

using ResourceManager.App.Infrastructure.DiskUsage;
namespace ResourceManager.ReadWrite.Windows.Files.DiskUsage;

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
public sealed class WindowsMftDiskUsageScanner(
    IDiskUsageVolumeCatalog volumes,
    IDiskUsageScanner? fallbackScanner = null,
    Func<string, INtfsDiskUsageVolumeReader?>? openReader = null,
    Func<string, string, bool>? canScanFolder = null) : IDiskUsageScanner
{
    private readonly long retainedInputBudgetBytes = 256L * 1024 * 1024;
    private readonly int maximumRetainedRecords = 1_000_000;

    internal WindowsMftDiskUsageScanner(IDiskUsageVolumeCatalog volumes,
        long retainedInputBudgetBytes, int maximumRetainedRecords,
        Func<string, INtfsDiskUsageVolumeReader?>? openReader = null)
        : this(volumes, openReader: openReader)
    {
        this.retainedInputBudgetBytes = retainedInputBudgetBytes;
        this.maximumRetainedRecords = maximumRetainedRecords;
    }

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
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(retainedInputBudgetBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumRetainedRecords);
        if (request.Scope is not DiskUsageScanScopes.AllVolumes and not DiskUsageScanScopes.Volume and not DiskUsageScanScopes.Folder)
            throw new ArgumentException("Choose an explicit supported scan scope.", nameof(request));
        if (request.Mode is not DiskUsageScanModes.Fast and not DiskUsageScanModes.Full)
            throw new ArgumentException("Choose fast or explicit full scan.", nameof(request));
        if (request.Scope != DiskUsageScanScopes.AllVolumes && string.IsNullOrWhiteSpace(request.Target))
            throw new ArgumentException("A volume or folder target is required.", nameof(request));
        // Full traversal is an explicit caller-supplied fallback.
        if (!string.Equals(request.Mode, DiskUsageScanModes.Fast, StringComparison.Ordinal))
        {
            return await (fallbackScanner ?? throw new NotSupportedException("Full scan requires an explicit fallback scanner."))
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
            if (folderPath is not null && !(canScanFolder ?? IsPhysicalFolderPath)(volume.VolumeId, folderPath))
            {
                skipped.Add(new DiskUsageSkippedTarget(folderPath, DiskUsageSkipReasons.TargetUnavailable));
                continue;
            }
            using var reader = (openReader ?? NtfsVolumeReader.TryOpen)(volume.VolumeId);
            if (reader is null)
            {
                // 打不开就是没权限 —— 文件系统本身在卷清单里已经筛过了。
                skipped.Add(new DiskUsageSkippedTarget(
                    volume.VolumeId,
                    DiskUsageSkipReasons.NeedsElevation));
                continue;
            }

            var unreadableBefore = totals.Unreadable;
            var records = ReadAllRecords(reader, progress, totals, cancellationToken);
            if (records.Count == 0)
            {
                skipped.Add(new DiskUsageSkippedTarget(
                    volume.VolumeId,
                    DiskUsageSkipReasons.TargetUnavailable));
                continue;
            }
            if (!BuildSubtree(volume.VolumeId, folderPath, records, builder, totals, cancellationToken, reader.Layout.VolumeSerialNumber, totals.Unreadable == unreadableBefore))
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
            totals.Unreadable + (builder.Truncated ? 1 : 0))
        { IsComplete = skipped.Count == 0 && totals.Unreadable == 0 && !builder.Truncated });
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
    private Dictionary<long, NtfsFileRecord> ReadAllRecords(
        INtfsDiskUsageVolumeReader reader,
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
        var records = new Dictionary<long, NtfsFileRecord>((int)Math.Min(1 << 16,
            Math.Min(maximumRetainedRecords, retainedInputBudgetBytes / 256)));
        // 扩展记录先攒着：它引用的基记录可能还没读到，等整张表读完再合并。
        var extensions = new List<NtfsExtensionRecord>();
        long retainedBytes = 0;
        var retainedCount = 0;
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
                if (read < recordSize || read % recordSize != 0)
                {
                    totals.Unreadable++;
                    if (read < recordSize) break;
                }
                if (read < wanted) totals.Unreadable++;

                for (var offset = 0; offset + recordSize <= read; offset += recordSize)
                {
                    var kind = NtfsFileRecordParser.Classify(
                        chunk.AsSpan(offset, recordSize),
                        out var entry,
                        out var extension);
                    var retain = kind == NtfsFileRecordParser.RecordKind.File && entry.InUse
                        || kind == NtfsFileRecordParser.RecordKind.Extension && extension.InUse;
                    if (retain)
                    {
                        // Include dictionary/list capacity, record values, link arrays,
                        // link objects and UTF-16 names. Conservative retained-input
                        // accounting bounds both base and extension collections before tree building.
                        var names = kind == NtfsFileRecordParser.RecordKind.File ? entry.Names : extension.Names;
                        var bytes = 256L + names.Sum(static name => 96L + name.Name.Length * 2L);
                        if (retainedCount >= maximumRetainedRecords || bytes > retainedInputBudgetBytes - retainedBytes)
                        {
                            totals.Unreadable++;
                            MergeExtensions(records, extensions);
                            return records;
                        }
                        retainedBytes += bytes;
                        retainedCount++;
                    }
                    switch (kind)
                    {
                        case NtfsFileRecordParser.RecordKind.File when entry.InUse:
                            records[recordNumber] = entry;
                            break;
                        case NtfsFileRecordParser.RecordKind.Extension:
                            // 属性装不下时另开的记录，本身没有名字。
                            // 它带的大小要记到基记录头上，否则那个文件会显示成 0。
                            if (extension.InUse) extensions.Add(extension);
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
                // A partial raw read cannot shift subsequent record addresses.
                if (read < wanted || read % recordSize != 0) break;
            }
        }

        MergeExtensions(records, extensions);
        return records;
    }

    private static void MergeExtensions(Dictionary<long, NtfsFileRecord> records, List<NtfsExtensionRecord> extensions)
    {
        foreach (var extra in extensions)
        {
            if (!records.TryGetValue(extra.BaseRecordNumber, out var entry)
                || entry.SequenceNumber != extra.BaseSequenceNumber || entry.IsDirectory != extra.IsDirectory) continue;
            var names = NormalizeNames(entry.Names.Concat(extra.Names));
            if (extra.HasPrimaryData && !entry.IsDirectory)
            {
                if (!entry.HasPrimaryData)
                    entry = entry with { SizeBytes = extra.SizeBytes, AllocatedBytes = extra.AllocatedBytes,
                        LogicalSizeKnown = extra.LogicalSizeKnown, AllocationKnown = extra.AllocationKnown, HasPrimaryData = true };
                else if (entry.SizeBytes != extra.SizeBytes || entry.AllocatedBytes != extra.AllocatedBytes)
                    entry = entry with { LogicalSizeKnown = false, AllocationKnown = false };
                else entry = entry with { LogicalSizeKnown = entry.LogicalSizeKnown && extra.LogicalSizeKnown,
                    AllocationKnown = entry.AllocationKnown && extra.AllocationKnown };
            }
            records[extra.BaseRecordNumber] = entry with { Names = names, IsReparsePoint = entry.IsReparsePoint || extra.IsReparsePoint };
        }
    }

    /// <summary>
    /// 从根目录出发按先序把记录喂进树构造器。
    /// 显式栈，不递归；带访问集合，防止父子引用成环时转不出来。
    /// </summary>
    private static NtfsFileNameLink[] NormalizeNames(IEnumerable<NtfsFileNameLink> source)
    {
        var names = source.ToArray();
        var preferredParents = names.Where(link => link.Namespace != 2)
            .Select(link => (link.ParentRecordNumber, link.ParentSequenceNumber)).ToHashSet();
        return names.Where(link => link.Namespace != 2 || !preferredParents.Contains((link.ParentRecordNumber, link.ParentSequenceNumber)))
            .DistinctBy(link => (link.ParentRecordNumber, link.ParentSequenceNumber, link.Name)).ToArray();
    }

    public static bool BuildSubtree(string volumeId, string? folderPath,
        Dictionary<long, NtfsFileRecord> records, DiskUsageTreeBuilder builder, ScanTotals totals,
        CancellationToken cancellationToken, ulong volumeSerialNumber = 0, bool complete = true)
    {
        var childrenByParent = new Dictionary<long, List<(long Number, NtfsFileNameLink Link)>>();
        foreach (var (number, record) in records)
        {
            if (number == RootRecordNumber || !record.InUse) continue;
            if (record.Names.Count == 0 && string.IsNullOrEmpty(record.Name)) { complete = false; continue; }
            IReadOnlyList<NtfsFileNameLink> links = record.Names.Count == 0
                ? new[] { new NtfsFileNameLink(record.ParentRecordNumber, record.Name) } : record.Names;
            foreach (var link in NormalizeNames(links))
            {
                if (!records.TryGetValue(link.ParentRecordNumber, out var parent) || !parent.IsDirectory
                    || parent.SequenceNumber != link.ParentSequenceNumber) { complete = false; continue; }
                if (!childrenByParent.TryGetValue(link.ParentRecordNumber, out var list))
                    childrenByParent[link.ParentRecordNumber] = list = [];
                list.Add((number, link));
            }
        }
        var rootRecord = RootRecordNumber;
        if (!records.TryGetValue(rootRecord, out var rootEntry) || !rootEntry.IsDirectory) return false;
        if (folderPath is not null)
        {
            var relative = Path.GetRelativePath(volumeId + Path.DirectorySeparatorChar, folderPath);
            if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return false;
            foreach (var component in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                if (component == ".") continue;
                if (!childrenByParent.TryGetValue(rootRecord, out var children)) return false;
                var next = children.FirstOrDefault(child => records[child.Number].IsDirectory
                    && !records[child.Number].IsReparsePoint && child.Link.Name.Equals(component, StringComparison.OrdinalIgnoreCase));
                if (next.Number == 0) return false;
                rootRecord = next.Number;
            }
            rootEntry = records[rootRecord];
        }
        var rootNode = builder.Add(-1, folderPath ?? volumeId + Path.DirectorySeparatorChar, true, 0, 0,
            new DiskUsageObjectIdentity(volumeSerialNumber, rootRecord, rootEntry.SequenceNumber));
        if (rootNode < 0) return true;
        totals.Directories++;
        var visitedDirectories = new HashSet<long> { rootRecord };
        var pending = new Stack<(long Record, int Node)>();
        pending.Push((rootRecord, rootNode));
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (parentRecord, parentNode) = pending.Pop();
            if (!childrenByParent.TryGetValue(parentRecord, out var children)) continue;
            foreach (var (number, link) in children)
            {
                var entry = records[number];
                if (entry.IsDirectory && (entry.IsReparsePoint || !visitedDirectories.Add(number)))
                { builder.SetIncomplete(parentNode); totals.Unreadable++; continue; }
                var node = builder.Add(parentNode, link.Name, entry.IsDirectory,
                    entry.IsDirectory ? 0 : entry.SizeBytes, entry.IsDirectory ? 0 : entry.AllocatedBytes,
                    new DiskUsageObjectIdentity(volumeSerialNumber, number, entry.SequenceNumber),
                    entry.IsDirectory || entry.LogicalSizeKnown, entry.IsDirectory || entry.AllocationKnown);
                if (node < 0) { builder.SetIncomplete(rootNode); return true; }
                if (entry.IsDirectory) { totals.Directories++; pending.Push((number, node)); }
                else
                {
                    totals.Files++;
                    try { totals.Bytes = checked(totals.Bytes + entry.SizeBytes); }
                    catch (OverflowException) { totals.Unreadable++; builder.SetIncomplete(rootNode); }
                }
            }
        }
        if (!complete) { builder.SetIncomplete(rootNode); totals.Unreadable++; }
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

    public sealed class ScanTotals
    {
        public long Bytes;
        public long Files;
        public long Directories;
        public long Unreadable;
    }
}
