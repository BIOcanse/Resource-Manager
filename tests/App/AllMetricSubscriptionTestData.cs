using System.IO.Compression;
using System.Text.Json;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace Resource_Manager_APP.Tests;

internal static class AllMetricSubscriptionTestData
{
    internal static ResourceManager.App.Domain.RuntimeSpecialization.CompiledHostManagerPlan BoundedPlan(
        int items, int sources = 256, int memberships = 8192)
        => HostManagerTestPlanFactory.CreatePlan(root =>
        {
            var role = root["host_recreate"]!["sampling_subscription"]!["roles"]![0]!;
            role["maximum_item_count"] = items;
            role["maximum_due_item_count"] = items;
            role["maximum_source_count"] = sources;
            role["maximum_source_view_count"] = sources;
            role["maximum_expired_source_count"] = sources;
            role["maximum_membership_count"] = memberships;
        });

    internal static HardwareMetricCatalogState RecordedCatalog(int metricCount = 1557)
    {
        var assembly = typeof(AllMetricSubscriptionTestData).Assembly;
        if (metricCount == 1557)
        {
            using var installed = assembly.GetManifestResourceStream(assembly.GetManifestResourceNames()
                .Single(name => name.EndsWith("installed-all-metrics-shape.json", StringComparison.Ordinal)))!;
            using var shape = JsonDocument.Parse(installed);
            var codes = shape.RootElement.GetProperty("definitionFieldCodes").EnumerateArray()
                .Select(code => code.GetInt32()).ToArray();
            Assert.Equal(metricCount, codes.Length);
            return Catalog(codes.Select((code, index) => new MetricDeviceFieldBinding("recorded-device",
                new MetricFieldDefinition((MetricFieldCode)code, $"recorded-field-{index}", MetricResource.System,
                    MetricVendor.All, "count", "recorded", ResourceManager.ReadWrite.ReadVolatility.Sampled),
                $"recorded-metric-{index}", "recorded-source")));
        }
        using var input = assembly.GetManifestResourceStream(assembly.GetManifestResourceNames()
            .Single(name => name.EndsWith("c13-metric-catalog.json.gz", StringComparison.Ordinal)))!;
        using var expanded = new GZipStream(input, CompressionMode.Decompress);
        using var json = JsonDocument.Parse(expanded);
        var handles = json.RootElement.GetProperty("Rules").EnumerateArray()
            .Select(row => row.GetProperty("MetricHandle").GetUInt64()).Distinct().ToArray();
        Assert.Equal(1352, handles.Length);
        var field = new MetricFieldDefinition(MetricFieldCode.Unknown, "recorded-field",
            MetricResource.System, MetricVendor.All, "count", "recorded", ResourceManager.ReadWrite.ReadVolatility.Sampled);
        var bindings = handles.ToDictionary(handle => $"recorded-metric-{handle}",
            handle => new MetricDeviceFieldBinding("recorded-device", field, $"recorded-metric-{handle}", "recorded-source"));
        return Catalog(bindings.Values);
    }

    private static HardwareMetricCatalogState Catalog(IEnumerable<MetricDeviceFieldBinding> fields)
    {
        var bindings = fields.ToDictionary(field => field.RequestAddress);
        var catalog = new HardwareMetricCatalogState();
        catalog.PublishCatalogProbe(new(DateTimeOffset.UtcNow, HardwareMetricTestData.EmptyCpu,
            HardwareMetricTestData.EmptyMemory, HardwareMetricTestData.EmptyVirtualMemory, [],
            HardwareMetricTestData.EmptyGpuInventory, new Dictionary<string, MetricReadingValue>())
            { FieldBindings = bindings });
        return catalog;
    }
}
