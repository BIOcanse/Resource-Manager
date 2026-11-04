namespace ResourceManager.ReadWrite.Windows.Devices.Models;

/// <summary>Disk number belongs to the current OS enumeration, not a persistent hardware identity.</summary>
public sealed record DeviceTopologyVolumeDiskExtent(uint DiskNumberReported, ulong StartingOffsetBytes, ulong ExtentLengthBytes);

/// <summary>Format-time filesystem serial and raw FILE_* flags are not manufacturer identity or a unified enabled state.</summary>
public sealed record DeviceTopologyVolumeFileSystemInformation(
    string Name, string Label, uint SerialReported, uint MaximumComponentLengthReported, uint FlagsReported);
