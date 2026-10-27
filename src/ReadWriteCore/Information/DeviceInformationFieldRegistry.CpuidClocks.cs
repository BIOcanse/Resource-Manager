namespace ResourceManager.ReadWrite.Information;

public static partial class DeviceInformationFieldRegistry
{
    public static readonly DeviceInformationDefinition CpuIntelProcessorBaseFrequencyDeclaredMhz =
        new("cpu_intel_processor_base_frequency_declared_mhz", "cpu", "intel", "unsigned-integer", "MHz", "cpu.intel_processor_base_frequency_specification_not_counter_calibration", "intel.cpuid.16", ReadVolatility.Fixed);
    public static readonly DeviceInformationDefinition CpuIntelProcessorMaximumFrequencyDeclaredMhz =
        new("cpu_intel_processor_maximum_frequency_declared_mhz", "cpu", "intel", "unsigned-integer", "MHz", "cpu.intel_processor_maximum_frequency_specification_not_measured_boost", "intel.cpuid.16", ReadVolatility.Fixed);
    public static readonly DeviceInformationDefinition CpuIntelBusReferenceFrequencyDeclaredMhz =
        new("cpu_intel_bus_reference_frequency_declared_mhz", "cpu", "intel", "unsigned-integer", "MHz", "cpu.intel_bus_reference_frequency_specification_not_actual_bclk", "intel.cpuid.16", ReadVolatility.Fixed);
    public static readonly DeviceInformationDefinition CpuIntelNominalCoreCrystalFrequencyDeclaredHz =
        new("cpu_intel_nominal_core_crystal_frequency_declared_hz", "cpu", "intel", "unsigned-integer", "Hz", "cpu.intel_nominal_core_crystal_frequency_not_bus_or_core_frequency", "intel.cpuid.15", ReadVolatility.Fixed);
    public static readonly DeviceInformationDefinition CpuIntelTscToCoreCrystalRatioDeclared =
        new("cpu_intel_tsc_to_core_crystal_ratio_declared", "cpu", "intel", "rational", "ratio", "cpu.intel_tsc_to_core_crystal_ratio_declared", "intel.cpuid.15", ReadVolatility.Fixed);
}
