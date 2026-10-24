using ResourceManager.ReadWrite.Native;
namespace ResourceManager.App.Infrastructure.Monitoring;

// Durable state contains catalog identity only; native checkpoints remain explicit in-process exports.
public sealed record NativeMetricSnapshotPersistenceCallbacks(
    Func<ulong, string, NativeMetricSnapshotCapacity, NativeMetricSnapshotCatalogPersistenceState?> Load,
    Action<ulong, NativeMetricSnapshotCatalogPersistenceState> Save,
    Action Discard,
    Action<string>? Warning = null);

public sealed record NativeMetricSnapshotPersistenceImage(
    NativeMetricSnapshotCatalogPersistenceState Catalog,
    NativeMetricSnapshotPersistenceHeader Header,
    NativeMetricSnapshotSourcePersistenceOutput[] Sources,
    NativeMetricSnapshotRuleStateOutput[] Rules,
    NativeMetricSnapshotGpuInventoryOutput[] GpuInventory);

