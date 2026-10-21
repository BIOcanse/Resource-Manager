using ResourceManager.ReadWrite.Native;
namespace ResourceManager.App.Infrastructure.Monitoring;

// The SDK imports/exports native facts; callers own envelope IO and checkpoint placement.
public sealed record NativeMetricSnapshotPersistenceCallbacks(
    Func<ulong, string, NativeMetricSnapshotCapacity, NativeMetricSnapshotPersistenceImage?> Load,
    Action<ulong, NativeMetricSnapshotPersistenceImage> Save,
    Action Discard);

public sealed record NativeMetricSnapshotPersistenceImage(
    NativeMetricSnapshotCatalogPersistenceState Catalog,
    NativeMetricSnapshotPersistenceHeader Header,
    NativeMetricSnapshotSourcePersistenceOutput[] Sources,
    NativeMetricSnapshotRuleStateOutput[] Rules,
    NativeMetricSnapshotGpuInventoryOutput[] GpuInventory);

