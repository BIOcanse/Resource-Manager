using ResourceManager.App.Domain.DeviceTopology;

namespace ResourceManager.App.Application.DeviceTopology;

public interface IDeviceTopologyReader
{
    DeviceTopologySnapshot ReadSnapshot();
}
