using ResourceManager.ReadWrite.Windows.Devices.Usb;

namespace ResourceManager.ReadWrite.Windows.Devices.Models;

/// <summary>Attached USB facts for a caller-owned PnP identity; parent and connector representative are distinct.</summary>
public sealed record DeviceTopologyUsbInformation(string DeviceIdentity, string? ActualParentPortIdentity,
    string? ManufacturerName, string? ProductName, string? SerialNumber,
    double? InputPollingIntervalMilliseconds = null, double? TheoreticalReportRateHz = null,
    IReadOnlyList<DeviceTopologyUsbCameraModeDescriptor>? CameraModes = null, bool HasHidCapabilities = false,
    double? NegotiatedLinkSpeedMegabitsPerSecond = null,
    double? DeclaredMaximumLinkSpeedMegabitsPerSecond = null);
