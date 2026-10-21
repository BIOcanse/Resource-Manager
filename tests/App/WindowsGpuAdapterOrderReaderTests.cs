using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Tests;

public sealed class WindowsGpuAdapterOrderReaderTests
{
    [Theory]
    [InlineData("Intel(R) Arc(TM) Graphics", 128, true)]
    [InlineData("Intel(R) Arc(TM) 140T GPU", 2048, true)]
    [InlineData("Intel Arc 140V GPU", 128, true)]
    [InlineData("Intel(R) Arc(TM) A770 Graphics", 16384, false)]
    [InlineData("Intel(R) Arc(TM) B580 Graphics", 12288, false)]
    [InlineData("Intel Arc Pro B50", 16384, false)]
    public void ArcIntegratedAndDiscreteModelsKeepTheirHardwareRole(string name, ulong memoryMib, bool integrated)
    {
        Assert.Equal(integrated,
            ResourceManager.App.Domain.Optimization.GpuPerformanceScorePresetResolver.IsLikelyIntegratedGpuName(name));
        Assert.Equal(integrated ? WindowsGpuAdapterKind.Integrated : WindowsGpuAdapterKind.Dedicated,
            WindowsGpuAdapterOrderReader.ClassifyAdapter(name, new WindowsGpuHardwareId(0x8086, 1, 0), memoryMib * 1024 * 1024));
    }

    [Fact]
    public void ApplyPublicAdapterOrder_UsesTaskManagerKmtOrdinalAndLuid()
    {
        var integrated = CreateAdapter(0, 0x10);
        var dedicated = CreateAdapter(1, 0x20);

        var result = WindowsGpuAdapterOrderReader.ApplyPublicAdapterOrder(
            [integrated, dedicated],
            new Dictionary<ulong, int>
            {
                [Pack(dedicated.Luid)] = 0,
                [Pack(integrated.Luid)] = 1
            });

        Assert.Collection(
            result,
            adapter =>
            {
                Assert.Equal(0, adapter.Index);
                Assert.Equal(dedicated.Luid.LowPart, adapter.Luid.LowPart);
                Assert.Equal(WindowsGpuPublicIndexSource.TaskManagerKmt, adapter.PublicIndexSource);
            },
            adapter =>
            {
                Assert.Equal(1, adapter.Index);
                Assert.Equal(integrated.Luid.LowPart, adapter.Luid.LowPart);
                Assert.Equal(WindowsGpuPublicIndexSource.TaskManagerKmt, adapter.PublicIndexSource);
            });
    }

    [Fact]
    public void ApplyPublicAdapterOrder_IncompleteKmtInventoryFallsBackAsOneUnit()
    {
        var integrated = CreateAdapter(3, 0x10);
        var dedicated = CreateAdapter(7, 0x20);

        var result = WindowsGpuAdapterOrderReader.ApplyPublicAdapterOrder(
            [integrated, dedicated],
            new Dictionary<ulong, int>
            {
                [Pack(dedicated.Luid)] = 0
            });

        Assert.Collection(
            result,
            adapter =>
            {
                Assert.Equal(0, adapter.Index);
                Assert.Equal(integrated.Luid.LowPart, adapter.Luid.LowPart);
                Assert.Equal(WindowsGpuPublicIndexSource.DxgiFallback, adapter.PublicIndexSource);
            },
            adapter =>
            {
                Assert.Equal(1, adapter.Index);
                Assert.Equal(dedicated.Luid.LowPart, adapter.Luid.LowPart);
                Assert.Equal(WindowsGpuPublicIndexSource.DxgiFallback, adapter.PublicIndexSource);
            });
    }

    [Fact]
    public void PhysicalTopologyFingerprintIgnoresEnumerationOrderAndDisplayMetadata()
    {
        var first = CreateAdapter(0, 0x10);
        var second = CreateAdapter(1, 0x20);
        var reordered = new[]
        {
            second with { Index = 7, Name = "Renamed second GPU" },
            first with { Index = 3, Name = "Renamed first GPU" }
        };

        Assert.Equal(
            WindowsGpuAdapterIdentity.ComputeTopologyFingerprint(
                [first, second]),
            WindowsGpuAdapterIdentity.ComputeTopologyFingerprint(reordered));
        Assert.NotEqual(
            WindowsGpuAdapterIdentity.ComputeTopologyFingerprint(
                [first, second]),
            WindowsGpuAdapterIdentity.ComputeTopologyFingerprint(
                [first, second with
                {
                    Luid = new AdapterLuid
                    {
                        LowPart = 0x30,
                        HighPart = 0
                    }
                }]));
    }

    private static WindowsGpuAdapter CreateAdapter(int index, uint luidLow)
    {
        return new WindowsGpuAdapter(
            index,
            $"GPU {index}",
            VendorId: 0x10DE,
            DeviceId: checked((uint)index + 1),
            SubSystemId: 1,
            new AdapterLuid { LowPart = luidLow, HighPart = 0 },
            IsSoftware: false,
            DedicatedVideoMemoryBytes: 8UL * 1024 * 1024 * 1024,
            WindowsGpuAdapterKind.Dedicated);
    }

    private static ulong Pack(AdapterLuid luid)
    {
        return ((ulong)(uint)luid.HighPart << 32) | luid.LowPart;
    }
}
