using System.Collections.Immutable;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Domain.Messages;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Windows.Metrics.Capture;

namespace Resource_Manager_APP.Tests;

public sealed class MetricAvailabilityRegressionTests
{
    [Fact]
    public void CanonicalAndAliasCandidatesDoNotInventSelectionFromPreference()
    {
        var field = MetricFieldRegistry.FindByTemplate("gpu.{gpu}.temperature", "amd.adlx")!;
        var binding = new MetricDeviceFieldBinding("recorded-gpu", field with
        {
            Reading = new("reading-edge", "gpu.edge_temperature", 20, "available", true, "recorded-gpu", field.Name)
        }, "scope/edge", "amd.adlx") { RequestAliases = ["gpu.1.temperature"] };
        var snapshot = EmptySnapshot() with { FieldBindings = new Dictionary<string, MetricDeviceFieldBinding>
            { [binding.RequestAddress] = binding } };
        var definitions = MetricCatalog.FromSnapshot(snapshot);
        Assert.False(Assert.Single(definitions.Single(item => item.Id == binding.RequestAddress).CandidateSources).IsSelected);
        Assert.False(Assert.Single(definitions.Single(item => item.Id == "gpu.1.temperature").CandidateSources).IsSelected);
    }

    [Fact]
    public void CanonicalUnprobedWindowsFieldHasItsSourceAndCanBeRequested()
    {
        var field = MetricFieldRegistry.FindByName("network_all_rx_unicast_packets_actual")!;
        var binding = new MetricDeviceFieldBinding("recorded-physical-interface", field with
        {
            Reading = new("reading-network", "network.rx_unicast_packets", 100, "unverified", false, null, null)
        }, "scope/network_all_rx_unicast_packets_actual", "windows.network-interface");
        var snapshot = EmptySnapshot() with { FieldBindings = new Dictionary<string, MetricDeviceFieldBinding>
            { [binding.RequestAddress] = binding } };
        var definition = MetricCatalog.FromSnapshot(snapshot).Single(item => item.Id == binding.RequestAddress);
        Assert.True(definition.Selectable);
        Assert.Null(definition.DisabledReason);
        var candidate = Assert.Single(definition.CandidateSources);
        Assert.Equal("windows.network-interface", candidate.SourceId);
        Assert.Equal("unverified", candidate.SourceAvailability);
        Assert.False(candidate.IsSelected);
        Assert.Contains(binding.RequestAddress, MetricDeviceFields.CreateRequest(snapshot,
            binding.DeviceIdentity, [field.Name]).Ids);

        var unsupported = snapshot with { FieldBindings = new Dictionary<string, MetricDeviceFieldBinding>
            { [binding.RequestAddress] = binding with { Field = binding.Field with
                { Reading = binding.Field.Reading! with { SourceAvailability = "unsupported" } } } } };
        var rejected = MetricCatalog.FromSnapshot(unsupported).Single(item => item.Id == binding.RequestAddress);
        Assert.False(rejected.Selectable);
        Assert.Equal(BackendMessageCodes.Metric.NotExposed, rejected.DisabledReason!.Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProductionAmdCatalogRegistersSensorsIndependentlyOfDedicatedMemory(bool dedicated)
    {
        var catalog = NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(
            HostManagerTestPlanFactory.CreatePlan().MetricSnapshot,
            [new(1, "AMD Radeon 610M", "recorded-amd-610m", 0x610, 1, 2, dedicated)],
            [], [], NativeMetricSnapshotCatalogHandleMap.Empty);
        var frame = new NativeMetricSnapshotCommittedFrame(new()
            { ConfigurationGeneration = 1, CatalogGeneration = 1, CommittedGeneration = 1 },
            [], [], [], [], catalog);
        foreach (var code in new[] { MetricFieldCode.GpuGraphicsClock, MetricFieldCode.GpuVoltage })
        {
            var candidates = catalog.MetricBindings.Values.Where(binding => binding.FieldCode == code).ToArray();
            Assert.Equal(2, candidates.Length);
            Assert.Equal(2, candidates.Select(binding => binding.Field!.Name).Distinct().Count());
            Assert.Equal(2, candidates.Select(binding => binding.MetricHandle).Distinct().Count());
            Assert.Contains(candidates, binding => binding.SourceId == "amd.adlx");
            Assert.Contains(candidates, binding => binding.SourceId == "amd.smu");
        }
        var tracker = new MetricReadingSourceTracker();
        var snapshot = NativeMetricSnapshotCommittedProjection.Create(frame, 1, tracker.Select(frame, 1));
        var definitions = MetricCatalog.WithInstalledComponents(MetricCatalog.FromSnapshot(snapshot),
            new HashSet<string> { "amd-adlx-provider", "amd-smu-pawnio-provider" });
        foreach (var suffix in new[] { "power", "boardPower", "temperature", "hotspotTemperature",
                     "intakeTemperature", "coreVoltage", "graphicsClock", "graphicsClockPercent", "memoryClock" })
        {
            var definition = definitions.Single(item => item.Id == $"gpu.1.{suffix}");
            Assert.True(definition.Selectable, suffix);
            Assert.Contains(definition.CandidateSources, candidate => candidate.SourceId == "amd.adlx");
            Assert.All(definition.CandidateSources, candidate => Assert.Equal("unverified", candidate.SourceAvailability));
        }
        foreach (var suffix in new[] { "vram", "vramPercent" })
        {
            var definition = definitions.Single(item => item.Id == $"gpu.1.{suffix}");
            if (dedicated) Assert.NotEmpty(definition.CandidateSources);
            else
            {
                Assert.False(definition.Selectable);
                Assert.Equal(BackendMessageCodes.Metric.NotApplicable, definition.DisabledReason!.Code);
                Assert.Null(definition.RequiredComponentId);
            }
        }
        Assert.Equal(dedicated, catalog.MetricBindings.Values.Any(binding => binding.SourceId == "amd.adlx"
            && binding.FieldCode == MetricFieldCode.AdlxDedicatedMemoryMb));
        // No package current/limit is relabelled as a graphics-domain reading.
        Assert.DoesNotContain(catalog.MetricBindings.Values, binding => binding.DisplayIndex == 1
            && binding.Field?.Resource == MetricResource.Gpu
            && binding.FieldCode is MetricFieldCode.GpuCurrent or MetricFieldCode.GpuPowerLimit);
    }

    private static HardwareMetricSnapshot EmptySnapshot() => new(default, HardwareMetricTestData.EmptyCpu,
        HardwareMetricTestData.EmptyMemory, HardwareMetricTestData.EmptyVirtualMemory, [],
        HardwareMetricTestData.EmptyGpuInventory, ImmutableDictionary<string, MetricReadingValue>.Empty);
}
