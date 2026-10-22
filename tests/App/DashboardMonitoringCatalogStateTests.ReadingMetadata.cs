using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace Resource_Manager_APP.Tests;

public sealed partial class DashboardMonitoringCatalogStateTests
{
    [Fact]
    public void CommittedReadingMetadataUpdatesTheCatalogAndSignalsOnceWithoutReplacingValuesOrOfflineBindings()
    {
        var definition = MetricFieldRegistry.FindByName("gpu_amd_apu_temperature_reported_celsius")!;
        var binding = new MetricDeviceFieldBinding("opaque-gpu", definition, "raw-field", "amd.smu");
        var initial = CreateSnapshot(1, "opaque-gpu", "gpu.0.usage") with
        { FieldBindings = new Dictionary<string, MetricDeviceFieldBinding> { ["raw-field"] = binding, ["offline"] = binding with { RequestAddress = "offline" } } };
        var state = new DashboardMonitoringCatalogState();
        var changes = 0;
        state.Changed += () => changes++;
        state.PublishCatalogProbe(initial);
        var initialRevision = state.Revision;
        var reading = new MetricReadingSource("reading", "gpu.edge_temperature", 10, "available", true, "opaque-gpu", definition.Name);
        var committed = initial with { FieldBindings = new Dictionary<string, MetricDeviceFieldBinding>
            { ["raw-field"] = binding with { Field = definition with { Reading = reading }, RequestAliases = ["gpu.0.temperature"] } } };
        Assert.True(state.PublishReadingMetadata(committed));
        var selectedRevision = state.Revision;
        Assert.NotEqual(initialRevision, selectedRevision);
        Assert.Equal(2, changes);
        Assert.Equal(reading, state.Current!.FieldBindings["raw-field"].Field.Reading);
        Assert.Equal(2, state.Current.FieldBindings.Count);
        Assert.Same(initial.Items, state.Current.Items);
        Assert.Equal(new[] { "gpu.0.temperature" }, state.Current.FieldBindings["raw-field"].RequestAliases);
        Assert.Contains("gpu.0.temperature", state.SelectSubscribable(MetricSampleRequest.ForIds(["gpu.0.temperature"])).Ids);
        Assert.False(state.PublishReadingMetadata(committed));
        Assert.False(state.PublishReadingMetadata(committed with { ConfigurationGeneration = committed.ConfigurationGeneration + 1 }));
        Assert.Equal(selectedRevision, state.Revision);
        Assert.Equal(2, changes);
    }
}
