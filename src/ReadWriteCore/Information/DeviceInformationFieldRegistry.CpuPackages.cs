namespace ResourceManager.ReadWrite.Information;

public static partial class DeviceInformationFieldRegistry
{
    public static readonly DeviceInformationDefinition CpuAllPackageCountReported =
        new("cpu_all_package_count_reported", "cpu", "all", "unsigned-integer", "count", "cpu.package_count_reported", "windows.processor-package");
    public static readonly DeviceInformationDefinition CpuAllPackageLogicalProcessorAddresses =
        new("cpu_all_package_logical_processor_addresses", "cpu", "all", "processor-addresses", "group-index[]", "cpu.package_logical_processor_addresses", "windows.processor-package");
}
