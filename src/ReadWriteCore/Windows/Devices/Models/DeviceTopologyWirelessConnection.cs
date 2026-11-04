namespace ResourceManager.ReadWrite.Windows.Devices.Models;

/// <summary>Native WLAN facts; PHY numbers are DOT11_PHY_TYPE, not marketing generations or radio bands.</summary>
public sealed record DeviceTopologyWirelessConnection(
    string Source,
    uint InterfaceStateRaw,
    IReadOnlyList<uint>? SupportedPhyTypesRaw,
    IReadOnlyList<string>? SupportedStandards,
    uint? NegotiatedPhyTypeRaw,
    string? NegotiatedStandard);
