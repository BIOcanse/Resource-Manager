using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.ReadWrite.Windows.Devices.Models;

namespace ResourceManager.ReadWrite.Windows.Devices.Snapshots;

/// <summary>The durable topology contains declarations and relationships. Current readings
/// are projected from the complete capture before this structural snapshot is published.</summary>
public static class DeviceTopologyStructure
{
    public static DeviceTopologySnapshot Create(DeviceTopologySnapshot snapshot) => snapshot with
    {
        SourceDiagnostics = [],
        Notes = snapshot.Notes.Except(snapshot.SourceDiagnostics.Select(static diagnostic => diagnostic.MessageCode)).ToArray(),
        NetworkInterfaceInformation = snapshot.NetworkInterfaceInformation?.Select(static row => row with
        { UnicastAddresses = null, Routes = null }).ToArray(),
        NetworkEndpointInformation = DeviceTopologyNetworkEndpointInformation.Unavailable,
        VolumeSpaceInformation = snapshot.VolumeSpaceInformation?.Select(static volume => new DeviceTopologyVolumeSpaceInformation(volume.DeviceIdentity)
        {
            TotalBytes = volume.TotalBytes,
            SectorsPerAllocationUnit = volume.SectorsPerAllocationUnit,
            BytesPerSector = volume.BytesPerSector,
            AllocationUnitBytes = volume.AllocationUnitBytes,
            DiskExtents = volume.DiskExtents,
            MountPaths = volume.MountPaths,
            FileSystem = volume.FileSystem
        }).ToArray(),
        PageFileInformation = snapshot.PageFileInformation?.Select(static file => file with
        { UsedPages = null, PeakUsedPages = null }).ToArray(),
        Ports = snapshot.Ports.Select(static port => port with
        {
            SmartDevice = port.SmartDevice is null ? null : port.SmartDevice with
            {
                BatteryPercent = null,
                Storages = port.SmartDevice.Storages.Select(static storage => storage with { FreeBytes = null }).ToArray()
            },
            Storage = port.Storage is null ? null : port.Storage with
            {
                Partitions = port.Storage.Partitions.Select(static partition => partition with
                { Volumes = partition.Volumes.Select(static volume => volume with { FreeBytes = null }).ToArray() }).ToArray()
            }
        }).ToArray()
    };
}
