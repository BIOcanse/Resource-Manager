using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite;

namespace Resource_Manager_APP.Tests;

public sealed class GpuVendorLibraryIntegrationTests
{
    [Fact]
    public void AdlxKeepsTheExistingPublicConstructorAndValidatesAnExplicitReadDeadline()
    {
        Assert.NotNull(typeof(AmdAdlxBridgeReader).GetConstructor([typeof(Func<string, string, string>)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AmdAdlxBridgeReader(TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AmdAdlxBridgeReader(Timeout.InfiniteTimeSpan));
        _ = new AmdAdlxBridgeReader(TimeSpan.FromSeconds(1)); // construction performs no hardware IO
    }

    [Fact]
    public async Task PublicReadersAndWriteBridgesBelongToTheLibraryAndEmptyDemandDoesNotAccessNativeDevices()
    {
        var owner = typeof(ReadWriteCore).Assembly;
        Assert.All(new[] { typeof(WindowsGpuAdapterOrderReader), typeof(WindowsGpuIdentityBridge),
            typeof(WindowsGpuProviderIdentityResolver), typeof(NvidiaNvmlReader), typeof(NvidiaNvapiReader),
            typeof(AmdAdlxBridgeReader), typeof(IntelGclReader), typeof(NvidiaNvmlControlBridge),
            typeof(NvidiaNvapiControlBridge), typeof(IntelGraphicsControlBridge), typeof(GpuMetrics),
            typeof(GpuSensorMetrics), typeof(HardwareSensorProviderState), typeof(SamplingObservationStatus) },
            type => Assert.Same(owner, type.Assembly));
        var initializations = 0;
        _ = new NvidiaNvmlReader(() => { initializations++; return 1; });
        _ = new NvidiaNvmlControlBridge();
        _ = new NvidiaNvapiControlBridge();
        Assert.Equal(IntelGraphicsControlProbe.TargetMissing, new IntelGraphicsControlBridge().Probe(null).Status);
        Assert.Equal(0, initializations);
        var inventory = new WindowsGpuAdapterInventoryRead(SamplingObservationStatus.Current, 1, 1, 0, 0, 0, 1, []);
        Assert.Empty(await new NvidiaNvapiReader().ReadAsync(new(null, false, false, false), inventory, CancellationToken.None));
        Assert.Empty(new IntelGclReader().Read(inventory, new Dictionary<int, IReadOnlySet<MetricFieldCode>>(), CancellationToken.None));
        var none = new AmdAdlxReadRequest(null, false, false, false, false, false, false, false, false, false, false, false);
        Assert.Empty(await new AmdAdlxBridgeReader().ReadAsync(none, inventory, CancellationToken.None));
    }

    [Fact]
    public void CanonicalGpuDeclarationsMergeOnlyTheSamePhysicalIdentityAndKeepVendorDomains()
    {
        var at = DateTimeOffset.UtcNow;
        var windows = new DeviceTopologyGpuInformation("  pci\\nvidia  ", "0000000000000001", "GPU", "10DE", "1234", "1.2");
        var nvidia = new DeviceTopologyNvidiaGpuInformation("PCI\\NVIDIA", "vbios", "8.9");
        var amd = new DeviceTopologyAmdGpuInformation("PCI\\AMD", "amd-bios", "GDDR6", "part", "date");
        var frame = WindowsInformationFieldProjection.GpuDeclarations([windows], [nvidia], [amd], at);
        Assert.Equal(2, frame.Devices.Count);
        var matched = Assert.Single(frame.Devices, device => device.DeviceIdentity == "windows-pnp|PCI\\NVIDIA");
        Assert.Equal(7, matched.Fields.Count);
        Assert.DoesNotContain(matched.Fields, field => field.Definition.Vendor == "amd");
        Assert.Equal("8.9", matched.Fields.Single(field => field.Definition == DeviceInformationFieldRegistry.GpuNvidiaCudaComputeCapability).Value!.Value.GetString());
        var failed = WindowsInformationFieldProjection.GpuDeclarations([windows], [nvidia], [amd], null);
        Assert.All(failed.Devices.SelectMany(device => device.Fields), field => Assert.Null(field.Value));
        Assert.Empty(WindowsInformationFieldProjection.GpuDeclarations([windows, windows], null, null, at).Devices);
    }

    [Fact]
    public void ExplicitHostDescriptionKeepsTheOriginalSensorStateAndDefaultRawReadingHasNoProductText()
    {
        var packet = new NativeAdlxGpuMetrics { AvailableFlags = 16, PowerWatts = 0 };
        var raw = AmdAdlxBridgeReader.CreateSensors(packet, "GPU");
        var described = AmdAdlxBridgeReader.CreateSensors(packet, "GPU", GpuProviderMessages.Describe);
        Assert.Equal(HardwareSensorProviderStates.Partial, raw.ProviderState.State);
        Assert.Equal(0, raw.PowerWatts);
        Assert.Null(raw.ProviderState.Message);
        Assert.Equal(GpuProviderMessages.Describe("AMD ADLX", HardwareSensorProviderStates.Partial), described.ProviderState.Message);
        Assert.Equal(raw.PowerWatts, described.PowerWatts);
        Assert.Null(raw.CurrentAmps);
    }
}
