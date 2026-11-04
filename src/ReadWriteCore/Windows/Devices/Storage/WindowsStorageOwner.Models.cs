using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Domain.Metrics;
namespace ResourceManager.ReadWrite.Windows.Devices;

public sealed record StorageOwnerIdentity(int DisplayIndex, string ExactIdentityKey)
{
    public string? UniqueId { get; init; }
    public string? SerialNumber { get; init; }
    public string? NameDeclared { get; init; }
    public bool SupportsNvmeHealth { get; init; }
    public bool SupportsAtaSmart { get; init; }
}
public sealed record StorageIdentityRead(string? Error, IReadOnlyList<StorageOwnerIdentity> Identities);
public sealed record StorageVolumeObservation(bool IsReady, string? FileSystem, ulong? CapacityBytes, ulong? AvailableFreeBytes);
public enum StorageOwnerMessage { NoPhysicalDisks, CounterUnavailable, CounterMissing, Active, NoValues }
public sealed record StorageOwnerSensorSnapshot(HardwareSensorProviderState ProviderState, IReadOnlyList<DiskTemperatureSensor> Disks)
{
    // Null is failed enumeration; [] is a successful enumeration without unambiguous identities.
    public IReadOnlyList<StorageOwnerIdentity>? Identities { get; init; }
}
