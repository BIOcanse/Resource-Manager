using ResourceManager.App.Domain.Metrics;
using ResourceManager.ReadWrite.Native;

namespace ResourceManager.ReadWrite.Windows.Devices.Battery;

public sealed record BatteryInformation(uint Capabilities, string Chemistry,
    uint DesignedCapacity, uint FullChargedCapacity, uint CycleCount)
{
    public bool IsRelative => (Capabilities & 0x40000000) != 0;
}

public sealed record BatteryCatalogIdentity(int DisplayIndex, string DevicePath, string UniqueId,
    BatteryInformation Information)
{
    public string? Manufacturer { get; init; }
    public string? SerialNumber { get; init; }
    public string ExactIdentityKey => CreateIdentity(DevicePath, UniqueId);
    public static string CreateIdentity(string path, string uniqueId)
    {
        var normalizedPath = path.ToUpperInvariant();
        return $"windows-battery|{normalizedPath.Length}:{normalizedPath}|{uniqueId}";
    }
}

public sealed record BatteryTopologyRead(NativeMetricSnapshotCatalogTopologyStatus Status,
    IReadOnlyList<BatteryCatalogIdentity> Identities)
{
    // The interface directory succeeded, but these rows could not establish a stable battery identity.
    public int RejectedCount { get; init; }
}

public sealed record BatteryMetricRead(string DeviceIdentity,
    IReadOnlyDictionary<MetricFieldCode, double> Values, uint? PowerState, bool RequiresTopologyRefresh);

public sealed record BatteryFieldRead(string DeviceIdentity,
    IReadOnlyList<FieldReading> Readings, bool RequiresTopologyRefresh);
