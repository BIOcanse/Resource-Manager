using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Domain.Messages;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace Resource_Manager_APP.Tests;

public sealed partial class HardwareMetricCatalogStateTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void AliasMetadataBelongsToTheCurrentValueOrPreferredReadingRegardlessOfBindingOrder(
        bool hasCurrentValue, bool reverseBindings)
    {
        const string alias = "cpu.temperature";
        var preferred = CatalogBinding("cpu_intel_package_dts_temperature", "intel.msr",
            "a-preferred", alias, "available", preferred: true);
        var actual = CatalogBinding("cpu_all_temperature_reported_celsius", "windows.wmi",
            "z-actual", alias, "available", preferred: false);
        var bindings = reverseBindings ? new[] { actual, preferred } : new[] { preferred, actual };
        var items = new Dictionary<string, MetricReadingValue>();
        if (hasCurrentValue)
            items[alias] = new(alias, 58, "°C")
            {
                FieldCode = actual.Field.Code, FieldName = actual.Field.Name,
                DeviceIdentity = actual.DeviceIdentity, SourceId = actual.SourceId, ValueKind = actual.Field.ValueKind
            };
        var snapshot = CreateSnapshot(1, "gpu", []) with
        {
            Items = items,
            FieldBindings = bindings.ToDictionary(static binding => binding.RequestAddress)
        };
        var definition = MetricCatalog.FromSnapshot(snapshot).Single(item => item.Id == alias);
        var expected = hasCurrentValue ? actual : preferred;
        Assert.True(definition.Selectable);
        Assert.Equal(expected.SourceId, definition.SourceId);
        Assert.Equal(expected.Field.Code, definition.FieldCode);
        Assert.Equal(expected.Field.Name, definition.FieldName);
        Assert.Equal(expected.DeviceIdentity, definition.DeviceIdentity);
        Assert.Equal(expected.Field.ValueKind, definition.ValueKind);
        Assert.Equal("°C", definition.Unit);
    }

    [Theory]
    [InlineData("motherboard_all_vrm_temperature_celsius", "system.vrmTemperature", "memory_all_temperature_reported_celsius", "hardware-monitor.core")]
    [InlineData("motherboard_all_chipset_temperature_celsius", "system.chipsetTemperature", "memory_all_temperature_reported_celsius", "hardware-monitor.core")]
    [InlineData("cpu_amd_stapm_power_watts", "cpu.stapmPower", "cpu_amd_actual_power_watts", "amd.smu")]
    [InlineData("cpu_amd_package_power_reported_watts", "cpu.packagePower", "cpu_amd_actual_power_watts", "amd.smu")]
    public void RunningSourceAllowsRequestingUnverifiedFieldsWithoutClaimingSupport(
        string fieldName, string alias, string availableFieldName, string source)
    {
        var initial = CreateSnapshot(1, "gpu", []);
        initial = initial with { Cpu = initial.Cpu with { Name = "AMD Ryzen test" } };
        var target = CatalogBinding(fieldName, source, "target", alias, "unverified", preferred: false);
        var sibling = CatalogBinding(availableFieldName, source, "sibling", "unrelated", "available", preferred: true);
        initial = initial with { FieldBindings = new Dictionary<string, MetricDeviceFieldBinding>
            { ["target"] = target, ["sibling"] = sibling } };
        var definition = MetricCatalog.FromSnapshot(initial).Single(item => item.Id == alias);
        Assert.True(definition.Selectable);
        Assert.Null(definition.RequiredComponentId);
        Assert.Null(definition.DisabledReason);
        Assert.Equal("unverified", initial.FieldBindings["target"].Field.Reading!.SourceAvailability);
        Assert.Empty(initial.Items);

        // An installed, unrelated provider is not evidence for this one.
        var unrelated = initial with { FieldBindings = new Dictionary<string, MetricDeviceFieldBinding>
            { ["target"] = target, ["sibling"] = sibling with { SourceId = "other-provider" } } };
        Assert.Equal(BackendMessageCodes.Metric.NeedsComponent,
            MetricCatalog.FromSnapshot(unrelated).Single(item => item.Id == alias).DisabledReason!.Code);
    }

    [Theory]
    [InlineData("unsupported", false, BackendMessageCodes.Metric.NotExposed)]
    [InlineData("unverified", false, BackendMessageCodes.Metric.NeedsComponent)]
    [InlineData("available", true, 0)]
    public void CatalogUsesExplicitFieldCapabilityWithoutInventingValues(string availability, bool selectable, int reason)
    {
        const string alias = "system.motherboardTemperature";
        var binding = CatalogBinding("motherboard_all_temperature_reported_celsius", "hardware-monitor.core",
            "field", alias, availability, preferred: availability == "available");
        var snapshot = CreateSnapshot(1, "gpu", []) with { FieldBindings = new Dictionary<string, MetricDeviceFieldBinding> { ["field"] = binding } };
        var definition = MetricCatalog.FromSnapshot(snapshot).Single(item => item.Id == alias);
        Assert.Equal(selectable, definition.Selectable);
        if (reason == 0) Assert.Null(definition.DisabledReason);
        else Assert.Equal(reason, definition.DisabledReason!.Code);
        Assert.Empty(snapshot.Items);
    }

    [Fact]
    public void CommittedSourceCapabilityCorrectsMissingComponentWithoutResamplingCatalogValues()
    {
        var target = CatalogBinding("motherboard_all_vrm_temperature_celsius", "hardware-monitor.core",
            "target", "system.vrmTemperature", "unverified", preferred: false);
        var sibling = CatalogBinding("memory_all_temperature_reported_celsius", "hardware-monitor.core",
            "sibling", "memory.temperature", "unverified", preferred: false);
        var initial = CreateSnapshot(1, "gpu", []) with { FieldBindings = new Dictionary<string, MetricDeviceFieldBinding>
            { ["target"] = target, ["sibling"] = sibling } };
        var state = new HardwareMetricCatalogState();
        state.PublishCatalogProbe(initial);
        var revision = state.Revision;
        Assert.Equal(BackendMessageCodes.Metric.NeedsComponent,
            MetricCatalog.FromSnapshot(state.Current!).Single(item => item.Id == "system.vrmTemperature").DisabledReason!.Code);
        var committed = initial with { FieldBindings = new Dictionary<string, MetricDeviceFieldBinding>
            { ["target"] = target, ["sibling"] = sibling with { Field = sibling.Field with
                { Reading = sibling.Field.Reading! with { SourceAvailability = "available", IsPreferredSource = true } } } } };
        Assert.True(state.PublishReadingMetadata(committed));
        Assert.NotEqual(revision, state.Revision);
        Assert.Empty(state.Current!.Items);
        Assert.True(MetricCatalog.FromSnapshot(state.Current).Single(item => item.Id == "system.vrmTemperature").Selectable);
    }

    [Fact]
    public void InstalledComponentIsNotReportedAsMissingBeforeItsSourceIsSampled()
    {
        const string alias = "system.motherboardTemperature";
        var binding = CatalogBinding("motherboard_all_temperature_reported_celsius", "hardware-monitor.core",
            "field", alias, "unverified", preferred: false);
        var snapshot = CreateSnapshot(1, "gpu", []) with { FieldBindings = new Dictionary<string, MetricDeviceFieldBinding> { ["field"] = binding } };
        var definitions = MetricCatalog.FromSnapshot(snapshot);
        Assert.Equal(BackendMessageCodes.Metric.NeedsComponent, definitions.Single(item => item.Id == alias).DisabledReason!.Code);

        var missing = MetricCatalog.WithInstalledComponents(definitions, new HashSet<string>(StringComparer.OrdinalIgnoreCase))
            .Single(item => item.Id == alias);
        Assert.Equal("hardware-sensors-pawnio-provider", missing.RequiredComponentId);
        Assert.Equal(BackendMessageCodes.Metric.NeedsComponent, missing.DisabledReason!.Code);

        var installed = MetricCatalog.WithInstalledComponents(definitions,
                new HashSet<string>(["Hardware-Sensors-PawnIO-Provider"], StringComparer.OrdinalIgnoreCase))
            .Single(item => item.Id == alias);
        Assert.Null(installed.RequiredComponentId);
        Assert.True(installed.Selectable);
        Assert.Null(installed.DisabledReason);
    }

    private static MetricDeviceFieldBinding CatalogBinding(string name, string source, string request,
        string alias, string availability, bool preferred)
    {
        var definition = MetricFieldRegistry.FindByName(name)!;
        Assert.NotNull(definition);
        return new("opaque-" + request, definition with { Reading = new("reading-" + request, name,
            100, availability, preferred, preferred ? "opaque-" + request : null, preferred ? name : null) }, request, source)
        { RequestAliases = [alias] };
    }

    [Fact]
    public void CommittedReadingMetadataUpdatesTheCatalogAndSignalsOnceWithoutReplacingValuesOrOfflineBindings()
    {
        var definition = MetricFieldRegistry.FindByName("gpu_amd_apu_temperature_reported_celsius")!;
        var binding = new MetricDeviceFieldBinding("opaque-gpu", definition, "raw-field", "amd.smu");
        var initial = CreateSnapshot(1, "opaque-gpu", "gpu.0.usage") with
        { FieldBindings = new Dictionary<string, MetricDeviceFieldBinding> { ["raw-field"] = binding, ["offline"] = binding with { RequestAddress = "offline" } } };
        var state = new HardwareMetricCatalogState();
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
        Assert.All(state.Current.Items.Values, value => Assert.Null(value.NumericValue));
        Assert.Equal(new[] { "gpu.0.temperature" }, state.Current.FieldBindings["raw-field"].RequestAliases);
        Assert.Contains("gpu.0.temperature", state.SelectSubscribable(MetricSampleRequest.ForIds(["gpu.0.temperature"])).Ids);
        Assert.False(state.PublishReadingMetadata(committed));
        Assert.False(state.PublishReadingMetadata(committed with { ConfigurationGeneration = committed.ConfigurationGeneration + 1 }));
        Assert.Equal(selectedRevision, state.Revision);
        Assert.Equal(2, changes);
    }
}
