using ResourceManager.ReadWrite.Windows.Devices.Display;
using ResourceManager.ReadWrite.Windows.Devices.Models;
namespace ResourceManager.App.Infrastructure.DeviceTopology;

public sealed record DeviceTopologyResolvedDisplayPath(
    DeviceTopologyDisplayPath Path,
    DeviceTopologyOemDisplayConnectorProfile? Profile);
