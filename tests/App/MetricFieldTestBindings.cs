using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.Monitoring;

namespace Resource_Manager_APP.Tests;

internal static class MetricFieldTestBindings
{
    internal static NativeMetricSnapshotRuleCatalogBinding Create(string template, int index = -1)
        => new(0, 0, 0, 0, "fixture-address", "fixture", 0, 0, 0, 0, 0, 0)
        {
            Field = MetricFieldRegistry.FindByTemplate(template),
            DisplayIndex = index
        };
}
