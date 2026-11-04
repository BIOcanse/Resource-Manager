namespace ResourceManager.ReadWrite.Windows.Devices.Models;

/// <summary>One OS volume GUID; quota-dependent values belong to QueryCallerSid.</summary>
public sealed record DeviceTopologyVolumeSpaceInformation(string DeviceIdentity)
{
    public string? QueryCallerSid { get; init; }
    public UInt128? TotalBytes { get; init; }
    public UInt128? AvailableBytes { get; init; }
    public UInt128? PoolUnavailableBytes { get; init; }
    public UInt128? CallerTotalBytes { get; init; }
    public UInt128? CallerAvailableBytes { get; init; }
    public UInt128? CallerPoolUnavailableBytes { get; init; }
    public UInt128? UsedBytes { get; init; }
    public UInt128? ReservedBytes { get; init; }
    public UInt128? StorageReserveBytes { get; init; }
    public UInt128? AvailableCommittedBytes { get; init; }
    public UInt128? PoolAvailableBytes { get; init; }
    public uint? SectorsPerAllocationUnit { get; init; }
    public uint? BytesPerSector { get; init; }
    public ulong? AllocationUnitBytes { get; init; }
    public IReadOnlyList<DeviceTopologyVolumeDiskExtent>? DiskExtents { get; init; }
    public IReadOnlyList<string>? MountPaths { get; init; }
    public DeviceTopologyVolumeFileSystemInformation? FileSystem { get; init; }
}
