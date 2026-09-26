using ResourceManager.App.Domain.DiskUsage;

namespace ResourceManager.App.Application.DiskUsage;

/// <summary>扫描过程中的进度。调用方拿它去驱动进度条。</summary>
public sealed record DiskUsageScanProgress(
    /// <summary>已经看过的条目数。</summary>
    long EntriesSeen,
    /// <summary>已经统计到的字节数。</summary>
    long BytesSeen,
    /// <summary>当前正在看的路径，用于让用户知道卡在哪儿。</summary>
    string CurrentPath,
    /// <summary>
    /// 完成比例（0..1）。遍历式扫描事先不知道总量，只能按已知容量估，
    /// 估不出来时为 null —— 界面这时显示不确定进度，而不是编一个数字。
    /// </summary>
    double? Fraction);

public sealed record DiskUsageScanResult(
    DiskUsageTree Tree,
    DiskUsageScanSummary Summary);

/// <summary>
/// 一次磁盘占用扫描。实现按 <see cref="DiskUsageScanRequest.Mode"/> 决定用哪条路，
/// 并把实际用了哪条写进结果 —— 不允许悄悄降级。
/// </summary>
public interface IDiskUsageScanner
{
    Task<DiskUsageScanResult> ScanAsync(
        DiskUsageScanRequest request,
        IProgress<DiskUsageScanProgress>? progress,
        CancellationToken cancellationToken);
}

/// <summary>
/// 当前扫描结果的唯一所有者。同一时刻只保留一份：换一次扫描整棵替换。
/// </summary>
public interface IDiskUsageTreeStore
{
    DiskUsageScanResult? Current { get; }

    void Replace(DiskUsageScanResult result);

    void Clear();
}
