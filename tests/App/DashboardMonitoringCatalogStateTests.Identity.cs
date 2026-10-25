using System.Reflection;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace Resource_Manager_APP.Tests;

public sealed partial class HardwareMetricCatalogStateTests
{
    [Theory]
    [InlineData("workspace")]
    [InlineData("configuration")]
    [InlineData("catalog")]
    [InlineData("gpu-identity")]
    [InlineData("gpu-index")]
    [InlineData("metric")]
    [InlineData("device")]
    [InlineData("address")]
    [InlineData("source")]
    [InlineData("reading-id")]
    [InlineData("quantity")]
    [InlineData("rank")]
    [InlineData("availability")]
    [InlineData("preferred")]
    [InlineData("preferred-device")]
    [InlineData("preferred-field")]
    public void IdentityDetectsAllPreviouslyComparedFields(string change)
    {
        var original = CreateSnapshot(1, "gpu", "gpu.0.usage");
        var definition = MetricFieldRegistry.FindByName("gpu_amd_apu_temperature_reported_celsius")!;
        var reading = new MetricReadingSource("reading", "temperature", 10, "available", true, "device", definition.Name);
        var binding = new MetricDeviceFieldBinding("device", definition with { Reading = reading }, "address", "source");
        original = original with { FieldBindings = new Dictionary<string, MetricDeviceFieldBinding> { ["address"] = binding } };
        var next = change switch
        {
            "workspace" => original with { WorkspaceIdentity = 2 },
            "configuration" => original with { ConfigurationGeneration = 2 },
            "catalog" => original with { CatalogGeneration = 2 },
            "gpu-identity" => original with { Gpus = [original.Gpus[0] with { IdentityKey = "other-gpu" }] },
            "gpu-index" => original with { Gpus = [original.Gpus[0] with { Index = 1 }] },
            "metric" => original with { Items = CreateSnapshot(1, "gpu", "gpu.0.usage", "cpu.temperature").Items },
            _ => original with { FieldBindings = new Dictionary<string, MetricDeviceFieldBinding>
            { ["address"] = change switch
                {
                    "device" => binding with { DeviceIdentity = "other" },
                    "address" => binding with { RequestAddress = "other" },
                    "source" => binding with { SourceId = "other" },
                    _ => binding with { Field = definition with { Reading = change switch
                        {
                            "reading-id" => reading with { Id = "other" },
                            "quantity" => reading with { Quantity = "other" },
                            "rank" => reading with { SourceRank = 20 },
                            "availability" => reading with { SourceAvailability = "unsupported" },
                            "preferred" => reading with { IsPreferredSource = false },
                            "preferred-device" => reading with { PreferredDeviceIdentity = "other" },
                            "preferred-field" => reading with { PreferredFieldName = "other" },
                            _ => throw new InvalidOperationException(change)
                        } } }
                } } }
        };
        var state = new HardwareMetricCatalogState();
        state.PublishCatalogProbe(original);
        var revision = state.Revision;
        state.PublishCatalogProbe(next);
        Assert.NotEqual(revision, state.Revision);
    }

    [Fact]
    public void EqualHashStillRequiresStructuralIdentityEquality()
    {
        var first = new HardwareMetricCatalogState();
        first.PublishCatalogProbe(CreateSnapshot(1, "gpu-a", "gpu.0.usage"));
        var second = new HardwareMetricCatalogState();
        second.PublishCatalogProbe(CreateSnapshot(1, "gpu-b", "gpu.0.usage"));
        var identityField = typeof(HardwareMetricCatalogState).GetField("identity", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var firstIdentity = identityField.GetValue(first)!;
        var secondIdentity = identityField.GetValue(second)!;
        var hashField = firstIdentity.GetType().GetField("hash", BindingFlags.Instance | BindingFlags.NonPublic)!;
        hashField.SetValue(secondIdentity, hashField.GetValue(firstIdentity));
        Assert.Equal(firstIdentity.GetHashCode(), secondIdentity.GetHashCode());
        Assert.False(firstIdentity.Equals(secondIdentity));
    }
}
