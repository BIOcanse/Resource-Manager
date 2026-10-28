using System.Text.Json;
using ResourceManager.App.Application.DeviceTopology;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Windows.Devices.Snapshots;

namespace Resource_Manager_APP.Tests;

public sealed class WindowsGpuGraphicsDockTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData(0u, false)]
    [InlineData(0x100u, false)] // ACGSupported, not Detachable.
    [InlineData(0x200u, false)] // SupportSetTimingsFromVidPn.
    [InlineData(0x400u, true)]
    [InlineData(0x800u, false)] // ComputeOnly.
    [InlineData(0xffffffffu, true)]
    public void OnlyOfficialDetachableBitProducesGraphicsDock(uint? flags, bool? expected)
    {
        var adapter = Adapter(flags);
        var gpu = Assert.Single(WindowsGpuInformationProjection.Create(Inventory(adapter))!);
        Assert.Equal(expected, adapter.IsDetachable);
        Assert.Equal(expected, gpu.IsDetachable);
        Assert.Equal(flags, gpu.AdapterTypeFlags);
        Assert.Equal(expected == true ? "graphics_dock" : null, gpu.AttachmentKind);
        Assert.Equal(gpu.AttachmentKind, adapter.AttachmentKind);
        Assert.Equal(flags.HasValue ? "d3dkmt-adapter-type" : null, gpu.AttachmentEvidenceSource);
    }

    [Theory]
    [InlineData("Thunderbolt USB4 External Graphics Dock NVIDIA RTX PRO 6000")]
    [InlineData("NVIDIA RTX PRO 6000")]
    public void NamesCannotFabricateExternalOrFixedAttachment(string name)
    {
        var adapter = Adapter(null) with { Name = name };
        Assert.Null(adapter.IsDetachable);
        var unknown = Assert.Single(WindowsGpuInformationProjection.Create(Inventory(adapter))!);
        Assert.Null(unknown.IsDetachable);
        Assert.Null(unknown.AttachmentKind);
        Assert.Null(unknown.AttachmentEvidenceSource);
        var knownFixed = Assert.Single(WindowsGpuInformationProjection.Create(Inventory(adapter with { AdapterTypeFlags = 0 }))!);
        Assert.False(knownFixed.IsDetachable);
        Assert.Null(knownFixed.AttachmentKind); // A negative detachable flag does not establish an internal connector.
    }

    [Fact]
    public void LinkedPhysicalMembersKeepLogicalAdapterEvidenceWithoutInventingMemberNames()
    {
        var adapter = Adapter(0x403);
        var second = adapter.PhysicalIdentities[0] with { PhysicalAdapterIndex = 1, PnpInstanceId = @"PCI\SECOND" };
        adapter = adapter with { PhysicalIdentities = [adapter.PhysicalIdentities[0], second] };
        var declarations = WindowsGpuInformationProjection.Create(Inventory(adapter))!;
        Assert.Equal(2, declarations.Count);
        Assert.All(declarations, gpu => {
            Assert.Null(gpu.Name);
            Assert.True(gpu.IsDetachable);
            Assert.Equal(0x403u, gpu.AdapterTypeFlags);
            Assert.Equal("graphics_dock", gpu.AttachmentKind);
        });
        // Logical evidence stays readable even when physical identities cannot be mapped.
        Assert.True((adapter with { PhysicalIdentityStatus = SamplingObservationStatus.Unavailable,
            PhysicalIdentities = [] }).IsDetachable);
    }

    [Fact]
    public void OriginalTopologyEntryReadsInventoryOnceForDetachableFactsAndVendorDeclarations()
    {
        var reads = 0;
        var inventory = Inventory(Adapter(0x400));
        var source = new WindowsGpuAdapterOrderMonitoringZone(() => { reads++; return inventory; });
        var nvml = new NvidiaNvmlMonitoringZone((_, _, _) => Task.FromResult<IReadOnlyList<GpuMetrics>>([]), data => {
            Assert.Same(inventory, data); return [];
        });
        var declarations = WindowsDeviceTopologyReader.ReadGpuDeclarations(nvml, source);
        Assert.Same(inventory, declarations.Inventory);
        Assert.True(Assert.Single(declarations.Windows!).IsDetachable);
        Assert.Equal(1, reads);
    }

    [Fact]
    public void RawEvidenceChangesSemanticIdentityAndRoundTripsWithoutNameInference()
    {
        var gpu = Assert.Single(WindowsGpuInformationProjection.Create(Inventory(Adapter(0x400)))!);
        var snapshot = DeviceInformationTestData.Snapshot() with { GpuInformation = [gpu] };
        var comparer = new DeviceTopologySemanticComparer();
        Assert.NotEqual(comparer.ComputeHash(snapshot), comparer.ComputeHash(snapshot with {
            GpuInformation = [gpu with { AdapterTypeFlags = null }] }));
        Assert.NotEqual(comparer.ComputeHash(snapshot), comparer.ComputeHash(snapshot with {
            GpuInformation = [gpu with { AdapterTypeFlags = 0x401 }] }));
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var persisted = JsonSerializer.Deserialize<DeviceTopologyGpuInformation>(JsonSerializer.Serialize(gpu, options), options)!;
        Assert.Equal(gpu, persisted);
        Assert.Equal("graphics_dock", persisted.AttachmentKind);
        var legacy = JsonSerializer.Deserialize<DeviceTopologyGpuInformation>(
            "{\"pnpInstanceId\":\"PCI\\\\FIRST\",\"name\":\"External USB4 Graphics Dock\"}", options)!;
        Assert.Null(legacy.AdapterTypeFlags);
        Assert.Null(legacy.AttachmentKind);
    }

    [Fact]
    public async Task FailedGpuCaptureClearsDetachableFactsInsteadOfRetainingStaleDockClassification()
    {
        var gpu = Assert.Single(WindowsGpuInformationProjection.Create(Inventory(Adapter(0x400)))!);
        var read = DeviceInformationTestData.Snapshot() with { GpuInformation = [gpu] };
        var policy = new DeviceTopologyCapturePolicy(new Reader(() => read),
            new DeviceTopologySemanticComparer().ComputeHash, DeviceTopologyInformationProjection.Project,
            static (_, _) => Task.CompletedTask, static _ => new("device-topology-read-failed", []),
            static (_, _, _, _) => { }, static _ => { }, static () => { });
        using var owner = new DeviceTopologyCaptureOwner(new FirmwareReader(), policy);
        await owner.CaptureInformationAsync(default);
        Assert.True(Assert.Single(owner.ReadState().Snapshot!.GpuInformation!).IsDetachable);
        var initialGeneration = owner.ReadState().ContentGeneration;
        // The inventory's physical sampling generation is unrelated to these structural declarations.
        // Changing just the raw flags must still replace and publish the topology snapshot.
        read = read with { GpuInformation = [gpu with { AdapterTypeFlags = 0 }] };
        await owner.CaptureInformationAsync(default);
        Assert.False(Assert.Single(owner.ReadState().Snapshot!.GpuInformation!).IsDetachable);
        Assert.Equal(initialGeneration + 1, owner.ReadState().ContentGeneration);
        read = read with { GpuInformation = null };
        await owner.CaptureInformationAsync(default);
        var failed = Assert.Single(owner.ReadState().Snapshot!.GpuInformation!);
        Assert.Equal(gpu.PnpInstanceId, failed.PnpInstanceId);
        Assert.Null(failed.AdapterTypeFlags);
        Assert.Null(failed.IsDetachable);
        Assert.Null(failed.AttachmentKind);
        Assert.Null(failed.AttachmentEvidenceSource);
    }

    private static WindowsGpuAdapterInventoryRead Inventory(params WindowsGpuAdapter[] adapters)
        => new(SamplingObservationStatus.Current, 1, DateTimeOffset.UtcNow.UtcTicks,
            (uint)adapters.Length, 0, 0, 1, adapters);

    private static WindowsGpuAdapter Adapter(uint? flags) => new(0, "NVIDIA RTX PRO 6000", 0x10de, 0x1234, 0,
        new() { LowPart = 1 }, false, 8UL << 30, WindowsGpuAdapterKind.Dedicated) {
        AdapterTypeFlags = flags, PhysicalIdentityStatus = SamplingObservationStatus.Current,
        PhysicalIdentities = [new(0, @"PCI\FIRST", WindowsGpuPnpKind.Pci, true, false, 0, 1, 0, 0,
            0x10de, 0x1234, 0, 0, 1, WindowsGpuIdentityEvidence.CmPciAddress,
            WindowsGpuPhysicalIdentityStatus.Bound, 0)]
    };

    private sealed class Reader(Func<DeviceTopologySnapshot> read) : IDeviceTopologyReader
    { public DeviceTopologySnapshot ReadSnapshot() => read(); }

    private sealed class FirmwareReader : IAcpiFirmwareTableReader
    { public IReadOnlyList<AcpiFirmwareTable>? ReadTables() => []; }
}
