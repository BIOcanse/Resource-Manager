namespace ResourceManager.ReadWrite.Windows.Devices.Models;

/// <summary>One exact Windows NPU instance. The identity survives an unavailable
/// directory; nullable declarations do not claim a current observation.</summary>
public sealed record DeviceTopologyNpuInformation(
    string DeviceIdentity,
    string? PnpInstanceId,
    string? NameDeclared,
    string? DriverVersionDeclared,
    string? LocationInformationReported,
    IReadOnlyList<string>? LocationPathsDeclared);
