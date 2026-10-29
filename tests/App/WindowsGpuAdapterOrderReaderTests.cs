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
}
