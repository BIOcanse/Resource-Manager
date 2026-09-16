using System.Diagnostics;
using ResourceManager.App.Application.DiskUsage;
using ResourceManager.App.Domain.DiskUsage;

namespace ResourceManager.App.Infrastructure.DiskUsage;

/// <summary>
/// 全扫描：逐级遍历目录。
///
/// 慢，但不挑文件系统、不需要提权，所以它是「不支持文件索引的磁盘」的覆盖手段。
/// 遍历用显式栈，不递归 —— 深目录会把调用栈打爆，而且项目规矩就是不递归。
///
/// 节点按先序加入（父先于子），这样 <see cref="DiskUsageTreeBuilder.Build"/>
/// 反向扫一遍就能把大小汇总上去。
/// </summary>
internal sealed class DirectoryWalkDiskUsageScanner(
    IDiskUsageVolumeCatalog volumes) : IDiskUsageScanner
{
    /// <summary>每看这么多条目回报一次进度。太密会把进度通道打满。</summary>
    private const int ProgressEntryInterval = 4096;

    public Task<DiskUsageScanResult> ScanAsync(
        DiskUsageScanRequest request,
        IProgress<DiskUsageScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Task.Run(() => Scan(request, progress, cancellationToken), cancellationToken);
    }

    private DiskUsageScanResult Scan(
        DiskUsageScanRequest request,
        IProgress<DiskUsageScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var skipped = new List<DiskUsageSkippedTarget>();
        var targets = ResolveTargets(request, skipped, out var knownTotalBytes);
        var builder = new DiskUsageTreeBuilder(capacityHint: 1 << 16);
        var state = new WalkState();

        foreach (var target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WalkOneRoot(target, builder, state, progress, knownTotalBytes, cancellationToken);
        }

        var tree = builder.Build();
        var roots = tree.Roots.Select(tree.PathOf).ToArray();
        var summary = new DiskUsageScanSummary(
            request.Scope,
            request.Mode,
            request.Target,
            roots,
            DiskUsageScanKinds.DirectoryWalk,
            skipped,
            DateTimeOffset.UtcNow,
            stopwatch.Elapsed.TotalSeconds,
            knownTotalBytes,
            state.BytesSeen >= 0 ? (ulong)state.BytesSeen : 0,
            state.FileCount,
            state.DirectoryCount,
            state.UnreadableCount + (builder.Truncated ? 1 : 0));
        return new DiskUsageScanResult(tree, summary);
    }

    /// <summary>
    /// 请求的范围落成一组要走的根路径。走不了的目标进 <paramref name="skipped"/>，
    /// 不静默丢掉。
    /// </summary>
    private IReadOnlyList<string> ResolveTargets(
        DiskUsageScanRequest request,
        List<DiskUsageSkippedTarget> skipped,
        out ulong knownTotalBytes)
    {
        knownTotalBytes = 0;
        switch (request.Scope)
        {
            case DiskUsageScanScopes.Folder:
                if (!Directory.Exists(request.Target))
                {
                    skipped.Add(new DiskUsageSkippedTarget(
                        request.Target,
                        DiskUsageSkipReasons.TargetUnavailable));
                    return [];
                }
                return [request.Target];

            case DiskUsageScanScopes.Volume:
            {
                var volume = volumes.ReadVolumes()
                    .FirstOrDefault(candidate => candidate.VolumeId.Equals(
                        request.Target,
                        StringComparison.OrdinalIgnoreCase));
                if (volume is null)
                {
                    skipped.Add(new DiskUsageSkippedTarget(
                        request.Target,
                        DiskUsageSkipReasons.TargetUnavailable));
                    return [];
                }
                if (!volume.IsReady)
                {
                    skipped.Add(new DiskUsageSkippedTarget(
                        volume.VolumeId,
                        DiskUsageSkipReasons.VolumeNotReady));
                    return [];
                }
                knownTotalBytes = volume.TotalBytes - volume.FreeBytes;
                return [volume.VolumeId + Path.DirectorySeparatorChar];
            }

            default:
            {
                var targets = new List<string>();
                foreach (var volume in volumes.ReadVolumes())
                {
                    // 网络位置和光驱不进「全部磁盘」：一个可能极慢，一个基本没意义。
                    if (volume.VolumeKind is DiskUsageVolumeKinds.Network
                        or DiskUsageVolumeKinds.Optical)
                    {
                        continue;
                    }
                    if (!volume.IsReady)
                    {
                        skipped.Add(new DiskUsageSkippedTarget(
                            volume.VolumeId,
                            DiskUsageSkipReasons.VolumeNotReady));
                        continue;
                    }
                    knownTotalBytes += volume.TotalBytes - volume.FreeBytes;
                    targets.Add(volume.VolumeId + Path.DirectorySeparatorChar);
                }
                return targets;
            }
        }
    }

    private static void WalkOneRoot(
        string rootPath,
        DiskUsageTreeBuilder builder,
        WalkState state,
        IProgress<DiskUsageScanProgress>? progress,
        ulong knownTotalBytes,
        CancellationToken cancellationToken)
    {
        var rootNode = builder.Add(-1, rootPath, isDirectory: true, 0, 0);
        if (rootNode < 0)
        {
            return;
        }
        state.DirectoryCount++;

        // 显式栈：待展开的目录（节点序号 + 它的绝对路径）。
        var pending = new Stack<(int Node, string Path)>();
        pending.Push((rootNode, rootPath));

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (node, path) = pending.Pop();

            IEnumerable<FileSystemInfo> entries;
            try
            {
                entries = new DirectoryInfo(path).EnumerateFileSystemInfos(
                    "*",
                    new EnumerationOptions
                    {
                        // 重解析点会把遍历引到别的卷上去，甚至绕成环。
                        AttributesToSkip = FileAttributes.ReparsePoint,
                        IgnoreInaccessible = true,
                        RecurseSubdirectories = false,
                        ReturnSpecialDirectories = false
                    });
            }
            catch (Exception error) when (IsExpectedIoFailure(error))
            {
                state.UnreadableCount++;
                continue;
            }

            using var enumerator = entries.GetEnumerator();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                FileSystemInfo entry;
                try
                {
                    if (!enumerator.MoveNext())
                    {
                        break;
                    }
                    entry = enumerator.Current;
                }
                catch (Exception error) when (IsExpectedIoFailure(error))
                {
                    state.UnreadableCount++;
                    break;
                }

                if (entry is DirectoryInfo directory)
                {
                    var child = builder.Add(node, directory.Name, isDirectory: true, 0, 0);
                    if (child < 0)
                    {
                        return;
                    }
                    state.DirectoryCount++;
                    pending.Push((child, directory.FullName));
                }
                else if (entry is FileInfo file)
                {
                    long size;
                    try
                    {
                        size = file.Length;
                    }
                    catch (Exception error) when (IsExpectedIoFailure(error))
                    {
                        state.UnreadableCount++;
                        continue;
                    }

                    if (builder.Add(node, file.Name, isDirectory: false, size, size) < 0)
                    {
                        return;
                    }
                    state.FileCount++;
                    state.BytesSeen += size;
                }

                state.EntriesSeen++;
                if (state.EntriesSeen % ProgressEntryInterval == 0)
                {
                    progress?.Report(new DiskUsageScanProgress(
                        state.EntriesSeen,
                        state.BytesSeen,
                        path,
                        EstimateFraction(state.BytesSeen, knownTotalBytes)));
                }
            }
        }
    }

    /// <summary>
    /// 遍历事先不知道总量，只能拿「卷的已用字节」当分母估。估不出来就返回 null，
    /// 让界面显示不确定进度，而不是编一个数字。
    /// </summary>
    private static double? EstimateFraction(long bytesSeen, ulong knownTotalBytes)
    {
        if (knownTotalBytes == 0 || bytesSeen <= 0)
        {
            return null;
        }
        return Math.Min(1d, bytesSeen / (double)knownTotalBytes);
    }

    private static bool IsExpectedIoFailure(Exception error)
        => error is IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException;

    private sealed class WalkState
    {
        public long EntriesSeen;
        public long BytesSeen;
        public long FileCount;
        public long DirectoryCount;
        public long UnreadableCount;
    }
}
