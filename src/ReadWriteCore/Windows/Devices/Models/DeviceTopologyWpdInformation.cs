using ResourceManager.ReadWrite.Windows.Devices.PortableDevices;

namespace ResourceManager.ReadWrite.Windows.Devices.Models;

/// <summary>Reported values from one exact WPD manager connection and its root object.
/// Null means that the selected property was not returned or could not be read.</summary>
public sealed record DeviceTopologyWpdDeviceInformation(
    string DeviceConnectionId,
    string DeviceObjectId,
    string? PersistentUniqueId,
    string? FirmwareVersion,
    uint? BatteryPercent,
    bool? StorageDirectorySucceeded,
    IReadOnlyList<DeviceTopologyWpdStorageInformation> Storages);

/// <summary>Reported total capacity from one exact WPD storage functional object.</summary>
public sealed record DeviceTopologyWpdStorageInformation(
    string DeviceConnectionId,
    string ObjectId,
    ulong? CapacityBytes);

/// <summary>A failed requested property read. ObjectId is null for a device-root
/// or storage-directory failure; FailedFields names exactly which values were affected.</summary>
public sealed record WindowsWpdInformationReadFailure(
    string DeviceConnectionId,
    string? ObjectId,
    WindowsWpdInformationFields FailedFields,
    string Code);

/// <summary>The result of one WPD directory/capture. A failed manager enumeration has
/// no partial device rows; per-device and per-object failures remain separately visible.</summary>
public sealed record WindowsWpdInformationReadResult(
    bool DeviceDirectorySucceeded,
    IReadOnlyList<DeviceTopologyWpdDeviceInformation> Devices,
    IReadOnlyList<WindowsWpdInformationReadFailure> Failures,
    string? DeviceDirectoryFailureCode = null);
