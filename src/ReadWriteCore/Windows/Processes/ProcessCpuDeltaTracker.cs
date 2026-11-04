namespace ResourceManager.App.Infrastructure.ResourceBreakdown;

/// <summary>Tracks the original process CPU-time deltas using complete identity-verified capture batches.</summary>
public sealed class ProcessCpuDeltaTracker
{
    private static readonly IReadOnlyDictionary<ProcessInstanceKey, double> EmptyPercentages
        = new Dictionary<ProcessInstanceKey, double>();
    private readonly object sync = new();
    private Dictionary<ProcessInstanceKey, TimeSpan> previousCpu = [];
    private DateTimeOffset? previousCpuAt;

    /// <summary>Updates the original CPU baseline and returns percentages. When includeCpu is true,
    /// takes ownership of the complete currentCpu dictionary without copying; the caller must not
    /// mutate it after submission. When false, the input dictionary is not retained and
    /// the existing baseline is unchanged.</summary>
    public IReadOnlyDictionary<ProcessInstanceKey, double> Update(
        bool includeCpu,
        DateTimeOffset capturedAt,
        Dictionary<ProcessInstanceKey, TimeSpan> currentCpu,
        int processorCount)
        => UpdateInterval(includeCpu, capturedAt, currentCpu, processorCount).Percentages;

    public readonly record struct IntervalResult(DateTimeOffset CapturedAt, TimeSpan? Interval,
        IReadOnlyDictionary<ProcessInstanceKey, double> Percentages);

    /// <summary>Uses capture-start elapsed time and the explicit processorCount denominator.
    /// When includeCpu is true, takes ownership of the complete currentCpu dictionary without
    /// copying; the caller must not mutate it after submission. Non-CPU calls retain no input
    /// dictionary and leave the existing baseline unchanged.</summary>
    public IntervalResult UpdateInterval(bool includeCpu, DateTimeOffset capturedAt,
        Dictionary<ProcessInstanceKey, TimeSpan> currentCpu, int processorCount)
    {
        ArgumentNullException.ThrowIfNull(currentCpu);
        if (!includeCpu)
        {
            return new(capturedAt, null, EmptyPercentages);
        }

        lock (sync)
        {
            var interval = previousCpuAt is { } previousAt && capturedAt > previousAt
                ? capturedAt - previousAt : (TimeSpan?)null;
            var percentages = new Dictionary<ProcessInstanceKey, double>();
            if (interval is { } elapsed && processorCount > 0)
            {
                foreach (var pair in currentCpu)
                {
                    if (!previousCpu.TryGetValue(pair.Key, out var previous))
                    {
                        continue;
                    }

                    var deltaMs = (pair.Value - previous).TotalMilliseconds;
                    if (deltaMs < 0) continue;
                    var percent = deltaMs * 100 / (elapsed.TotalMilliseconds * processorCount);
                    if (double.IsFinite(percent)) percentages[pair.Key] = Math.Clamp(percent, 0, 100);
                }
            }

            previousCpu = currentCpu;
            previousCpuAt = capturedAt;
            return new(capturedAt, interval, percentages);
        }
    }
}
