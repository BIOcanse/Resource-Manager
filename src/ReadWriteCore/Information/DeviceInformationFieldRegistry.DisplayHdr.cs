namespace ResourceManager.ReadWrite.Information;

public static partial class DeviceInformationFieldRegistry
{
    public static readonly DeviceInformationDefinition DisplayAllHdrCapability =
        new("display_all_hdr_capability", "display", "all", "boolean", "boolean",
            "display.hdr_supported_reported", "windows.display-config");
    public static readonly DeviceInformationDefinition DisplayAllHdrEnabledActual =
        new("display_all_hdr_enabled_actual", "display", "all", "boolean", "boolean",
            "display.hdr_user_enabled_actual", "windows.display-config");
    public static readonly DeviceInformationDefinition DisplayAllHdrActiveActual =
        new("display_all_hdr_active_actual", "display", "all", "boolean", "boolean",
            "display.hdr_active_actual", "windows.display-config");
    public static readonly DeviceInformationDefinition DisplayAllColorModeActual =
        new("display_all_color_mode_actual", "display", "all", "unsigned-integer", "enum",
            "display.color_mode_actual_reported", "windows.display-config");
}
