namespace ResourceManager.ReadWrite.Windows.Devices.Models;

public enum StorageIdentifyProtocol { Ata, NvmeController }

/// <summary>The identity is the existing physical-disk identity; protocol declarations keep their own domain.</summary>
public sealed record DeviceTopologyStorageInformation(
    string DeviceIdentity,
    StorageIdentifyProtocol Protocol,
    AtaIdentifyInformation? Ata,
    NvmeControllerIdentifyInformation? Nvme)
{
    public StorageHardwareIdentityEvidence? HardwareIdentityEvidence { get; init; }
}

/// <summary>Current identity inputs confirmed by the existing opened-device Identify reader.</summary>
public sealed record StorageHardwareIdentityEvidence(string SerialNumber, string? UniqueId, int? UniqueIdFormat);

public sealed record AtaIdentifyInformation(
    byte[] Raw,
    string? Model,
    string? SerialNumber,
    string? FirmwareRevision,
    ulong? CapacityBytes,
    ulong? LogicalSectorBytes,
    ulong? PhysicalSectorBytes,
    bool? TrimSupported,
    ushort? MajorVersionSupportedMask,
    ushort? MediaRotationRateRpm,
    bool? NonRotatingMedia);

public sealed record NvmeControllerIdentifyInformation(
    byte[] Raw,
    string? Model,
    string? SerialNumber,
    string? FirmwareRevision,
    ushort VendorId,
    ushort SubsystemVendorId,
    uint IeeeOui,
    uint? Version,
    ushort ControllerId,
    UInt128? TotalNvmCapacityBytes,
    UInt128? UnallocatedNvmCapacityBytes,
    uint MaximumValidNamespaceId,
    bool DatasetManagementSupported);
