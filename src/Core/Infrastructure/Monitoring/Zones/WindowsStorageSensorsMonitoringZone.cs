using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Domain.Messages;
using ResourceManager.ReadWrite.Windows.Devices;
using ResourceManager.ReadWrite.Windows.Metrics.Resources;
using static ResourceManager.App.Infrastructure.Monitoring.PlatformSensorWmiUtilities;
using SdkSnapshot = ResourceManager.ReadWrite.Windows.Devices.StorageOwnerSensorSnapshot;
using SdkQuery = ResourceManager.ReadWrite.Windows.Platform.QueryResult;

namespace ResourceManager.App.Infrastructure.Monitoring;

public sealed partial class WindowsStorageSensorsMonitoringZone
{
    // One factual reader; no host SMART, identity, or capability cache.
    private readonly WindowsStorageOwner owner;

    public WindowsStorageSensorsMonitoringZone(ResourceManager.Shared.BusinessBlocks.State.StateAuthority? authority = null)
        => owner = CreateOwner(authority: authority);

    internal WindowsStorageSensorsMonitoringZone(Func<string, string, QueryResult> query,
        Func<int, string, string?, int?, NvmeHealthRead?> readNvme,
        Func<int, string, AtaSmartPages, string?, int?, AtaSmartRead?>? readAta = null,
        Func<int, StorageIdentifyProtocol, string, string?, int?, StorageIdentifyRead?>? readIdentify = null) : this()
        => owner = CreateOwner(query, readNvme, readAta, readIdentify);

    private static WindowsStorageOwner CreateOwner(Func<string, string, QueryResult>? query = null,
        Func<int, string, string?, int?, NvmeHealthRead?>? readNvme = null,
        Func<int, string, AtaSmartPages, string?, int?, AtaSmartRead?>? readAta = null,
        Func<int, StorageIdentifyProtocol, string, string?, int?, StorageIdentifyRead?>? readIdentify = null,
        ResourceManager.Shared.BusinessBlocks.State.StateAuthority? authority = null)
        => new(TimeSpan.Zero, TimeProvider.System,
            hasMsft => BackendMessage.Create(BackendMessageDomains.DeviceTopology, hasMsft
                ? BackendMessageCodes.DeviceTopology.SourceMsftDiskAssociations : BackendMessageCodes.DeviceTopology.SourceWin32DiskAssociations),
            query: query is null ? null : (path, command) => { var result = query(path, command); return new SdkQuery(result.Rows, result.Error); },
            readNvme: readNvme, readAta: readAta, readIdentify: readIdentify,
            message: code => code switch
            {
                StorageOwnerMessage.NoPhysicalDisks => "未读取到 Windows 物理磁盘列表。",
                StorageOwnerMessage.CounterUnavailable => "Windows Storage Reliability Counter 不可用。",
                StorageOwnerMessage.CounterMissing => "未找到匹配的 MSFT_StorageReliabilityCounter。",
                StorageOwnerMessage.Active => "已读取磁盘温度。",
                StorageOwnerMessage.NoValues => "驱动或权限未返回磁盘温度。",
                _ => throw new ArgumentOutOfRangeException(nameof(code))
            })
        {
            // Memory/CPU tier option storage_capability_cache_seconds, read at each snapshot request; 0 re-reads.
            CacheLifetimeSource = () => ResourceManager.App.Infrastructure.Settings.MemoryCpuTierState.Read(authority).StorageSnapshot
        };

    internal static NativeMetricSnapshotOrdinalTopologyRead ReadIdentities(ulong generation,
        Func<string, string, QueryResult>? query = null)
    {
        var result = CreateOwner(query).ReadIdentities();
        return result.Error is null
            ? new(NativeMetricSnapshotCatalogTopologyStatus.Complete, ProjectIdentities(result.Identities, generation))
            : new(NativeMetricSnapshotCatalogTopologyStatus.Unavailable, []);
    }
    internal static IReadOnlyList<NativeMetricSnapshotOrdinalCatalogIdentity> ProjectIdentities(
        IReadOnlyList<Dictionary<string, object?>> rows, ulong generation)
        => ProjectIdentities(WindowsStorageOwner.ProjectIdentities(rows), generation);
    private static IReadOnlyList<NativeMetricSnapshotOrdinalCatalogIdentity> ProjectIdentities(
        IReadOnlyList<StorageOwnerIdentity> identities, ulong generation)
        => identities.Select(identity => WindowsHardwareMetricResources.StorageCatalogIdentity(identity, generation)).ToArray();
    private static StorageSensorSnapshot Adapt(SdkSnapshot snapshot)
        => WindowsHardwareMetricResources.ProjectStorageSensors(snapshot);
    internal StorageSensorSnapshot ReadSensors() => ReadStorageSensors();

    internal StorageSensorSnapshot ReadStorageSensors() => Adapt(owner.ReadStorageSensors());
    internal static Dictionary<MetricFieldCode, ulong> ReadReliabilityValues(IReadOnlyDictionary<string, object?>? row)
        => WindowsStorageOwner.ReadReliabilityValues(row);
}
// Passive sensor snapshot is owned by ReadWriteCore; original namespace preserved.
