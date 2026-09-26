using ResourceManager.App.Domain.DiskUsage;

namespace ResourceManager.App.Application.DiskUsage;

/// <summary>
/// 本机有哪些卷可以扫。只读，随叫随取——盘符会插拔，所以不缓存。
/// </summary>
public interface IDiskUsageVolumeCatalog
{
    IReadOnlyList<DiskUsageVolume> ReadVolumes();
}
