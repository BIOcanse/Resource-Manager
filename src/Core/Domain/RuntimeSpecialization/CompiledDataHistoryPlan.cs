using System.Collections.Frozen;
using System.Collections.Immutable;
using ResourceManager.App.Domain.Monitoring;

namespace ResourceManager.App.Domain.RuntimeSpecialization;

public sealed class CompiledDataHistoryPlan
{
    private readonly FrozenDictionary<string, int> retainedRounds;

    private CompiledDataHistoryPlan(Dictionary<string, int> requirements)
    {
        retainedRounds = requirements.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        Requirements = requirements
            .OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(static pair => new HistoryRequirement(pair.Key, pair.Value))
            .ToImmutableArray();
    }

    public ImmutableArray<HistoryRequirement> Requirements { get; }

    public static CompiledDataHistoryPlan Empty { get; } = Compile([]);

    public int GetRetainedRounds(string dataItem)
        => retainedRounds.GetValueOrDefault(dataItem, 1);

    public static CompiledDataHistoryPlan Compile(IEnumerable<HistoryRequirement> requirements)
    {
        ArgumentNullException.ThrowIfNull(requirements);
        var merged = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var requirement in requirements)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(requirement.DataItem);
            ArgumentOutOfRangeException.ThrowIfLessThan(requirement.RetainedRounds, 1);
            var dataItem = requirement.DataItem.Trim();
            merged[dataItem] = Math.Max(
                merged.GetValueOrDefault(dataItem),
                requirement.RetainedRounds);
        }
        return new CompiledDataHistoryPlan(merged);
    }
}
