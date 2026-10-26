namespace ResourceManager.App.Domain.DeviceTopology;

public sealed record DeviceTopologyGpuInformation(
    string PnpInstanceId,
    string? AdapterLuid,
    string? Name,
    string? PciVendorId,
    string? PciDeviceId,
    string? DriverVersion)
{
    // Source is the enumerated rendering adapter, never a peer computer or a generic external port.
    public uint? AdapterTypeFlags { get; init; }
    public bool? IsDetachable => AdapterTypeFlags is { } flags ? (flags & (1u << 10)) != 0 : null;
    public string? AttachmentKind => IsDetachable == true ? "graphics_dock" : null;
    public string? AttachmentEvidenceSource => AdapterTypeFlags.HasValue ? "d3dkmt-adapter-type" : null;
}
