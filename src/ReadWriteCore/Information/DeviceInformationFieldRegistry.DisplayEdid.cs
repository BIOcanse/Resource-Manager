namespace ResourceManager.ReadWrite.Information;

public static partial class DeviceInformationFieldRegistry
{
    public static readonly DeviceInformationDefinition DisplayAllModeCapability =
        new("display_all_mode_capability", "display", "all", "object", "mode",
            "display.windows_registry_edid_mode_capability_declared", "windows.registry-monitor-edid");

    public static readonly DeviceInformationDefinition DisplayAllHdrLuminanceDeclared =
        new("display_all_hdr_luminance_declared", "display", "all", "hdr-luminance-declarations", "nit",
            "display.windows_registry_edid_cta_desired_content_luminance_declared", "windows.registry-monitor-edid");

    public static readonly DeviceInformationDefinition DisplayAllChromaticityDeclared =
        new("display_all_chromaticity_declared", "display", "all", "chromaticity", "coordinate",
            "display.windows_registry_edid_base_chromaticity_declared_xy", "windows.registry-monitor-edid");

    public static readonly DeviceInformationDefinition DisplayAllEdidRawDeclared =
        new("display_all_edid_raw_declared", "display", "all", "bytes", "B",
            "display.windows_registry_edid_value_raw", "windows.registry-monitor-edid");
}
