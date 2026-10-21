namespace ResourceManager.App.Domain.DeviceTopology;

public sealed record DeviceTopologyGpuInformation(
    string PnpInstanceId,
    string? AdapterLuid,
    string? Name,
    string? PciVendorId,
    string? PciDeviceId,
    string? DriverVersion);
