using ResourceManager.App.Infrastructure.Control.Writers;

namespace ResourceManager.App.Infrastructure.Monitoring;

/// <summary>Optional SMU reference from the current run. Absence keeps the caller's nominal-frequency fallback.</summary>
public sealed class CpuMaxBoostFrequency(HardwareBridgeClient bridge)
{
    public double? Megahertz => bridge.ReadCurrentReadings()?.MaxBoostFrequencyMhz is > 0 and < 10_000 and var reported
        ? reported : null;
}
