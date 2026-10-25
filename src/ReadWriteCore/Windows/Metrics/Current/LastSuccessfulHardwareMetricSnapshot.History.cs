using System.Collections.Immutable;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.RuntimeSpecialization;

namespace ResourceManager.App.Infrastructure.Monitoring;

public sealed partial class LastSuccessfulHardwareMetricSnapshot
{
    private readonly Dictionary<string, DataHistoryBuffer<HardwareMetricHistorySample>> histories =
        new(StringComparer.OrdinalIgnoreCase);
    private ImmutableDictionary<string, ImmutableArray<HardwareMetricHistorySample>> historyView =
        ImmutableDictionary<string, ImmutableArray<HardwareMetricHistorySample>>.Empty
            .WithComparers(StringComparer.OrdinalIgnoreCase);

    public void ConfigureHistory(IEnumerable<HistoryRequirement> requirements)
        => ConfigureHistory(CompiledDataHistoryPlan.Compile(requirements));

    public void ConfigureHistory(CompiledDataHistoryPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        lock (sync)
        {
            var state = ReadState();
            var requested = plan.Requirements.ToDictionary(
                static requirement => requirement.DataItem,
                static requirement => requirement.RetainedRounds,
                StringComparer.OrdinalIgnoreCase);
            foreach (var item in histories.Keys.Except(requested.Keys, StringComparer.OrdinalIgnoreCase).ToArray())
            {
                histories.Remove(item);
                historyView = historyView.Remove(item);
            }
            foreach (var (item, rounds) in requested)
            {
                if (histories.TryGetValue(item, out var existing))
                {
                    existing.Resize(rounds);
                }
                else
                {
                    existing = new DataHistoryBuffer<HardwareMetricHistorySample>(rounds);
                    histories.Add(item, existing);
                    if (state.Snapshot is { } snapshot
                        && TryReadCompletedHistorySample(snapshot, state.Datasets, item, out var sample))
                    {
                        existing.Append(sample);
                    }
                }
                historyView = historyView.SetItem(item, existing.Read());
            }
        }
    }

    private HardwareMetricSnapshot PublishHistory(
        HardwareMetricSnapshot snapshot,
        IReadOnlyDictionary<string, DatasetPublication> datasets,
        IReadOnlySet<string> completedItems)
    {
        foreach (var (item, history) in histories)
        {
            if (!completedItems.Contains(item)
                || !TryReadCompletedHistorySample(snapshot, datasets, item, out var sample))
            {
                continue;
            }
            history.Append(sample);
            historyView = historyView.SetItem(item, history.Read());
        }
        return snapshot with { History = historyView };
    }

    private static bool TryReadCompletedHistorySample(
        HardwareMetricSnapshot snapshot,
        IReadOnlyDictionary<string, DatasetPublication> datasets,
        string item,
        out HardwareMetricHistorySample sample)
    {
        if (!datasets.TryGetValue(item, out var publication))
        {
            sample = default;
            return false;
        }
        var value = publication.PayloadSnapshot?.Items.GetValueOrDefault(item);
        double? numericValue = value?.NumericValue;
        if (numericValue is null && value?.IntegerValue is { } integer)
            numericValue = integer.Negative
                ? (double)unchecked((long)integer.LowBits)
                : (double)(((UInt128)integer.HighBits << 64) | integer.LowBits);
        if (item.Equals(MetricDatasetIds.SystemCpuUsage, StringComparison.OrdinalIgnoreCase))
        {
            numericValue = publication.PayloadSnapshot?.Cpu is { IsUsageAvailable: true } cpu
                ? cpu.UsagePercent
                : null;
        }
        sample = new HardwareMetricHistorySample(publication.LastAttemptAt, numericValue);
        return true;
    }

}
