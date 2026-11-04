namespace ResourceManager.App.Domain.ResourceBreakdown;

/// <summary>Hierarchical measured leaves. A parent and its children are never emitted together.</summary>
public static class PhysicalMemoryResidualKinds
{
    public const string NonPagedPool = "system-memory/nonpaged-pool";
    public const string PoolTagPrefix = NonPagedPool + "/tag/";
    public const string OtherPoolTags = NonPagedPool + "/other-tags";
    public const string UnclassifiedPool = NonPagedPool + "/unclassified";
    public const string PagedPoolResident = "system-memory/paged-pool-resident";
    public const string DriverResident = "system-memory/driver-resident";
    public const string CodeResident = "system-memory/code-resident";
    public const string Unclassified = "system-memory/unclassified";
}
