namespace ResourceManager.ReadWrite.Windows.Devices.Models;

public sealed record DeviceTopologyNativeSnapshot(
    IReadOnlyDictionary<string, DeviceTopologyNativeDevice> Devices,
    string? Error);

public sealed record DeviceTopologyNativeDevice(
    string DeviceId,
    string? FriendlyName,
    string? DeviceDescription,
    string? Manufacturer,
    string? Service,
    string? DriverKey,
    string? ClassName,
    string? ClassGuid,
    string? EnumeratorName,
    string? LocationInfo,
    IReadOnlyList<string> LocationPaths,
    IReadOnlyList<string> HardwareIds,
    IReadOnlyList<string> CompatibleIds,
    string? ParentDeviceId,
    uint? DevNodeStatus,
    uint? ProblemCode)
{
    // The presentation's empty list alone cannot distinguish a failed property
    // query from a successfully reported empty REG_MULTI_SZ declaration.
    public bool LocationPathsAvailable { get; init; }
    public DeviceTopologyDriverInformation? DriverInformation { get; init; }
    public DeviceTopologyPciResourceInformation PciResources { get; init; } = DeviceTopologyPciResourceInformation.Unavailable;
}

