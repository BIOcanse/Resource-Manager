namespace ResourceManager.App.Infrastructure.DeviceTopology;

internal sealed record DeviceTopologyResolvedDisplayPath(
    DeviceTopologyDisplayPath Path,
    DeviceTopologyOemDisplayConnectorProfile? Profile);
