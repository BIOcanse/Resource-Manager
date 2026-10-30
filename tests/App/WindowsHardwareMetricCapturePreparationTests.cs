using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.ReadWrite.Native;
using ResourceManager.ReadWrite.Windows.Metrics.Capture;

namespace Resource_Manager_APP.Tests;

public sealed class WindowsHardwareMetricCapturePreparationTests
{
    [Fact]
    public void MinimalControlPreparationRequiresCurrentOrdinaryCatalogMetadata()
    {
        var header = Header(configurationGeneration: 4, catalogGeneration: 9, committedGeneration: 12);
        var catalog = Snapshot() with
        {
            WorkspaceIdentity = 17,
            ConfigurationGeneration = 4,
            CatalogGeneration = 9,
            CommittedGeneration = 11
        };

        Assert.True(WindowsHardwareMetricCapture.CanUseMinimalControlPreparation(
            MetricSampleRequest.ForIds(["cpu.usage"]), 4, 17, header, catalog));

        Assert.False(WindowsHardwareMetricCapture.CanUseMinimalControlPreparation(
            MetricSampleRequest.ForIds(["cpu.usage"]).WithCatalog(), 4, 17, header, catalog));
        Assert.False(WindowsHardwareMetricCapture.CanUseMinimalControlPreparation(
            MetricSampleRequest.CatalogProbe, 4, 17, header, catalog));
        Assert.False(WindowsHardwareMetricCapture.CanUseMinimalControlPreparation(
            MetricSampleRequest.ForIds(["cpu.usage"]), 4, 18, header, catalog));
        Assert.False(WindowsHardwareMetricCapture.CanUseMinimalControlPreparation(
            MetricSampleRequest.ForIds(["cpu.usage"]), 5, 17, header, catalog));
        Assert.False(WindowsHardwareMetricCapture.CanUseMinimalControlPreparation(
            MetricSampleRequest.ForIds(["cpu.usage"]), 4, 17,
            Header(configurationGeneration: 4, catalogGeneration: 10, committedGeneration: 12), catalog));
        Assert.False(WindowsHardwareMetricCapture.CanUseMinimalControlPreparation(
            MetricSampleRequest.ForIds(["cpu.usage"]), 4, 17, header,
            catalog with { CommittedGeneration = 13 }));
        Assert.False(WindowsHardwareMetricCapture.CanUseMinimalControlPreparation(
            MetricSampleRequest.ForIds(["cpu.usage"]), 4, 17, header, null));
    }

    [Fact]
    public void MinimalControlPreparationDropsAllPreviousFrameRowsAndBindingsButKeepsGenerationMetadata()
    {
        var binding = new MetricDeviceFieldBinding("recorded-device",
            MetricFieldRegistry.FindByName("cpu_intel_requested_pl1_power_limit_watts")!,
            "managed-control|recorded", "intel.msr") { ControlTarget = new("recorded") };
        var prior = Snapshot() with
        {
            WorkspaceIdentity = 17,
            ConfigurationGeneration = 4,
            CatalogGeneration = 9,
            CommittedGeneration = 11,
            FieldBindings = new Dictionary<string, MetricDeviceFieldBinding> { [binding.RequestAddress] = binding },
            Items = new Dictionary<string, MetricReadingValue>
            {
                [binding.RequestAddress] = new(Id: binding.RequestAddress, NumericValue: 200, Unit: "W", Percent: null)
            },
            Datasets = new Dictionary<string, HardwareMetricDatasetObservation>
            {
                [binding.RequestAddress] = new(binding.RequestAddress, SamplingObservationStatus.Current,
                    10, 100, 17, 4, 9, 11)
            }
        };

        var minimal = WindowsHardwareMetricCapture.CreateMinimalControlPreparation(prior);

        Assert.Equal(prior.CapturedAt, minimal.CapturedAt);
        Assert.Equal(prior.WorkspaceIdentity, minimal.WorkspaceIdentity);
        Assert.Equal(prior.ConfigurationGeneration, minimal.ConfigurationGeneration);
        Assert.Equal(prior.CatalogGeneration, minimal.CatalogGeneration);
        Assert.Equal(prior.CommittedGeneration, minimal.CommittedGeneration);
        Assert.Empty(minimal.FieldBindings);
        Assert.Empty(minimal.Items);
        Assert.Empty(minimal.Datasets);
    }

    private static NativeMetricSnapshotSnapshotHeader Header(
        ulong configurationGeneration, ulong catalogGeneration, ulong committedGeneration)
        => new()
        {
            AbiVersion = NativeMetricSnapshotAbi.Version,
            StructSize = 1,
            ConfigurationGeneration = configurationGeneration,
            CatalogGeneration = catalogGeneration,
            CatalogFingerprint = 5,
            CommittedGeneration = committedGeneration,
            StateRevision = 3,
            LastOperationEpoch = 4,
            LastPlanEpoch = 2,
            CapturedAtMilliseconds = 100,
            SourceCount = 8,
            MetricCount = 12,
            RuleCount = 12,
            GpuAdapterCount = 0,
            CurrentMetricCount = 0,
            UnavailableMetricCount = 0,
            UnsupportedMetricCount = 0,
            SkippedMetricCount = 0,
            Phase = 1,
            SemanticFingerprint = 7
        };

    private static HardwareMetricSnapshot Snapshot()
        => new(DateTimeOffset.UnixEpoch, HardwareMetricTestData.EmptyCpu, HardwareMetricTestData.EmptyMemory, HardwareMetricTestData.EmptyVirtualMemory, [], HardwareMetricTestData.EmptyGpuInventory,
            new Dictionary<string, MetricReadingValue>(StringComparer.OrdinalIgnoreCase));
}
