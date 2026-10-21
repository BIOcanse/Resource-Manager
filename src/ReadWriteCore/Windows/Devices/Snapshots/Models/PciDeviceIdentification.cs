namespace ResourceManager.App.Domain.DeviceTopology;

/// <summary>Windows PCI bus identifier declarations; not direct configuration-space readings.</summary>
public sealed record PciDeviceIdentification(
    string? VendorId,
    string? DeviceId,
    string? RevisionId,
    string? SubsystemIdentifier,
    string? SubsystemVendorId,
    string? BaseClass,
    string? Subclass,
    string? ProgrammingInterface)
{
    public static PciDeviceIdentification Empty { get; } = new(null, null, null, null, null, null, null, null);
}
