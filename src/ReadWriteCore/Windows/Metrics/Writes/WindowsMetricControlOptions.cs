using ResourceManager.ReadWrite.Helpers;

namespace ResourceManager.ReadWrite.Windows.Metrics.Writes;

/// <summary>Explicit durable storage/environment inputs for original control
/// resources. Construction performs no Probe, helper start, consent waiver,
/// installer or setter. Missing paths declare missing dependencies at discovery.</summary>
public sealed record WindowsMetricControlOptions(string BaselineDirectory)
{
    public string? HardwareBridgeExecutablePath { get; init; }
    public HardwareBridgeProcessOptions? HardwareBridgeOptions { get; init; }
    public Func<bool> IsFixedChassis { get; init; } = static () => false;
    public Func<CancellationToken, Task<bool>> IsIntelOverclockAccepted { get; init; }
        = static _ => Task.FromResult(false);
}
