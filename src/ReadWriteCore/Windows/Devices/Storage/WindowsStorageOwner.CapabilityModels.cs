using ResourceManager.App.Domain.DeviceTopology;
namespace ResourceManager.App.Infrastructure.DeviceTopology;

public sealed record DeviceTopologyStorageSnapshot(IReadOnlyList<DeviceTopologyStorageBinding> Devices, string? Error);
public sealed record DeviceTopologyStorageBinding(string PnpDeviceId, DeviceTopologyStorageDevice Device);
