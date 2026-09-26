using System.Runtime.InteropServices;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.NativeCore;

namespace Resource_Manager_APP.Tests;

public sealed class WindowsGpuIdentityBridgeTests
{
    [Fact]
    public void NativeLayoutsMatchWindowsSdkX64Contract()
    {
        Assert.Equal(8, Marshal.SizeOf<AdapterLuid>());
        Assert.Equal(
            12,
            Marshal.SizeOf<
                WindowsGpuIdentityBridge.NativeMethods
                    .OpenAdapterFromLuid>());
        Assert.Equal(
            24,
            Marshal.SizeOf<
                WindowsGpuIdentityBridge.NativeMethods
                    .QueryAdapterInfo>());
        Assert.Equal(
            4,
            Marshal.SizeOf<
                WindowsGpuIdentityBridge.NativeMethods.CloseAdapter>());
        Assert.Equal(
            12,
            Marshal.SizeOf<
                WindowsGpuIdentityBridge.NativeMethods.AdapterAddress>());
        Assert.Equal(
            4,
            Marshal.SizeOf<
                WindowsGpuIdentityBridge.NativeMethods
                    .PhysicalAdapterCount>());
        Assert.Equal(
            28,
            Marshal.SizeOf<
                WindowsGpuIdentityBridge.NativeMethods.QueryDeviceIds>());
        Assert.Equal(
            24,
            Marshal.SizeOf<
                WindowsGpuIdentityBridge.NativeMethods
                    .QueryPhysicalAdapterPnpKey>());
    }

    [Fact]
    public void LuidPackingPreservesSignedHighPartBits()
    {
        var luid = new AdapterLuid
        {
            LowPart = 0x1234_5678,
            HighPart = unchecked((int)0xFEDC_BA98)
        };

        Assert.Equal(
            0xFEDC_BA98_1234_5678UL,
            NativePdhAdapterIdentity.Pack(luid));
    }

    [Theory]
    [InlineData(
        @"\REGISTRY\MACHINE\SYSTEM\ControlSet001\Enum\PCI\VEN_10DE&DEV_2D19\ABC\Device Parameters",
        @"PCI\VEN_10DE&DEV_2D19\ABC")]
    [InlineData(
        @"\REGISTRY\MACHINE\SYSTEM\ControlSet999\Enum\ROOT\BASICRENDER\0000\Device Parameters",
        @"ROOT\BASICRENDER\0000")]
    public void HardwarePnpPathExtractsCanonicalInstanceAcrossControlSets(
        string path,
        string expected)
    {
        Assert.True(
            WindowsGpuIdentityBridge.TryExtractHardwarePnpInstance(
                path,
                out var actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("")]
    [InlineData(
        @"\REGISTRY\MACHINE\SYSTEM\ControlSet001\Enum\PCI\VEN_10DE")]
    [InlineData(
        @"\REGISTRY\MACHINE\SYSTEM\ControlSet001\Enum\\Device Parameters")]
    [InlineData(
        @"\REGISTRY\MACHINE\SYSTEM\ControlSet001\Enum\PCI\\BAD\Device Parameters")]
    [InlineData(
        @"\REGISTRY\MACHINE\SYSTEM\ControlSet001\Enum\PCI\GOOD\Device Parameters\EXTRA")]
    public void HardwarePnpPathRejectsMalformedOrAmbiguousShapes(string path)
    {
        Assert.False(
            WindowsGpuIdentityBridge.TryExtractHardwarePnpInstance(
                path,
                out var instanceId));
        Assert.Empty(instanceId);
    }

    [Fact]
    public void TopologyGenerationOnlyAdvancesWhenTopologyChanges()
    {
        var tracker = new WindowsGpuTopologyGenerationTracker();

        Assert.Equal(0UL, tracker.Current);
        Assert.Equal(1UL, tracker.Observe(0x101UL));
        Assert.Equal(1UL, tracker.Observe(0x101UL));
        Assert.Equal(1UL, tracker.Current);
        Assert.Equal(2UL, tracker.Observe(0x202UL));
        Assert.Equal(2UL, tracker.Observe(0x202UL));
    }

    [Fact]
    public void TopologyGenerationRejectsMissingFingerprint()
    {
        var tracker = new WindowsGpuTopologyGenerationTracker();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => tracker.Observe(0));
        Assert.Equal(0UL, tracker.Current);
    }

    [Fact]
    public void TopologyFingerprintCoversStablePhysicalIdentityFields()
    {
        var firstIdentity = CreatePhysicalIdentity(
            0,
            @"PCI\VEN_10DE&DEV_2D19\A");
        var secondIdentity = CreatePhysicalIdentity(
            1,
            @"PCI\VEN_10DE&DEV_2D19\B");
        var adapter = CreateAdapter(
            "NVIDIA GeForce RTX 5060 Laptop GPU",
            [firstIdentity, secondIdentity]);
        var baseline =
            NativePdhAdapterIdentity.ComputeTopologyFingerprint([adapter]);

        Assert.Equal(
            baseline,
            NativePdhAdapterIdentity.ComputeTopologyFingerprint(
                [adapter with
                {
                    PhysicalIdentities =
                        [secondIdentity, firstIdentity]
                }]));
        Assert.Equal(
            baseline,
            NativePdhAdapterIdentity.ComputeTopologyFingerprint(
                [adapter with
                {
                    PhysicalIdentities =
                    [
                        firstIdentity with
                        {
                            PnpInstanceId = firstIdentity.PnpInstanceId.ToLowerInvariant()
                        },
                        secondIdentity
                    ]
                }]));
        Assert.NotEqual(
            baseline,
            NativePdhAdapterIdentity.ComputeTopologyFingerprint(
                [adapter with
                {
                    PhysicalIdentities =
                    [
                        firstIdentity with
                        {
                            PnpInstanceId =
                                @"PCI\VEN_10DE&DEV_2D19\C"
                        },
                        secondIdentity
                    ]
                }]));
        Assert.NotEqual(
            baseline,
            NativePdhAdapterIdentity.ComputeTopologyFingerprint(
                [adapter with
                {
                    PhysicalIdentities =
                    [
                        firstIdentity with
                        {
                            RevisionId = firstIdentity.RevisionId + 1
                        },
                        secondIdentity
                    ]
                }]));
    }

    [Fact]
    public void AdapterInventorySourceContainsNoFuzzyIdentityFallback()
    {
        var path = Path.Combine(
            FindAppRoot(),
            "Infrastructure",
            "Monitoring",
            "WindowsGpuAdapterOrderReader.cs");
        var source = File.ReadAllText(path);

        Assert.DoesNotContain("FindDxgiMatch", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MatchingDeviceId", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ReadRegistryAdapters", source, StringComparison.Ordinal);
        Assert.DoesNotContain("hardwareMatches.Length == 1", source, StringComparison.Ordinal);
        Assert.DoesNotContain("nameMatches.Length == 1", source, StringComparison.Ordinal);
    }

    private static WindowsGpuAdapter CreateAdapter(
        string name,
        IReadOnlyList<WindowsGpuPhysicalIdentity> identities)
        => new(
            0,
            name,
            0x10DE,
            0x2D19,
            0x1234,
            new AdapterLuid
            {
                LowPart = 0x1020_3040,
                HighPart = 0x5060_7080
            },
            false,
            8UL * 1024 * 1024 * 1024,
            WindowsGpuAdapterKind.Dedicated)
        {
            PhysicalIdentityStatus = SamplingObservationStatus.Current,
            PhysicalIdentities = identities,
            PhysicalIdentityNativeStatus = 0
        };

    private static WindowsGpuPhysicalIdentity CreatePhysicalIdentity(
        uint index,
        string pnpInstanceId)
        => new(
            index,
            pnpInstanceId,
            WindowsGpuPnpKind.Pci,
            true,
            true,
            0,
            1,
            index,
            0,
            0x10DE,
            0x2D19,
            0x1462,
            0x1234,
            1,
            WindowsGpuIdentityEvidence.KmtLuidOpen
                | WindowsGpuIdentityEvidence.KmtHardwarePnpKey
                | WindowsGpuIdentityEvidence.CmExactDevNode
                | WindowsGpuIdentityEvidence.KmtDeviceIds,
            WindowsGpuPhysicalIdentityStatus.Bound,
            0);

    private static string FindAppRoot(
        [System.Runtime.CompilerServices.CallerFilePath]
        string sourceFilePath = "")
    {
        var sourceDirectory = Path.GetDirectoryName(sourceFilePath);
        if (!string.IsNullOrWhiteSpace(sourceDirectory))
        {
            var sourceCandidate = Path.GetFullPath(Path.Combine(
                sourceDirectory,
                "..",
                "..",
                "Resource Manager",
                "Resource Manager-APP"));
            if (File.Exists(Path.Combine(
                    sourceCandidate,
                    "ResourceManager.App.csproj")))
            {
                return sourceCandidate;
            }
        }

        throw new DirectoryNotFoundException(
            "The Resource Manager application root could not be located.");
    }
}
