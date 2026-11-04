namespace ResourceManager.ReadWrite.Information;

public static partial class DeviceInformationFieldRegistry
{
    public static readonly DeviceInformationDefinition CpuAllDieCountOsReported =
        new("cpu_all_die_count_os_reported", "cpu", "all", "unsigned-integer", "count", "cpu.die_count_os_reported", "windows.processor-die", ReadVolatility.Fixed);
    public static readonly DeviceInformationDefinition CpuAllDieLogicalProcessorAddresses =
        new("cpu_all_die_logical_processor_addresses", "cpu", "all", "processor-addresses", "group-index[]", "cpu.die_logical_processor_addresses", "windows.processor-die", ReadVolatility.Fixed);
    public static readonly DeviceInformationDefinition CpuAllDiePackageDeviceIdentity =
        new("cpu_all_die_package_device_identity", "cpu", "all", "text", "device-id", "cpu.die_package_device_identity", "windows.processor-die", ReadVolatility.Fixed);
}
