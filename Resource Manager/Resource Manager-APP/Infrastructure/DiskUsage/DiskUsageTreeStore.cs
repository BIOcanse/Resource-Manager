using ResourceManager.App.Application.DiskUsage;

namespace ResourceManager.App.Infrastructure.DiskUsage;

/// <summary>
/// 当前扫描结果的唯一所有者。
///
/// 同一时刻只留一份：新结果算完之后整棵原子替换，读取端永远看到一份完整的树，
/// 不会读到扫到一半的中间状态。
/// </summary>
internal sealed class DiskUsageTreeStore : IDiskUsageTreeStore
{
    private DiskUsageScanResult? current;

    public DiskUsageScanResult? Current => Volatile.Read(ref current);

    public void Replace(DiskUsageScanResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        Volatile.Write(ref current, result);
    }

    public void Clear() => Volatile.Write(ref current, null);
}
