using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Vendors.Nvidia;
using ResourceManager.ReadWrite.Windows.Metrics.Resources;

namespace Resource_Manager_APP.Tests;

public sealed class MonitoringNativeSessionLifetimeTests
{
    [Fact]
    public void NvmlRepeatedReadsRetainInitializationUntilDispose()
    {
        var initializations = 0;
        var shutdowns = 0;
        var reader = new NvidiaNvmlReader(() => { initializations++; return 0; },
            () => { shutdowns++; return 0; });
        using var zone = new NvidiaNvmlMonitoringZone(reader);
        Assert.Equal(0, initializations);
        for (var cycle = 0; cycle < 3; cycle++)
        {
            Assert.True(reader.EnsureInitialized());
            Assert.True(reader.EnsureInitialized());
            Assert.Equal(1, initializations);
            Assert.Equal(0, shutdowns);
        }
        zone.Dispose();
        zone.Dispose();
        Assert.Equal(1, shutdowns);
        Assert.Throws<ObjectDisposedException>(() => reader.EnsureInitialized());
    }

    [Fact]
    public void NvmlFailedShutdownRetainsOwnedReferenceForExplicitRetry()
    {
        var initializations = 0;
        var shutdowns = 0;
        var reader = new NvidiaNvmlReader(() => { initializations++; return 0; },
            () => ++shutdowns == 1 ? 3 : 0);
        Assert.True(reader.EnsureInitialized());
        Assert.Throws<InvalidOperationException>(reader.ReleaseSession);
        Assert.True(reader.EnsureInitialized());
        Assert.Equal(1, initializations);
        reader.ReleaseSession();
        Assert.True(reader.EnsureInitialized());
        Assert.Equal(2, initializations);
        reader.Dispose();
        reader.Dispose();
        Assert.Equal(3, shutdowns);
    }

    [Fact]
    public void FrequencyRepeatedReadsRetainQueryUntilDispose()
    {
        var held = false;
        var opens = 0;
        var closes = 0;
        using var zone = new PdhCpuFrequencyMonitoringZone(() =>
        {
            if (!held) { held = true; opens++; }
            return new CpuFrequency(2000, 4000, 50, "recorded");
        }, () => { if (held) { held = false; closes++; } });
        for (var cycle = 0; cycle < 3; cycle++)
        {
            Assert.Equal(2000, zone.ReadFrequency().CurrentMhz);
            Assert.Equal(2000, zone.ReadFrequency().CurrentMhz);
            Assert.Equal(1, opens);
            Assert.True(held);
            Assert.Equal(0, closes);
        }
        _ = zone.ReadFrequency();
        zone.Dispose();
        Assert.False(held);
        Assert.Equal(1, closes);
    }
}
