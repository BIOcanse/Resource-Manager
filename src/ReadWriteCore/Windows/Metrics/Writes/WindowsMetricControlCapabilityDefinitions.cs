namespace ResourceManager.ReadWrite.Windows.Metrics.Writes;

/// <summary>Original pure WindowsControlObjectCatalog CPU/iGPU declarations.
/// The application retains permissions and chassis policy. ADLX bounds/defaults
/// must come from fresh original tuning reads; this table never probes hardware.</summary>
public static class WindowsMetricControlCapabilityDefinitions
{
    private readonly record struct Bounds(double? Minimum, double? Maximum, double? Step, double? Default);

    public static FieldWriteContract CreateContract(WindowsMetricControlFamily family, string rawCapability,
        string physicalScope, FieldWriteEvidence evidence, string? modeGate = null)
    {
        Bounds bounds = (family, rawCapability) switch
        {
            (WindowsMetricControlFamily.IntelCpu, "cpu.power-limit") => new(5, 200, 1, null),
            (WindowsMetricControlFamily.IntelCpu, "cpu.power-limit-window") => new(1, 128, 1, null),
            (WindowsMetricControlFamily.IntelCpu, "cpu.short-power-limit") => new(5, 300, 1, null),
            (WindowsMetricControlFamily.IntelCpu, "cpu.short-power-limit-window") => new(1, 128, 1, null),
            (WindowsMetricControlFamily.IntelCpu, "cpu.core-voltage-offset") => new(-250, 100, 1, 0),
            (WindowsMetricControlFamily.IntelCpu, "cpu.igpu-voltage-offset") => new(-250, 100, 1, 0),
            (WindowsMetricControlFamily.IntelCpu, "cpu.cache-voltage-offset") => new(-250, 100, 1, 0),
            (WindowsMetricControlFamily.IntelCpu, "cpu.system-agent-voltage-offset") => new(-250, 100, 1, 0),
            (WindowsMetricControlFamily.AmdCpu, "cpu.power-limit") => new(5, 200, 1, null),
            (WindowsMetricControlFamily.AmdCpu, "cpu.slow-power-limit") => new(5, 200, 1, null),
            (WindowsMetricControlFamily.AmdCpu, "cpu.fast-power-limit") => new(5, 200, 1, null),
            (WindowsMetricControlFamily.AmdCpu, "cpu.pbo-scalar") => new(1, 10, 1, 1),
            (WindowsMetricControlFamily.AmdCpu, "cpu.curve-optimizer") => new(-30, 10, 1, 0),
            (WindowsMetricControlFamily.AmdCpu, "cpu.tdc-limit") => new(10, 300, 1, null),
            (WindowsMetricControlFamily.AmdCpu, "cpu.edc-limit") => new(10, 400, 1, null),
            (WindowsMetricControlFamily.AmdCpu, "cpu.temperature-limit") => new(45, 105, 1, null),
            (WindowsMetricControlFamily.IntelCpu, "cpu.power-limit-mmio") => new(5, 200, 1, null),
            (WindowsMetricControlFamily.IntelIntegratedGpu, "gpu.core-clock-offset") => new(-200, 200, 5, 0),
            (WindowsMetricControlFamily.AmdIntegratedGpu, "gpu.curve-optimizer") => new(-30, 10, 1, 0),
            (WindowsMetricControlFamily.IntelCpu, "cpu.temperature-offset") => new(0, 30, 1, 0),
            // Exact desktop GPU capabilities are admitted by the explicit bindings
            // table; null bounds are required until the original ADLX read succeeds.
            (WindowsMetricControlFamily.AmdDesktopGpu, "gpu.power-limit-offset" or "gpu.core-clock-minimum"
                or "gpu.core-clock-maximum" or "gpu.core-voltage" or "gpu.memory-clock-maximum"
                or "gpu.core-clock-offset" or "gpu.core-voltage-offset" or "fan.minimum-rpm"
                or "fan.target-rpm" or "fan.zero-rpm") => new(null, null, null, null),
            _ => throw new ArgumentException("managed-control-capability-unknown", nameof(rawCapability))
        };
        return new(physicalScope, bounds.Minimum, bounds.Maximum, bounds.Step, bounds.Default, evidence,
            family == WindowsMetricControlFamily.AmdDesktopGpu
                ? "fresh-original-adlx-tuning-range-required"
                : "original-declared-application-bounds") { ModeGate = modeGate };
    }
}
