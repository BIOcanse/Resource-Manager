namespace ResourceManager.ReadWrite.ProcessFields;

/// <summary>The original process dataset identities, shared by read and subscription adapters.</summary>
public static class ProcessMetricDatasetIds
{
    public const string ObservedNetworkTraffic = "process.network.observed-traffic";
    public const string ObservedPhysicalDiskIo = "process.disk.observed-io";
    public const string GpuResidency = "process.gpu.engine-residency";
    public const string ObservedCpuResidency = "process.cpu.residency";
    public const string BasicMetrics = "process.basic.metrics";
}

