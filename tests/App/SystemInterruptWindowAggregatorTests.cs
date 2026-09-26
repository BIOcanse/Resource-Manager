using ResourceManager.App.Domain.SystemHealth;
using ResourceManager.App.Infrastructure.SystemHealth.Interrupts;

namespace ResourceManager.App.Tests;

public sealed class SystemInterruptWindowAggregatorTests
{
    [Fact]
    public void CreateSnapshot_UsesCpuCapacityAndGroupsDrivers()
    {
        var now = AlignedNow();
        var aggregator = new SystemInterruptWindowAggregator();
        foreach (var sample in new[]
        {
            Event(now.AddSeconds(-55), SystemInterruptEventKinds.Dpc, 1.5, "driver-a.sys"),
            Event(now.AddSeconds(-45), SystemInterruptEventKinds.Isr, 2.0, "driver-a.sys"),
            Event(now.AddSeconds(-35), SystemInterruptEventKinds.TimerDpc, 3.0, "driver-b.sys")
        })
        {
            aggregator.Observe(sample);
        }

        var snapshot = aggregator.CreateSnapshot(
            now,
            1,
            now.AddSeconds(-60),
            4,
            new SystemInterruptProviderState("test", "Running", "test"));

        Assert.True(snapshot.Available);
        Assert.Equal(6.5, snapshot.TotalDurationMilliseconds, 3);
        Assert.Equal(3, snapshot.MaximumSingleDurationMilliseconds, 3);
        Assert.Equal(3, snapshot.EventsAtOrAboveOneMillisecond);
        Assert.Equal(6, snapshot.Buckets.Count);
        Assert.Equal(6.5 * 100 / (60_000 * 4), snapshot.CpuCapacityPercent, 4);
        Assert.Collection(
            snapshot.Drivers,
            driver =>
            {
                Assert.Equal("driver-a.sys", driver.ModuleName);
                Assert.Equal(3.5, driver.TotalDurationMilliseconds, 3);
                Assert.Equal(2, driver.EventCount);
            },
            driver =>
            {
                Assert.Equal("driver-b.sys", driver.ModuleName);
                Assert.Equal(3, driver.TotalDurationMilliseconds, 3);
                Assert.Equal(1, driver.EventCount);
            });
    }

    [Fact]
    public void CreateSnapshot_StaysUnavailableUntilFirstCompleteBucket()
    {
        var now = AlignedNow();
        var aggregator = new SystemInterruptWindowAggregator();
        aggregator.Observe(Event(now.AddSeconds(-1), SystemInterruptEventKinds.Dpc, 20, "driver.sys"));
        var snapshot = aggregator.CreateSnapshot(
            now,
            1,
            now.AddSeconds(-9),
            8,
            new SystemInterruptProviderState("test", "Running", "test"));

        Assert.False(snapshot.Available);
        Assert.Equal("Warming", snapshot.ProviderState.State);
        Assert.Empty(snapshot.Buckets);
    }

    [Fact]
    public void KernelModuleMap_ResolvesMostSpecificContainingImage()
    {
        var modules = new KernelModuleAddressMap();
        modules.Upsert(0x1000, 0x1000, @"C:\Windows\System32\drivers\outer.sys");
        modules.Upsert(0x1800, 0x100, @"C:\Windows\System32\drivers\inner.sys");

        var inner = modules.Resolve(0x1850);
        var outer = modules.Resolve(0x1200);
        var unknown = modules.Resolve(0x4000);

        Assert.Equal("inner.sys", inner.Name);
        Assert.Equal("outer.sys", outer.Name);
        Assert.Equal(KernelModuleAddressMap.UnknownModuleName, unknown.Name);
    }

    private static SystemInterruptEventSample Event(
        DateTimeOffset observedAt,
        string kind,
        double durationMilliseconds,
        string moduleName)
    {
        return new SystemInterruptEventSample(
            observedAt,
            kind,
            durationMilliseconds,
            0x1000,
            moduleName,
            $@"C:\Windows\System32\drivers\{moduleName}");
    }

    private static DateTimeOffset AlignedNow()
    {
        var now = DateTimeOffset.UtcNow;
        var bucketSeconds = (long)SystemInterruptWindowAggregator.BucketDuration.TotalSeconds;
        return DateTimeOffset.FromUnixTimeSeconds(now.ToUnixTimeSeconds() / bucketSeconds * bucketSeconds);
    }
}
