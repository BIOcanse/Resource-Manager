using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Tests;

public sealed class WindowsGpuAdapterOrderReaderTests
{
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
            NativePdhAdapterIdentity.ComputeTopologyFingerprint(
                [first, second]),
            NativePdhAdapterIdentity.ComputeTopologyFingerprint(reordered));
        Assert.NotEqual(
            NativePdhAdapterIdentity.ComputeTopologyFingerprint(
                [first, second]),
            NativePdhAdapterIdentity.ComputeTopologyFingerprint(
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
