namespace ResourceManager.App.Infrastructure.DeviceTopology;

internal sealed record DeviceTopologyNativeSnapshot(
    IReadOnlyDictionary<string, DeviceTopologyNativeDevice> Devices,
    string? Error);

internal sealed record DeviceTopologyNativeDevice(
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
    uint? ProblemCode);
