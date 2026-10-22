using ResourceManager.App.Domain.Metrics;

namespace ResourceManager.ReadWrite.Fields;

/// <summary>Copies committed reading metadata into an already discovered catalog; never collects values.</summary>
public static class MetricReadingCatalog
{
    public static HardwareMetricSnapshot Update(HardwareMetricSnapshot catalog, HardwareMetricSnapshot committed)
    {
        if (catalog.WorkspaceIdentity != committed.WorkspaceIdentity
            || catalog.ConfigurationGeneration != committed.ConfigurationGeneration
            || catalog.CatalogGeneration != committed.CatalogGeneration) return catalog;
        Dictionary<string, MetricDeviceFieldBinding>? merged = null;
        foreach (var pair in catalog.FieldBindings)
        {
            if (!committed.FieldBindings.TryGetValue(pair.Key, out var next)
                || pair.Value.DeviceIdentity != next.DeviceIdentity || pair.Value.SourceId != next.SourceId
                || pair.Value.Field.Name != next.Field.Name || pair.Value.Field.Reading == next.Field.Reading) continue;
            merged ??= new(catalog.FieldBindings, StringComparer.OrdinalIgnoreCase);
            merged[pair.Key] = pair.Value with { Field = pair.Value.Field with { Reading = next.Field.Reading } };
        }
        return merged is null ? catalog : catalog with { FieldBindings = merged };
    }
}
