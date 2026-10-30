using System.Text;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology.Pci;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;
using ResourceManager.App.Infrastructure.Monitoring;

namespace Resource_Manager_APP.Tests;

public sealed class WindowsGpuInformationCollectorTests
{
    internal static DeviceTopologyGpuInformation[] Declarations() =>
        [new(NvidiaGpuDeclarationCollectorTests.PnpA, "FEDCBA9876543210", "显卡 Fixture", "10DE", "2684", "32.0.15.8100")];

    [Theory]
    [InlineData(0x10DE)]
    [InlineData(0x1002)]
    [InlineData(0x8086)]
    public void AllVendorsKeepExactPhysicalIdentityDriverAndFullLuid(uint vendor)
    {
        var adapter = Adapter(9, "fixture-pnp", vendor) with { Luid = new() { LowPart = 0x76543210, HighPart = unchecked((int)0xFEDCBA98) } };
        var gpu = Assert.Single(WindowsGpuInformationProjection.Create(Inventory(adapter))!);
        Assert.Equal("FIXTURE-PNP", gpu.PnpInstanceId);
        Assert.Equal("FEDCBA9876543210", gpu.AdapterLuid);
        Assert.Equal("显卡 Fixture", gpu.Name);
        Assert.Equal(vendor.ToString("X4"), gpu.PciVendorId);
        Assert.Equal("2684", gpu.PciDeviceId);
        Assert.Equal("32.0.15.8100", gpu.DriverVersion);
    }

    [Fact]
    public void LinkedPhysicalMembersKeepSharedLogicalLuidWithoutInventingIndividualNames()
    {
        var first = Adapter(9, "first");
        var second = first.PhysicalIdentities[0] with { PhysicalAdapterIndex = 1, PnpInstanceId = "second", DeviceId = 0x123,
            DriverVersion = "2.3.4.5" };
        var result = WindowsGpuInformationProjection.Create(Inventory(first with { PhysicalIdentities = [first.PhysicalIdentities[0], second] }))!;
        Assert.Equal(2, result.Count);
        Assert.All(result, gpu => { Assert.Null(gpu.Name); Assert.Equal("000000000000000A", gpu.AdapterLuid); });
        Assert.Equal("2684", result[0].PciDeviceId);
        Assert.Equal("0123", result[1].PciDeviceId);
        Assert.Equal("2.3.4.5", result[1].DriverVersion);
    }

    [Fact]
    public void NonPciMemberAndOutOfRangeIdsAreNeverFormattedAsPciIds()
    {
        var adapter = Adapter(1, @"ROOT\GPU");
        var physical = adapter.PhysicalIdentities[0] with { Status = WindowsGpuPhysicalIdentityStatus.NonPci, PnpKind = WindowsGpuPnpKind.Root };
        var nonPci = Assert.Single(WindowsGpuInformationProjection.Create(Inventory(adapter with { PhysicalIdentities = [physical] }))!);
        Assert.Null(nonPci.PciVendorId); Assert.Null(nonPci.PciDeviceId);
        Assert.Equal(adapter.Name, nonPci.Name); Assert.NotNull(nonPci.AdapterLuid); Assert.NotNull(nonPci.DriverVersion);
        physical = adapter.PhysicalIdentities[0] with { VendorId = 0x10000, DeviceId = 0x10000 };
        var invalid = Assert.Single(WindowsGpuInformationProjection.Create(Inventory(adapter with { PhysicalIdentities = [physical] }))!);
        Assert.Null(invalid.PciVendorId); Assert.Null(invalid.PciDeviceId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    public void IncompleteOrAmbiguousInventoryIsUnavailableRatherThanSuccessfulEmpty(int mode)
    {
        var adapter = Adapter(1, "first");
        var inventory = Inventory(adapter);
        inventory = mode switch {
            0 => inventory with { Status = SamplingObservationStatus.Unavailable },
            1 => inventory with { Generation = 0 },
            2 => inventory with { ObservedCount = 2 },
            3 => inventory with { SkippedCount = 1 },
            4 => inventory with { OverflowCount = 1 },
            5 => Inventory(adapter with { PhysicalIdentityStatus = SamplingObservationStatus.Unavailable }),
            6 => Inventory(adapter with { PhysicalIdentities = [] }),
            7 => Inventory(adapter with { PhysicalIdentities = [adapter.PhysicalIdentities[0] with { Status = WindowsGpuPhysicalIdentityStatus.Conflict }] }),
            8 => Inventory(adapter, Adapter(2, "FIRST")),
            9 => Inventory(adapter, Adapter(1, "second")),
            _ => Inventory(adapter with { Luid = default })
        };
        Assert.Null(WindowsGpuInformationProjection.Create(inventory));
    }

    [Fact]
    public void EmptyAndSoftwareOnlyInventoriesRemoveHardwareWhileMissingVersionIsIndependent()
    {
        Assert.Empty(WindowsGpuInformationProjection.Create(Inventory())!);
        Assert.Empty(WindowsGpuInformationProjection.Create(Inventory(Adapter(1, "first") with { IsSoftware = true }))!);
        var adapter = Adapter(1, "first");
        var gpu = Assert.Single(WindowsGpuInformationProjection.Create(Inventory(adapter with {
            PhysicalIdentities = [adapter.PhysicalIdentities[0] with { DriverVersion = null }] }))!);
        Assert.Null(gpu.DriverVersion); Assert.NotNull(gpu.PciDeviceId); Assert.NotNull(gpu.Name); Assert.NotNull(gpu.AdapterLuid);
    }

    [Fact]
    public void OriginalTopologyEntrySharesOneInventoryAndIsolatesVendorFailure()
    {
        var reads = 0; var vendorReads = 0;
        var inventory = Inventory(Adapter(9, NvidiaGpuDeclarationCollectorTests.PnpA));
        var source = new WindowsGpuAdapterOrderMonitoringZone(() => { reads++; return inventory; });
        var nvml = new NvidiaNvmlMonitoringZone((_, _, _) => Task.FromResult<IReadOnlyList<GpuMetrics>>([]), data => {
            vendorReads++; Assert.Same(inventory, data); throw new InvalidOperationException("vendor failure");
        });
        Assert.Equal(0, reads);
        var result = WindowsDeviceTopologyReader.ReadGpuDeclarations(nvml, source);
        Assert.Single(result.Windows!); Assert.Null(result.Nvidia); Assert.Equal(1, reads); Assert.Equal(1, vendorReads);
    }

    [Fact]
    public void OriginalTopologyEntryUsesSameInventoryForBothSuccessfulDeclarations()
    {
        var inventory = Inventory(Adapter(9, NvidiaGpuDeclarationCollectorTests.PnpA));
        var reads = 0;
        var source = new WindowsGpuAdapterOrderMonitoringZone(() => { reads++; return inventory; });
        var nvml = new NvidiaNvmlMonitoringZone((_, _, _) => Task.FromResult<IReadOnlyList<GpuMetrics>>([]), data => {
            Assert.Same(inventory, data); return NvidiaGpuDeclarationCollectorTests.Declarations();
        });
        var result = WindowsDeviceTopologyReader.ReadGpuDeclarations(nvml, source);
        Assert.Equal(Assert.Single(result.Windows!).PnpInstanceId, Assert.Single(result.Nvidia!).PnpInstanceId);
        Assert.Equal(1, reads);
    }

    [Fact]
    public void BothGpuSourcesMergeUnderOnePnpAndPersistedValuesAreAllEmpty()
    {
        var snapshot = DeviceInformationTestData.Snapshot() with { GpuInformation = Declarations(),
            NvidiaGpuInformation = NvidiaGpuDeclarationCollectorTests.Declarations() };
        var gpu = Assert.Single(DeviceTopologyInformationProjection.Create(snapshot, DateTimeOffset.UtcNow).Devices,
            static d => d.DeviceIdentity == NvidiaGpuDeclarationCollectorTests.IdentityA);
        Assert.Equal("gpu_adapter", gpu.ObjectType); Assert.Equal(7, gpu.Fields.Count);
        Assert.Equal(7, gpu.Fields.Select(f => f.Definition.Name).Distinct().Count());
        Assert.All(gpu.Fields.Where(f => f.Definition.Vendor == "all"), field => {
            Assert.Equal("windows.gpu-adapter-order", field.Definition.SourceId); Assert.Equal("gpu", field.Definition.Resource);
        });
        var persisted = Assert.Single(DeviceTopologyInformationProjection.Create(snapshot, null).Devices,
            static d => d.DeviceIdentity == NvidiaGpuDeclarationCollectorTests.IdentityA);
        Assert.All(persisted.Fields, static field => Assert.Null(field.Value));
    }

    [Fact]
    public void EnumerationOrderChangesDoNotChangeTopologyIdentity()
    {
        var snapshot = DeviceInformationTestData.Snapshot() with { GpuInformation = [Declarations()[0], Declarations()[0] with { PnpInstanceId = "second" }],
            NvidiaGpuInformation = [NvidiaGpuDeclarationCollectorTests.Declarations()[0], NvidiaGpuDeclarationCollectorTests.Declarations()[0] with { PnpInstanceId = "second" }] };
        var comparer = new DeviceTopologySemanticComparer();
        Assert.Equal(comparer.ComputeHash(snapshot), comparer.ComputeHash(snapshot with {
            GpuInformation = snapshot.GpuInformation!.Reverse().ToArray(), NvidiaGpuInformation = snapshot.NvidiaGpuInformation!.Reverse().ToArray() }));
    }

    [Fact]
    public void GpuPciIdsHaveOneAuthoritativeSourceAndOtherPciDeclarationsRemain()
    {
        var port = DeviceInformationTestData.Port(NvidiaGpuDeclarationCollectorTests.PnpA) with {
            PciIdentification = PciDeviceIdentificationReader.Read([@"PCI\VEN_10DE&DEV_2684&SUBSYS_000010DE&REV_01"], []) };
        var snapshot = DeviceInformationTestData.Snapshot(port) with { GpuInformation = Declarations(),
            NvidiaGpuInformation = NvidiaGpuDeclarationCollectorTests.Declarations() };
        var gpu = Assert.Single(DeviceTopologyInformationProjection.Create(snapshot, DateTimeOffset.UtcNow).Devices,
            static d => d.DeviceIdentity == NvidiaGpuDeclarationCollectorTests.IdentityA);
        Assert.DoesNotContain(gpu.Fields, field => field.Definition == DeviceInformationFieldRegistry.PciAllVendorId
            || field.Definition == DeviceInformationFieldRegistry.PciAllDeviceId);
        Assert.Equal("10DE", Assert.Single(gpu.Fields, field => field.Definition == DeviceInformationFieldRegistry.GpuAllPciVendorId).Value!.Value.GetString());
        Assert.Equal("2684", Assert.Single(gpu.Fields, field => field.Definition == DeviceInformationFieldRegistry.GpuAllPciDeviceId).Value!.Value.GetString());
        Assert.Equal("01", Assert.Single(gpu.Fields, field => field.Definition == DeviceInformationFieldRegistry.PciAllRevisionIdDeclared).Value!.Value.GetString());
        Assert.Equal("8.10", Assert.Single(gpu.Fields, field => field.Definition == DeviceInformationFieldRegistry.GpuNvidiaCudaComputeCapability).Value!.Value.GetString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void DriverPropertyQueryIsExactBoundedAndFailureDoesNotBecomeAValue(int mode)
    {
        var calls = 0;
        uint Query(uint devInst, in WindowsGpuIdentityBridge.NativeMethods.DevPropKey key, out uint type,
            byte[] buffer, ref uint size, uint flags)
        {
            calls++; Assert.Equal(123U, devInst); Assert.Equal(0U, flags);
            Assert.Equal(new Guid("A8B865DD-2E3D-4094-AD97-E593A70C75D6"), key.FormatId); Assert.Equal(3U, key.PropertyId);
            type = 0x12;
            if (mode == 0) { var bytes = Encoding.Unicode.GetBytes("32.0.15.8100\0"); bytes.CopyTo(buffer, 0); size = (uint)bytes.Length; return 0; }
            if (mode == 1 && calls == 2) { Assert.Equal(600, buffer.Length); var bytes = Encoding.Unicode.GetBytes(new string('1',299)+"\0"); bytes.CopyTo(buffer,0); size=600; return 0; }
            size = mode switch { 1 => 600, 2 => 4098, 3 => 513, _ => 512 };
            return mode == 5 ? 37U : 26U;
        }
        var result = WindowsGpuIdentityBridge.ReadDriverVersion(123, Query);
        Assert.Equal(mode == 0 ? "32.0.15.8100" : mode == 1 ? new string('1', 299) : null, result);
        Assert.Equal(mode == 1 ? 2 : 1, calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void DriverVersionDecoderRejectsWrongTypeLengthTerminationAndInvalidUtf16(int mode)
    {
        var buffer = Encoding.Unicode.GetBytes("32.0.15.8100\0"); uint size = (uint)buffer.Length; uint type = 0x12;
        switch (mode) {
            case 0: type = 7; break;
            case 1: size--; break;
            case 2: size += 2; break;
            case 3: buffer[^2] = 1; break;
            case 4: buffer = Encoding.Unicode.GetBytes(" \0"); size = (uint)buffer.Length; break;
            case 5: buffer = Encoding.Unicode.GetBytes("a\0b\0"); size = (uint)buffer.Length; break;
            case 6: buffer = [0, 0xD8, 0, 0]; size = 4; break;
            case 7: size = 0; break;
        }
        Assert.Null(WindowsGpuIdentityBridge.DecodeDriverVersion(type, buffer, size));
    }

    private static WindowsGpuAdapterInventoryRead Inventory(params WindowsGpuAdapter[] adapters) =>
        new(SamplingObservationStatus.Current, 1, DateTimeOffset.UtcNow.UtcTicks, (uint)adapters.Length, 0, 0, 1, adapters);

    private static WindowsGpuAdapter Adapter(int index, string pnp, uint vendor = 0x10DE) =>
        new(index, "显卡 Fixture", vendor, 0x2684, 0x10DE, new() { LowPart = (uint)index+1 }, false, 8UL<<30, WindowsGpuAdapterKind.Dedicated) {
            PhysicalIdentityStatus = SamplingObservationStatus.Current,
            PhysicalIdentities = [new(0, pnp, WindowsGpuPnpKind.Pci, true, true, 0, 1, 0, 0, vendor, 0x2684, 0x10DE, 0, 1,
                WindowsGpuIdentityEvidence.CmPciAddress, WindowsGpuPhysicalIdentityStatus.Bound, 0) { DriverVersion = "32.0.15.8100" }]
        };
}
