namespace ResourceManager.ReadWrite.Information;

public static partial class DeviceInformationFieldRegistry
{
    public static readonly DeviceInformationDefinition BluetoothAllControllerIdDeclared =
        new("bluetooth_all_controller_id_declared", "bluetooth_controller", "all", "text", "text",
            "bluetooth.classic.local_radio_bluetooth_address; Classic-only source, LE not enumerated",
            "windows.bluetooth.classic.radio-info", ReadVolatility.Fixed);

    public static readonly DeviceInformationDefinition BluetoothAllDeviceConnectedActual =
        new("bluetooth_all_device_connected_actual", "bluetooth_device", "all", "boolean", "state",
            "bluetooth.classic_device_info.fConnected; Classic-only source, LE not enumerated",
            "windows.bluetooth.classic.device-info", ReadVolatility.EventDriven("Bluetooth device connection or pairing changed"));

    public static readonly DeviceInformationDefinition BluetoothAllDevicePairedActual =
        new("bluetooth_all_device_paired_actual", "bluetooth_device", "all", "boolean", "state",
            "bluetooth.classic_device_info.fAuthenticated_paired_or_bonded; Classic-only source, LE not enumerated",
            "windows.bluetooth.classic.device-info", ReadVolatility.EventDriven("Bluetooth device connection or pairing changed"));
}
