namespace ResourceManager.ReadWrite.Windows.Devices.Models;

/// <summary>Driver declarations bound to the same identity as the battery metric catalog.</summary>
public sealed record DeviceTopologyBattery(
    string DeviceIdentity,
    string? Manufacturer,
    string? SerialNumber,
    string? Chemistry);
