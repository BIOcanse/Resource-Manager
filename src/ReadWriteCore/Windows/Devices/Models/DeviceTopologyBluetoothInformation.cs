namespace ResourceManager.ReadWrite.Windows.Devices.Models;

/// <summary>The local Bluetooth Classic radio address reported by Windows.</summary>
public sealed record DeviceTopologyBluetoothClassicControllerInformation(string ControllerAddress);

/// <summary>A Classic device row returned for one specific local radio. Authenticated is the
/// Windows fAuthenticated flag, which Windows defines as authenticated, paired, or bonded.</summary>
public sealed record DeviceTopologyBluetoothClassicDeviceInformation(
    string ControllerAddress,
    string DeviceAddress,
    bool Connected,
    bool Authenticated);

/// <summary>A complete or failed Bluetooth directory. Failed directories carry no partial rows.</summary>
public sealed record WindowsBluetoothClassicDirectoryRead<T>(
    bool DirectorySucceeded,
    IReadOnlyList<T> Objects,
    string? FailureReason = null);

/// <summary>One explicit Classic source result. ControllerAddresses are always supplied because
/// a device identity is scoped to its local radio even when controller IDs were not selected.</summary>
public sealed record WindowsBluetoothClassicInformationReadResult(
    IReadOnlyList<string> ControllerAddresses,
    WindowsBluetoothClassicDirectoryRead<DeviceTopologyBluetoothClassicControllerInformation>? Controllers = null,
    WindowsBluetoothClassicDirectoryRead<DeviceTopologyBluetoothClassicDeviceInformation>? Devices = null);
