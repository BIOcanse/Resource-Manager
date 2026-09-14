using System.Runtime.InteropServices;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.Monitoring;

namespace Resource_Manager_APP.Tests;

public sealed class WindowsGpuProviderIdentityResolverTests
{
    [Fact]
    public void NvmlBdfResolvesOnlyTheExactPhysicalMember()
    {
        var inventory = CreateInventory(
            CreateAdapter(
                index: 0,
                luidLow: 1,
                pnp: @"PCI\VEN_10DE&DEV_2D19&SUBSYS_00000000\A",
                bus: 1,
                device: 0,
                function: 0,
                vendorId: 0x10DE,
                deviceId: 0x2D19,
                subSystemId: 0x3412_10DE),
            CreateAdapter(
                index: 1,
                luidLow: 2,
                pnp: @"PCI\VEN_10DE&DEV_2684&SUBSYS_00000000\B",
                bus: 2,
                device: 0,
                function: 0,
                vendorId: 0x10DE,
                deviceId: 0x2684,
                subSystemId: 0x7856_10DE));
        var pci = new NvmlPciInfo
        {
            BusId = "00000000:02:00.0",
            BusIdLegacy = "0000:02:00.0",
            Domain = 0,
            Bus = 2,
            Device = 0,
            PciDeviceId = 0x2684_10DE,
            PciSubSystemId = 0x7856_10DE
        };

        Assert.True(
            WindowsGpuProviderIdentityResolver.TryParseNvmlPciEvidence(
                pci,
                out var evidence));
        var match = WindowsGpuProviderIdentityResolver.ResolvePci(
            inventory,
            evidence);

        Assert.True(match.IsCurrent);
        Assert.Equal(1, match.Binding.Adapter.Index);
        Assert.Equal(
            @"PCI\VEN_10DE&DEV_2684&SUBSYS_00000000\B",
            match.Binding.PhysicalIdentity.PnpInstanceId);
    }

    [Fact]
    public void DuplicateBdfWithoutProvableSegmentIsConflict()
    {
        var inventory = CreateInventory(
            CreateAdapter(
                index: 0,
                luidLow: 1,
                pnp: @"PCI\VEN_10DE&DEV_2684\A",
                bus: 2,
                device: 0,
                function: 0,
                vendorId: 0x10DE,
                deviceId: 0x2684,
                subSystemId: 0),
            CreateAdapter(
                index: 1,
                luidLow: 2,
                pnp: @"PCI\VEN_10DE&DEV_2684\B",
                bus: 2,
                device: 0,
                function: 0,
                vendorId: 0x10DE,
                deviceId: 0x2684,
                subSystemId: 0));
        Assert.True(
            WindowsGpuProviderIdentityResolver.TryCreateNvapiPciEvidence(
                bus: 2,
                slot: 0,
                combinedDeviceId: 0x2684_10DE,
                subSystemId: 0,
                out var evidence));

        var match = WindowsGpuProviderIdentityResolver.ResolvePci(
            inventory,
            evidence);

        Assert.Equal(
            WindowsGpuProviderIdentityMatchStatus.Conflict,
            match.Status);
    }

    [Fact]
    public void PnpJoinIsCanonicalAndExact()
    {
        var inventory = CreateInventory(
            CreateAdapter(
                index: 3,
                luidLow: 4,
                pnp: @"PCI\VEN_1002&DEV_164E\4&ABC&0&0041",
                bus: 5,
                device: 0,
                function: 0,
                vendorId: 0x1002,
                deviceId: 0x164E,
                subSystemId: 0));

        var match = WindowsGpuProviderIdentityResolver.ResolvePnp(
            inventory,
            " pci/ven_1002&dev_164e/4&abc&0&0041 ");

        Assert.True(match.IsCurrent);
        Assert.Equal(3, match.Binding.Adapter.Index);
    }

    [Fact]
    public void IncompleteInventoryNeverProducesProviderBinding()
    {
        var complete = CreateInventory(
            CreateAdapter(
                index: 0,
                luidLow: 1,
                pnp: @"PCI\VEN_10DE&DEV_2684\A",
                bus: 2,
                device: 0,
                function: 0,
                vendorId: 0x10DE,
                deviceId: 0x2684,
                subSystemId: 0));
        var incomplete = complete with { SkippedCount = 1 };
        Assert.True(
            WindowsGpuProviderIdentityResolver.TryCreateNvapiPciEvidence(
                bus: 2,
                slot: 0,
                combinedDeviceId: 0x2684_10DE,
                subSystemId: 0,
                out var evidence));

        var match = WindowsGpuProviderIdentityResolver.ResolvePci(
            incomplete,
            evidence);

        Assert.Equal(
            WindowsGpuProviderIdentityMatchStatus.Unavailable,
            match.Status);
    }

    [Fact]
    public void AdlxBridgeAndManagedLayoutExposeVersionedPnpIdentity()
    {
        Assert.Equal(2U, AmdAdlxBridgeReader.BridgeAbiVersion);
        Assert.Equal(
            512,
            Marshal.SizeOf<AdlxFixedString512>());
        Assert.True(
            Marshal.OffsetOf<NativeAdlxGpuMetrics>(
                nameof(NativeAdlxGpuMetrics.PnpString)).ToInt32()
            > Marshal.OffsetOf<NativeAdlxGpuMetrics>(
                nameof(NativeAdlxGpuMetrics.DeviceId)).ToInt32());

        var appRoot = FindAppRoot();
        var managed = File.ReadAllText(Path.Combine(
            appRoot,
            "Infrastructure",
            "Monitoring",
            "AmdAdlxBridgeReader.cs"));
        var native = File.ReadAllText(Path.Combine(
            appRoot,
            "Native",
            "AdlxBridge",
            "ResourceManagerAdlxBridge.c"));

        Assert.Contains(
            "ResourceManagerAdlxGetAbiVersion",
            managed,
            StringComparison.Ordinal);
        Assert.Contains(
            "gpu->pVtbl->PNPString",
            native,
            StringComparison.Ordinal);
        Assert.Contains(
            "RM_ADLX_BRIDGE_ABI_VERSION 2",
            native,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderReadersContainNoFuzzyOrOrdinalFallback()
    {
        var monitoringRoot = Path.Combine(
            FindAppRoot(),
            "Infrastructure",
            "Monitoring");
        var files = new[]
        {
            "NvidiaNvmlReader.Device.cs",
            "NvidiaNvapiReader.Device.cs",
            "NvidiaNvapiReader.Sensors.cs",
            "AmdAdlxBridgeReader.cs",
            "WindowsHardwareMetricSampler.NativeCollection.cs"
        };
        var source = string.Concat(
            files.Select(file => File.ReadAllText(Path.Combine(
                monitoringRoot,
                file))));

        Assert.DoesNotContain(
            "NextFreeDisplayIndex",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ResolveWindowsDisplayIndex",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ResolveWindowsAdapter",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "nameMatches",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "containsNameMatches",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ElementAtOrDefault(gpuIndex)",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "SharedMeaningfulGpuToken",
            source,
            StringComparison.Ordinal);
    }

    private static WindowsGpuAdapterInventoryRead CreateInventory(
        params WindowsGpuAdapter[] adapters)
    {
        return new WindowsGpuAdapterInventoryRead(
            SamplingObservationStatus.Current,
            Generation: 7,
            ObservedAtUtcTicks: DateTimeOffset.UtcNow.UtcTicks,
            ObservedCount: checked((uint)adapters.Length),
            SkippedCount: 0,
            OverflowCount: 0,
            TopologyFingerprint: 123,
            adapters);
    }

    private static WindowsGpuAdapter CreateAdapter(
        int index,
        uint luidLow,
        string pnp,
        uint bus,
        uint device,
        uint function,
        uint vendorId,
        uint deviceId,
        uint subSystemId)
    {
        var physical = new WindowsGpuPhysicalIdentity(
            PhysicalAdapterIndex: 0,
            pnp,
            WindowsGpuPnpKind.Pci,
            PciAddressValid: true,
            PciSegmentValid: false,
            PciSegment: 0,
            bus,
            device,
            function,
            vendorId,
            deviceId,
            SubVendorId: subSystemId & 0xFFFF,
            SubsystemId: subSystemId >> 16,
            RevisionId: 1,
            WindowsGpuIdentityEvidence.KmtLuidOpen
                | WindowsGpuIdentityEvidence.KmtHardwarePnpKey
                | WindowsGpuIdentityEvidence.CmExactDevNode
                | WindowsGpuIdentityEvidence.CmCanonicalInstanceRoundTrip
                | WindowsGpuIdentityEvidence.KmtDeviceIds
                | WindowsGpuIdentityEvidence.CmPciAddress,
            WindowsGpuPhysicalIdentityStatus.Bound,
            NativeStatus: 0);
        return new WindowsGpuAdapter(
            index,
            $"GPU {index}",
            vendorId,
            deviceId,
            subSystemId,
            new AdapterLuid { LowPart = luidLow, HighPart = 0 },
            IsSoftware: false,
            DedicatedVideoMemoryBytes: 8UL * 1024 * 1024 * 1024,
            WindowsGpuAdapterKind.Dedicated)
        {
            PhysicalIdentityStatus = SamplingObservationStatus.Current,
            PhysicalIdentities = [physical],
            PhysicalIdentityNativeStatus = 0
        };
    }

    private static string FindAppRoot(
        [System.Runtime.CompilerServices.CallerFilePath]
        string sourceFilePath = "")
    {
        var sourceDirectory = Path.GetDirectoryName(sourceFilePath);
        if (!string.IsNullOrWhiteSpace(sourceDirectory))
        {
            var candidate = Path.GetFullPath(Path.Combine(
                sourceDirectory,
                "..",
                "Resource Manager-APP"));
            if (File.Exists(Path.Combine(
                    candidate,
                    "ResourceManager.App.csproj")))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException(
            "The Resource Manager application root could not be located.");
    }
}
