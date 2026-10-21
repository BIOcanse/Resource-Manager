using ResourceManager.ReadWrite.Native.Pdh;
using ResourceManager.ReadWrite.Vendors.Nvidia;
using ResourceManager.ReadWrite.Windows.Metrics.Resources;

namespace Resource_Manager_APP.Tests;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class NvidiaDrsDefaultCompositionTests
{
    [Fact]
    public void DefaultCompositionUsesAndOwnsTheSameOriginalNvapiReader()
    {
        using var resources = WindowsHardwareMetricResources.CreateDefault(
            new NativePdhSessionOptions(TimeSpan.Zero, TimeSpan.Zero, 1000));
        var reader = Assert.IsType<NvidiaNvapiReader>(resources.ReadNvapiAsync.Target);
        Assert.Same(reader, resources.ReadNvapiDrs.Target);
        Assert.Same(reader, resources.DiscoverNvapiDrs.Target);
        resources.Dispose();
        // An empty selection requires no initialization or native query, but still rejects a closed reader.
        Assert.Throws<ObjectDisposedException>(() => reader.ReadDrs([], new(0, 0, 0, 0, 0)));
    }
}
