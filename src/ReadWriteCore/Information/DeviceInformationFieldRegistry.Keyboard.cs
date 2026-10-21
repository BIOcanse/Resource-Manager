namespace ResourceManager.ReadWrite.Information;

public static partial class DeviceInformationFieldRegistry
{
    public static readonly DeviceInformationDefinition KeyboardAllScanCodeRemapConfigured =
        new("keyboard_all_scan_code_remap_configured", "machine", "all", "object", "map",
            "keyboard.hklm_keyboard_layout_scancode_map_configuration_only", "windows.registry.keyboard-layout.scancode-map");
}
