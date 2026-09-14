using ResourceManager.App.Domain.Metrics;

namespace ResourceManager.App.Application.Metrics;

public static partial class MetricCatalog
{
    public static HardwareMetricSnapshot ApplyPresentation(HardwareMetricSnapshot snapshot)
    {
        var definitions = new Dictionary<string, MetricDefinition>(StringComparer.Ordinal);
        foreach (var definition in FromSnapshot(snapshot))
        {
            definitions[definition.Id] = definition;
        }

        var items = snapshot.Items.ToDictionary(
            static pair => pair.Key,
            pair => definitions.TryGetValue(pair.Key, out var definition)
                ? pair.Value with
                {
                    Label = definition.Label,
                    Group = definition.Group,
                    Unit = definition.Unit
                }
                : pair.Value,
            StringComparer.Ordinal);

        return snapshot with { Items = items };
    }
}
