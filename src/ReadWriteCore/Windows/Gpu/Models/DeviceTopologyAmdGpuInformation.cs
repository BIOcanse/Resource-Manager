namespace ResourceManager.App.Domain.DeviceTopology;

public sealed record DeviceTopologyAmdGpuInformation(string PnpInstanceId,
    string? VbiosVersion, string? VramType, string? VbiosPartNumber, string? VbiosDateRaw);
