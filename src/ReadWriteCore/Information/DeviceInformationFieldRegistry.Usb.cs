namespace ResourceManager.ReadWrite.Information;

public static partial class DeviceInformationFieldRegistry
{
    public static readonly DeviceInformationDefinition UsbAllParentPortActual =
        new("usb_all_parent_port_actual", "usb", "all", "text", "text",
            "usb.windows_attached_device_immediate_parent_port_actual", "windows.usb-hub-connection", ReadVolatility.EventDriven("Windows USB port connection changed"));
    public static readonly DeviceInformationDefinition UsbAllLinkSpeedNegotiated =
        new("usb_all_link_speed_negotiated", "usb", "all", "number", "Mbit/s",
            "usb.attached_device_current_negotiated_link_rate", "windows.usb-hub-connection", ReadVolatility.EventDriven("Windows USB port connection changed"));
    public static readonly DeviceInformationDefinition UsbAllLinkSpeedCapability =
        new("usb_all_link_speed_capability", "usb", "all", "number", "Mbit/s",
            "usb.attached_device_bos_declared_maximum_link_rate", "windows.usb-hub-connection", ReadVolatility.Fixed);
    public static readonly DeviceInformationDefinition CameraAllFrameIntervalCapability =
        new("camera_all_frame_interval_capability", "camera", "all", "array", "100ns",
            "camera.uvc_declared_frame_interval_capability", "usb.video-class-descriptors", ReadVolatility.Fixed);
}
