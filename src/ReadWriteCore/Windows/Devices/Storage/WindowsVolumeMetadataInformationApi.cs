using ResourceManager.ReadWrite.Windows.Devices.Models;

namespace ResourceManager.ReadWrite.Windows.Devices;

internal interface IWindowsVolumeMetadataInformationApi
{
    IReadOnlyList<DeviceTopologyVolumeDiskExtent>? ReadDiskExtents(string root);
    IReadOnlyList<string>? ReadMountPaths(string root);
    DeviceTopologyVolumeFileSystemInformation? ReadFileSystem(string root);
}
