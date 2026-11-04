using System.Collections.Immutable;
using ResourceManager.App.Domain.Metrics;

namespace ResourceManager.ReadWrite.Windows.Metrics.Writes;

public enum WindowsMetricControlFamily { IntelCpu, AmdCpu, AmdDesktopGpu, IntelIntegratedGpu, AmdIntegratedGpu }
/// <summary>Unit spelling/payload adaptation only: count is an original CO step,
/// never a voltage conversion. Numeric values keep their original magnitude.</summary>
public enum WindowsMetricControlUnitConversion
{
    Identity, CountToStep, MultiplierToRawMultiplier, CelsiusToRawCelsius, BooleanToToggle
}

public sealed record WindowsMetricControlBinding(MetricFieldDefinition Definition, string RawCapability,
    WindowsMetricControlFamily Family, bool Implemented, FieldWriteContract DeclaredRange,
    string RawUnit, string SourceId, FieldWriteEvidence Evidence)
{
    public WindowsMetricControlUnitConversion UnitConversion { get; init; }
    public ImmutableArray<string> ReadDependencies { get; init; } = [];
    public ImmutableArray<string> WriteDependencies { get; init; } = [];
}

/// <summary>Pure explicit managed field-to-original-control declarations. Matching
/// does not admit a device, probe hardware, open a helper, or retain current values.</summary>
public static class WindowsMetricControlBindings
{
    public static IReadOnlyList<WindowsMetricControlBinding> All { get; } = Array.AsReadOnly(new WindowsMetricControlBinding[]
    {
        Create("cpu_intel_requested_pl1_power_limit_watts", "cpu.power-limit", WindowsMetricControlFamily.IntelCpu, true,
            "W", "control.intel.msr", FieldWriteEvidence.MaskedRegisterReadback, "single-host CPU package RAPL MSR 0x610", null,
            WindowsMetricControlUnitConversion.Identity, ["intel-msr-pawnio-provider"], ["intel-msr-pawnio-provider"]),
        Create("cpu_intel_requested_pl1_time_window_seconds", "cpu.power-limit-window", WindowsMetricControlFamily.IntelCpu, true,
            "s", "control.intel.msr", FieldWriteEvidence.MaskedRegisterReadback, "single-host CPU package RAPL MSR 0x610", null,
            WindowsMetricControlUnitConversion.Identity, ["intel-msr-pawnio-provider"], ["intel-msr-pawnio-provider"]),
        Create("cpu_intel_requested_pl2_power_limit_watts", "cpu.short-power-limit", WindowsMetricControlFamily.IntelCpu, true,
            "W", "control.intel.msr", FieldWriteEvidence.MaskedRegisterReadback, "single-host CPU package RAPL MSR 0x610", null,
            WindowsMetricControlUnitConversion.Identity, ["intel-msr-pawnio-provider"], ["intel-msr-pawnio-provider"]),
        Create("cpu_intel_requested_pl2_time_window_seconds", "cpu.short-power-limit-window", WindowsMetricControlFamily.IntelCpu, true,
            "s", "control.intel.msr", FieldWriteEvidence.MaskedRegisterReadback, "single-host CPU package RAPL MSR 0x610", null,
            WindowsMetricControlUnitConversion.Identity, ["intel-msr-pawnio-provider"], ["intel-msr-pawnio-provider"]),
        Create("cpu_intel_requested_core_voltage_offset_millivolts", "cpu.core-voltage-offset", WindowsMetricControlFamily.IntelCpu, true,
            "mV", "control.intel.msr", FieldWriteEvidence.EncodedMailboxReadback, "single-host CPU OC mailbox plane core (not individual cores/GPU LUID)", null,
            WindowsMetricControlUnitConversion.Identity, ["intel-msr-pawnio-provider"], ["intel-msr-pawnio-provider"]),
        Create("cpu_intel_requested_igpu_voltage_offset_millivolts", "cpu.igpu-voltage-offset", WindowsMetricControlFamily.IntelCpu, true,
            "mV", "control.intel.msr", FieldWriteEvidence.EncodedMailboxReadback, "single-host CPU OC mailbox plane igpu (not individual cores/GPU LUID)", null,
            WindowsMetricControlUnitConversion.Identity, ["intel-msr-pawnio-provider"], ["intel-msr-pawnio-provider"]),
        Create("cpu_intel_requested_cache_voltage_offset_millivolts", "cpu.cache-voltage-offset", WindowsMetricControlFamily.IntelCpu, true,
            "mV", "control.intel.msr", FieldWriteEvidence.EncodedMailboxReadback, "single-host CPU OC mailbox plane cache (not individual cores/GPU LUID)", null,
            WindowsMetricControlUnitConversion.Identity, ["intel-msr-pawnio-provider"], ["intel-msr-pawnio-provider"]),
        Create("cpu_intel_requested_system_agent_voltage_offset_millivolts", "cpu.system-agent-voltage-offset", WindowsMetricControlFamily.IntelCpu, true,
            "mV", "control.intel.msr", FieldWriteEvidence.EncodedMailboxReadback, "single-host CPU OC mailbox plane system_agent (not individual cores/GPU LUID)", null,
            WindowsMetricControlUnitConversion.Identity, ["intel-msr-pawnio-provider"], ["intel-msr-pawnio-provider"]),
        Create("cpu_amd_requested_stapm_power_limit_watts", "cpu.power-limit", WindowsMetricControlFamily.AmdCpu, true,
            "W", "control.amd.hardware-bridge", FieldWriteEvidence.SettingReadbackTolerance, "single-host AMD CPU SMU power domain", null,
            WindowsMetricControlUnitConversion.Identity, ["hardware-bridge"], ["hardware-bridge"]),
        Create("cpu_amd_requested_slow_power_limit_watts", "cpu.slow-power-limit", WindowsMetricControlFamily.AmdCpu, true,
            "W", "control.amd.hardware-bridge", FieldWriteEvidence.SettingReadbackTolerance, "single-host AMD CPU SMU power domain", null,
            WindowsMetricControlUnitConversion.Identity, ["hardware-bridge"], ["hardware-bridge"]),
        Create("cpu_amd_requested_fast_power_limit_watts", "cpu.fast-power-limit", WindowsMetricControlFamily.AmdCpu, true,
            "W", "control.amd.hardware-bridge", FieldWriteEvidence.SettingReadbackTolerance, "single-host AMD CPU SMU power domain", null,
            WindowsMetricControlUnitConversion.Identity, ["hardware-bridge"], ["hardware-bridge"]),
        Create("cpu_amd_requested_pbo_scalar_multiplier", "cpu.pbo-scalar", WindowsMetricControlFamily.AmdCpu, true,
            "×", "control.amd.hardware-bridge", FieldWriteEvidence.AcceptedOnly, "single-host AMD CPU PBO scalar", null,
            WindowsMetricControlUnitConversion.MultiplierToRawMultiplier, [], ["hardware-bridge"]),
        Create("cpu_amd_requested_curve_optimizer_counts", "cpu.curve-optimizer", WindowsMetricControlFamily.AmdCpu, true,
            "档", "control.amd.hardware-bridge", FieldWriteEvidence.SettingReadbackTolerance, "single-host AMD CPU all-core CO; not per-core selector", null,
            WindowsMetricControlUnitConversion.CountToStep, ["hardware-bridge"], ["hardware-bridge"]),
        Create("cpu_amd_requested_tdc_vdd_current_limit_amperes", "cpu.tdc-limit", WindowsMetricControlFamily.AmdCpu, false,
            "A", "control.amd.hardware-bridge", FieldWriteEvidence.None, "single-host AMD CPU SMU domain", null,
            WindowsMetricControlUnitConversion.Identity, [], ["hardware-bridge"]),
        Create("cpu_amd_requested_edc_vdd_current_limit_amperes", "cpu.edc-limit", WindowsMetricControlFamily.AmdCpu, false,
            "A", "control.amd.hardware-bridge", FieldWriteEvidence.None, "single-host AMD CPU SMU domain", null,
            WindowsMetricControlUnitConversion.Identity, [], ["hardware-bridge"]),
        Create("cpu_amd_requested_temperature_limit_celsius", "cpu.temperature-limit", WindowsMetricControlFamily.AmdCpu, false,
            "°C", "control.amd.hardware-bridge", FieldWriteEvidence.None, "single-host AMD CPU SMU domain", null,
            WindowsMetricControlUnitConversion.CelsiusToRawCelsius, [], ["hardware-bridge"]),
        Create("cpu_intel_requested_pl1_mmio_power_limit_watts", "cpu.power-limit-mmio", WindowsMetricControlFamily.IntelCpu, false,
            "W", "control.intel.msr", FieldWriteEvidence.None, "unbound; MMIO path not implemented", null,
            WindowsMetricControlUnitConversion.Identity, [], ["intel-msr-pawnio-provider"]),
        Create("gpu_amd_requested_power_limit_offset_percent", "gpu.power-limit-offset", WindowsMetricControlFamily.AmdDesktopGpu, true,
            "%", "control.amd.adlx", FieldWriteEvidence.ExactIntegerAndPnpReadback, "exact admitted fixed/discrete AMD GPU PNP+LUID", null,
            WindowsMetricControlUnitConversion.Identity, ["amd-adlx-provider"], ["amd-adlx-provider"]),
        Create("gpu_amd_requested_graphics_clock_minimum_mhz", "gpu.core-clock-minimum", WindowsMetricControlFamily.AmdDesktopGpu, true,
            "MHz", "control.amd.adlx", FieldWriteEvidence.ExactIntegerAndPnpReadback, "exact admitted fixed/discrete AMD GPU PNP+LUID", "known GFX mode; min frequency remains MHz in both modes",
            WindowsMetricControlUnitConversion.Identity, ["amd-adlx-provider"], ["amd-adlx-provider"]),
        Create("gpu_amd_requested_graphics_clock_maximum_mhz", "gpu.core-clock-maximum", WindowsMetricControlFamily.AmdDesktopGpu, true,
            "MHz", "control.amd.adlx", FieldWriteEvidence.ExactIntegerAndPnpReadback, "exact admitted fixed/discrete AMD GPU PNP+LUID", "Absolute only",
            WindowsMetricControlUnitConversion.Identity, ["amd-adlx-provider"], ["amd-adlx-provider"]),
        Create("gpu_amd_requested_core_voltage_millivolts", "gpu.core-voltage", WindowsMetricControlFamily.AmdDesktopGpu, true,
            "mV", "control.amd.adlx", FieldWriteEvidence.ExactIntegerAndPnpReadback, "exact admitted fixed/discrete AMD GPU PNP+LUID", "Absolute only",
            WindowsMetricControlUnitConversion.Identity, ["amd-adlx-provider"], ["amd-adlx-provider"]),
        Create("gpu_amd_requested_memory_clock_maximum_mhz", "gpu.memory-clock-maximum", WindowsMetricControlFamily.AmdDesktopGpu, true,
            "MHz", "control.amd.adlx", FieldWriteEvidence.ExactIntegerAndPnpReadback, "exact admitted fixed/discrete AMD GPU PNP+LUID", null,
            WindowsMetricControlUnitConversion.Identity, ["amd-adlx-provider"], ["amd-adlx-provider"]),
        Create("gpu_amd_requested_graphics_clock_offset_mhz", "gpu.core-clock-offset", WindowsMetricControlFamily.AmdDesktopGpu, true,
            "MHz", "control.amd.adlx", FieldWriteEvidence.ExactIntegerAndPnpReadback, "exact admitted fixed/discrete AMD GPU PNP+LUID", "Offset only; max GFX tuning offset",
            WindowsMetricControlUnitConversion.Identity, ["amd-adlx-provider"], ["amd-adlx-provider"]),
        Create("gpu_amd_requested_core_voltage_offset_millivolts", "gpu.core-voltage-offset", WindowsMetricControlFamily.AmdDesktopGpu, true,
            "mV", "control.amd.adlx", FieldWriteEvidence.ExactIntegerAndPnpReadback, "exact admitted fixed/discrete AMD GPU PNP+LUID", "Offset only",
            WindowsMetricControlUnitConversion.Identity, ["amd-adlx-provider"], ["amd-adlx-provider"]),
        Create("fan_amd_requested_minimum_speed_rpm", "fan.minimum-rpm", WindowsMetricControlFamily.AmdDesktopGpu, true,
            "RPM", "control.amd.adlx", FieldWriteEvidence.ExactIntegerAndPnpReadback, "GPU-board fan tuning group attached to exact admitted AMD GPU; not individual tachometer fan", null,
            WindowsMetricControlUnitConversion.Identity, ["amd-adlx-provider"], ["amd-adlx-provider"]),
        Create("fan_amd_requested_target_speed_rpm", "fan.target-rpm", WindowsMetricControlFamily.AmdDesktopGpu, true,
            "RPM", "control.amd.adlx", FieldWriteEvidence.ExactIntegerAndPnpReadback, "GPU-board fan tuning group attached to exact admitted AMD GPU; not individual tachometer fan", null,
            WindowsMetricControlUnitConversion.Identity, ["amd-adlx-provider"], ["amd-adlx-provider"]),
        Create("fan_amd_requested_zero_rpm_enabled", "fan.zero-rpm", WindowsMetricControlFamily.AmdDesktopGpu, true,
            "", "control.amd.adlx", FieldWriteEvidence.ExactIntegerAndPnpReadback, "GPU-board fan tuning group attached to exact admitted AMD GPU; not individual tachometer fan", null,
            WindowsMetricControlUnitConversion.BooleanToToggle, ["amd-adlx-provider"], ["amd-adlx-provider"]),
        Create("gpu_intel_requested_core_clock_offset_mhz", "gpu.core-clock-offset", WindowsMetricControlFamily.IntelIntegratedGpu, true,
            "MHz", "control.intel.igcl", FieldWriteEvidence.SettingReadbackTolerance, "exact Intel integrated GPU LUID; original consent prerequisite", null,
            WindowsMetricControlUnitConversion.Identity, ["intel-graphics-driver"], ["intel-graphics-driver"]),
        Create("gpu_amd_requested_curve_optimizer_counts", "gpu.curve-optimizer", WindowsMetricControlFamily.AmdIntegratedGpu, true,
            "档", "control.amd.igpu.hardware-bridge", FieldWriteEvidence.ConditionalReadbackElseAccepted, "single-host AMD APU integrated GPU SMU domain, associated with unique fresh integrated GPU; no LUID selector in helper", null,
            WindowsMetricControlUnitConversion.CountToStep, ["hardware-bridge"], ["hardware-bridge"]),
        Create("cpu_intel_requested_temperature_limit_reduction_celsius", "cpu.temperature-offset", WindowsMetricControlFamily.IntelCpu, false,
            "°C", "control.intel.msr", FieldWriteEvidence.None, "single-host Intel CPU thermal limit reduction; positive downward adjustment, not absolute temperature", null,
            WindowsMetricControlUnitConversion.CelsiusToRawCelsius, [], ["intel-msr-pawnio-provider"]),
    });

    public static WindowsMetricControlBinding? FindByName(string name)
        => All.FirstOrDefault(entry => string.Equals(entry.Definition.Name, name, StringComparison.Ordinal));

    public static WindowsMetricControlBinding? TryFind(MetricDeviceFieldBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        var entry = FindByName(binding.Field.Name);
        if (entry is null) return null;
        var expected = entry.Definition;
        var actual = binding.Field;
        return actual.Code == expected.Code && actual.Resource == expected.Resource
            && actual.Vendor == expected.Vendor && actual.Unit == expected.Unit
            && actual.ValueKind == expected.ValueKind && binding.SourceId == entry.SourceId
            && actual.RequestTemplate == expected.RequestTemplate && !actual.HasLegacyAddress
            ? entry : null;
    }

    private static WindowsMetricControlBinding Create(string name, string capability,
        WindowsMetricControlFamily family, bool implemented, string rawUnit, string source,
        FieldWriteEvidence evidence, string scope, string? modeGate,
        WindowsMetricControlUnitConversion conversion, ImmutableArray<string> readDependencies,
        ImmutableArray<string> writeDependencies)
        => new(MetricFieldRegistry.FindByName(name)
                ?? throw new InvalidOperationException("managed-control-definition-missing:" + name),
            capability, family, implemented,
            WindowsMetricControlCapabilityDefinitions.CreateContract(family, capability, scope, evidence, modeGate),
            rawUnit, source, evidence)
        {
            UnitConversion = conversion,
            ReadDependencies = readDependencies,
            WriteDependencies = writeDependencies
        };
}
