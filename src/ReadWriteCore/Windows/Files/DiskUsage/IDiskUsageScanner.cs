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
    DiskUsageScanSummary? CurrentSummary { get; }

    DiskUsageTreeReadLease? TryAcquire();

    void Replace(DiskUsageScanResult result, CancellationToken cancellationToken = default);

    void Clear();
}

/// <summary>The authenticated Windows user and logon session that owns a scan result.</summary>
public readonly record struct DiskUsageResultOwner(string WindowsSid, int WindowsSessionId)
{
    public bool IsValid => !string.IsNullOrWhiteSpace(WindowsSid) && WindowsSessionId >= 0;

    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(WindowsSid);
        if (WindowsSessionId < 0)
            throw new ArgumentOutOfRangeException(nameof(WindowsSessionId));
    }
}

/// <summary>Owner-scoped access to the original tree store; it is not another result cache.</summary>
public interface IOwnedDiskUsageTreeStore : IDiskUsageTreeStore
{
    DiskUsageScanSummary? SummaryFor(DiskUsageResultOwner owner);

    DiskUsageTreeReadLease? TryAcquire(DiskUsageResultOwner owner);

    void Replace(DiskUsageScanResult result, DiskUsageResultOwner owner, CancellationToken cancellationToken = default);
}

/// <summary>Imports the actual published tree. The product supplies its allocator;
/// standalone read/write tools may keep their independently owned managed tree.</summary>
public interface IDiskUsageTreePayloadAllocator
{
    IDiskUsageTreePayload Import(DiskUsageTree source, CancellationToken cancellationToken = default);
}

public interface IDiskUsageTreePayload
{
    /// <summary>Reports completed release. The handler must not call the allocator.</summary>
    event Action? Released;
    IDiskUsageTreePayloadReadLease? TryAcquire();
    /// <summary>Stop new accesses and safely close after existing readers drain.</summary>
    void Retire();
}

public interface IDiskUsageTreePayloadReadLease : IDisposable
{
    DiskUsageTree Tree { get; }
}

/// <summary>One operation's result access. Tree spans and views must not escape this lease.</summary>
public sealed class DiskUsageTreeReadLease : IDisposable
{
    private IDiskUsageTreePayloadReadLease? payload;
    private readonly DiskUsageScanResult result;
    internal DiskUsageTreeReadLease(IDiskUsageTreePayloadReadLease payload, DiskUsageScanSummary summary)
    { this.payload = payload; result = new(payload.Tree, summary); }
    public DiskUsageScanResult Result => payload is not null ? result : throw new ObjectDisposedException(nameof(DiskUsageTreeReadLease));
    public void Dispose() => Interlocked.Exchange(ref payload, null)?.Dispose();
}
