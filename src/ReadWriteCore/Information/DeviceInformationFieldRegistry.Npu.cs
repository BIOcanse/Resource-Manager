namespace ResourceManager.ReadWrite.Information;

public static partial class DeviceInformationFieldRegistry
{
    public static readonly DeviceInformationDefinition NpuAllDeviceIdDeclared = new(
        "npu_all_device_id_declared", "npu", "all", "text", "text",
        "npu.exact_windows_pnp_instance_id_declared_not_pci_model_id", "windows.dxcore-npu.cfgmgr32", ReadVolatility.Fixed);
    public static readonly DeviceInformationDefinition NpuAllName = new(
        "npu_all_name", "npu", "all", "text", "text",
        "npu.same_instance_friendly_name_or_device_description_declared", "windows.pnp", ReadVolatility.EventDriven("Windows PnP device properties changed"));
    public static readonly DeviceInformationDefinition NpuAllDriverVersionDeclared = new(
        "npu_all_driver_version_declared", "npu", "all", "text", "text",
        "npu.same_device_bound_inf_driver_version_declared", "windows.cfgmgr32.driver-properties", ReadVolatility.EventDriven("Windows PnP device properties changed"));
    public static readonly DeviceInformationDefinition NpuAllLocationInformationReported = new(
        "npu_all_location_information_reported", "npu", "all", "text", "text",
        "npu.same_instance_pnp_location_information_reported_not_inferred_slot", "windows.pnp", ReadVolatility.EventDriven("Windows PnP device properties changed"));
    public static readonly DeviceInformationDefinition NpuAllLocationPathsDeclared = new(
        "npu_all_location_paths_declared", "npu", "all", "text-list", "text",
        "npu.same_instance_pnp_location_paths_declared", "windows.pnp", ReadVolatility.EventDriven("Windows PnP device properties changed"));
}
