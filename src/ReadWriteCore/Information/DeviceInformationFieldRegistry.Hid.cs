namespace ResourceManager.ReadWrite.Information;

public static partial class DeviceInformationFieldRegistry
{
    public static readonly DeviceInformationDefinition HidAllPnpInstanceIdDeclared =
        new("hid_all_pnp_instance_id_declared", "hid", "all", "text", "text", "hid.collection_pnp_instance_declared", "windows.hid.capabilities", ReadVolatility.Fixed);
    public static readonly DeviceInformationDefinition HidAllUsageDeclared =
        new("hid_all_usage_declared", "hid", "all", "object", "usage16/page16", "hid.collection_usage_declared", "windows.hid.capabilities", ReadVolatility.Fixed);
    public static readonly DeviceInformationDefinition HidAllReportLengthDeclared =
        new("hid_all_report_length_declared", "hid", "all", "object", "B", "hid.maximum_report_lengths_declared", "windows.hid.capabilities", ReadVolatility.Fixed);
    public static readonly DeviceInformationDefinition HidAllButtonsCapability =
        new("hid_all_buttons_capability", "hid", "all", "array", "capability[]", "hid.binary_control_capabilities_declared", "windows.hid.capabilities", ReadVolatility.Fixed);
    public static readonly DeviceInformationDefinition HidAllValueCapability =
        new("hid_all_value_capability", "hid", "all", "array", "capability[]", "hid.value_control_capabilities_declared", "windows.hid.capabilities", ReadVolatility.Fixed);
}
