namespace ResourceManager.ReadWrite.Native;

/// <summary>Reports whether a device catalog read completed, was unavailable, or is unsupported.</summary>
public enum NativeMetricSnapshotCatalogTopologyStatus
{
    Complete = 1,
    Unavailable = 2,
    Unsupported = 3
}
