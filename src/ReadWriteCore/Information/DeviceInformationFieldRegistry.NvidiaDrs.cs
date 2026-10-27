namespace ResourceManager.ReadWrite.Information;

public static partial class DeviceInformationFieldRegistry
{
    public static readonly DeviceInformationDefinition GpuNvidiaDriverProfileSettingValue = new(
        "gpu_nvidia_driver_profile_setting_value", "gpu_driver_profile_setting", "nvidia", "object", "typed_value",
        "nvidia.drs.profile_setting_database_value", "nvidia.nvapi.drs", ReadVolatility.EventDriven("NVIDIA DRS profile settings saved"));
}
