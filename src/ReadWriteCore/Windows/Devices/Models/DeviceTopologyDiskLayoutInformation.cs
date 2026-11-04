namespace ResourceManager.ReadWrite.Windows.Devices.Models;

public enum DiskPartitionStyle : uint { Mbr = 0, Gpt = 1, Raw = 2 }

public sealed record DeviceTopologyDiskLayoutInformation(string DeviceIdentity)
{
    public uint? OsDiskNumberReported { get; init; }
    public DeviceTopologyDiskLayout? Layout { get; init; }
    public DiskAccessAlignmentInformation? AccessAlignment { get; init; }
    public DiskHardwareIdentification? HardwareIdentification { get; init; }
    public uint? BusTypeReported { get; init; }
    public bool? TrimEnabledReported { get; init; }
}

public sealed record DiskHardwareIdentification(StorageIdentifyProtocol Protocol, string SerialNumber, byte[]? DeviceIdentifiers);

/// <summary>Raw Windows storage-stack report; this does not identify a physical hardware medium.</summary>
public sealed record DiskAccessAlignmentInformation(uint CacheLineSizeBytes, uint CacheAlignmentOffsetBytes,
    uint LogicalSectorSizeBytes, uint PhysicalSectorSizeBytes, uint PhysicalSectorOffsetBytes);

public sealed record DeviceTopologyDiskLayout(DiskPartitionStyle Style, DiskLayoutMbr? Mbr, DiskLayoutGpt? Gpt,
    IReadOnlyList<DiskLayoutPartition> Partitions);
public sealed record DiskLayoutMbr(uint Signature, uint Checksum);
public sealed record DiskLayoutGpt(Guid DiskId, ulong StartingUsableOffsetBytes, ulong UsableLengthBytes, uint MaximumPartitionCount);
public sealed record DiskLayoutPartition(uint Number, ulong StartingOffsetBytes, ulong LengthBytes,
    bool RewriteReported, bool IsServiceReported, DiskPartitionMbr? Mbr, DiskPartitionGpt? Gpt);
public sealed record DiskPartitionMbr(byte Type, bool BootIndicator, bool Recognized, uint HiddenSectors, Guid PartitionId);
public sealed record DiskPartitionGpt(Guid Type, Guid PartitionId, ulong Attributes, string Name);
