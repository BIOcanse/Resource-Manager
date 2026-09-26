namespace ResourceManager.App.Infrastructure.ResourceBreakdown;

internal sealed class ProcessCpuDeltaTracker
{
    private static readonly IReadOnlyDictionary<ProcessInstanceKey, double> EmptyPercentages
        = new Dictionary<ProcessInstanceKey, double>();
    private readonly object sync = new();
    private Dictionary<ProcessInstanceKey, TimeSpan> previousCpu = [];
    private DateTimeOffset? previousCpuAt;

    public IReadOnlyDictionary<ProcessInstanceKey, double> Update(
        bool includeCpu,
        DateTimeOffset capturedAt,
        Dictionary<ProcessInstanceKey, TimeSpan> currentCpu,
        int processorCount)
    {
        ArgumentNullException.ThrowIfNull(currentCpu);
        if (!includeCpu)
        {
            return EmptyPercentages;
        }

        lock (sync)
        {
            var elapsedMs = previousCpuAt is null
                ? 0
                : Math.Max(1, (capturedAt - previousCpuAt.Value).TotalMilliseconds);
            var percentages = new Dictionary<ProcessInstanceKey, double>();
            if (elapsedMs > 0)
            {
                foreach (var pair in currentCpu)
                {
                    if (!previousCpu.TryGetValue(pair.Key, out var previous))
                    {
                        continue;
                    }

                    var deltaMs = (pair.Value - previous).TotalMilliseconds;
                    var percent = deltaMs * 100 / (elapsedMs * Math.Max(1, processorCount));
                    percentages[pair.Key] = double.IsFinite(percent)
                        ? Math.Clamp(percent, 0, 100)
                        : 0;
                }
            }

            previousCpu = currentCpu;
            previousCpuAt = capturedAt;
            return percentages;
        }
    }
}
