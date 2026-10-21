namespace ResourceManager.App.Domain.DeviceTopology;

public static class DeviceConnectorKinds
{
    public const string UsbA = "usb-a";
    public const string UsbC = "usb-c";
    public const string Thunderbolt = "thunderbolt";
    public const string Hdmi = "hdmi";
    public const string DisplayPort = "displayport";
    public const string MiniDisplayPort = "mini-displayport";
    public const string Dvi = "dvi";
    public const string Vga = "vga";
    public const string InternalDisplay = "internal-display";
    public const string WirelessDisplay = "wireless-display";
    public const string Rj45 = "rj45";
    public const string Audio = "audio";
    public const string Power = "power";
    public const string SdCard = "sd-card";
    public const string Pcie = "pcie";
    public const string Bluetooth = "bluetooth";
    public const string Generic = "generic";
}

public static class DeviceDisplayOutputTechnologies
{
    public const string Other = "other";
    public const string Vga = "vga";
    public const string SVideo = "s-video";
    public const string CompositeVideo = "composite-video";
    public const string ComponentVideo = "component-video";
    public const string Dvi = "dvi";
    public const string Hdmi = "hdmi";
    public const string Lvds = "lvds";
    public const string DJpn = "d-jpn";
    public const string Sdi = "sdi";
    public const string DisplayPortExternal = "displayport-external";
    public const string DisplayPortEmbedded = "displayport-embedded";
    public const string UdiExternal = "udi-external";
    public const string UdiEmbedded = "udi-embedded";
    public const string SdtvDongle = "sdtv-dongle";
    public const string Miracast = "miracast";
    public const string IndirectWired = "indirect-wired";
    public const string IndirectVirtual = "indirect-virtual";
    public const string DisplayPortUsb4Tunnel = "displayport-usb4-tunnel";
    public const string Internal = "internal";
    public const string Unknown = "unknown";
}

public static class DeviceEdidDigitalInterfaces
{
    public const string Analog = "analog";
    public const string Undefined = "undefined";
    public const string Dvi = "dvi";
    public const string HdmiTypeA = "hdmi-type-a";
    public const string HdmiTypeB = "hdmi-type-b";
    public const string Mddi = "mddi";
    public const string DisplayPort = "displayport";
    public const string Reserved = "reserved";
}
