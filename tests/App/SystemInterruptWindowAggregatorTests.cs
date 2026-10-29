using ResourceManager.App.Domain.SystemHealth;
using ResourceManager.App.Infrastructure.SystemHealth.Interrupts;

namespace ResourceManager.App.Tests;

public sealed class SystemInterruptWindowAggregatorTests
{
    [Fact]
    public void MappedInterruptHotPathReusesIdentityAndDoesNotAllocatePerEvent()
    {
        var modules = new KernelModuleAddressMap();
        modules.Upsert(0x1000, 0x1000, "driver.sys");
        var identity = modules.Resolve(0x1010);
        Assert.Same(identity, modules.Resolve(0x1020));
        var aggregator = new SystemInterruptWindowAggregator();
        aggregator.Observe(new(1000, SystemInterruptEventKinds.Dpc, 2, 0x1010, identity.Name, identity.Path));
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 20_000; index++)
        {
            var module = modules.Resolve(0x1010);
            aggregator.Observe(new(1001, SystemInterruptEventKinds.Dpc, 2, 0x1010, module.Name, module.Path));
        }
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 1024);
    }

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
            now.ToUnixTimeMilliseconds(),
            1,
            now.AddSeconds(-60).ToUnixTimeMilliseconds(),
            4,
            new SystemInterruptProviderState("test", SystemInterruptProviderStates.Running, "test"));

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
            now.ToUnixTimeMilliseconds(),
            1,
            now.AddSeconds(-9).ToUnixTimeMilliseconds(),
            8,
            new SystemInterruptProviderState("test", SystemInterruptProviderStates.Running, "test"));

        Assert.False(snapshot.Available);
        Assert.Equal(SystemInterruptProviderStates.Warming, snapshot.ProviderState.State);
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

    [Fact]
    public void KernelModuleMap_ResolvesUnorderedLoadsReplacementsAndRemovals()
    {
        const ulong kernelBase = 0xFFFF_F800_0000_0000UL;
        var modules = new KernelModuleAddressMap();
        var order = Enumerable.Range(0, 300).OrderBy(index => (index * 7919) % 300).ToArray();
        foreach (var index in order)
        {
            modules.Upsert(kernelBase + (ulong)index * 0x10000, 0x8000, $@"C:\Windows\System32\drivers\d{index:000}.sys");
        }

        foreach (var index in order)
        {
            var start = kernelBase + (ulong)index * 0x10000;
            Assert.Equal($"d{index:000}.sys", modules.Resolve(start).Name);
            Assert.Equal($"d{index:000}.sys", modules.Resolve(start + 0x7FFF).Name);
            Assert.Equal(KernelModuleAddressMap.UnknownModuleName, modules.Resolve(start + 0x8000).Name);
        }

        modules.Upsert(kernelBase + 50 * 0x10000, 0x8000, @"C:\Windows\System32\drivers\replaced.sys");
        modules.Remove(kernelBase + 100 * 0x10000);
        modules.Remove(kernelBase + 0x1234);
        Assert.Equal("replaced.sys", modules.Resolve(kernelBase + 50 * 0x10000 + 0x10).Name);
        Assert.Equal(KernelModuleAddressMap.UnknownModuleName, modules.Resolve(kernelBase + 100 * 0x10000 + 0x10).Name);
        Assert.Equal("d101.sys", modules.Resolve(kernelBase + 101 * 0x10000 + 0x10).Name);
        Assert.Equal(KernelModuleAddressMap.UnknownModuleName, modules.Resolve(kernelBase - 1).Name);
        Assert.Equal(KernelModuleAddressMap.UnknownModuleName, modules.Resolve(0).Name);

        modules.Clear();
        Assert.Equal(KernelModuleAddressMap.UnknownModuleName, modules.Resolve(kernelBase + 0x10).Name);
    }

    [Fact]
    public void KernelModuleMap_WalksPastNestedImagesOnlyWhileAnEarlierImageReaches()
    {
        var modules = new KernelModuleAddressMap();
        modules.Upsert(0x1000, 0x2000, @"C:\Windows\System32\drivers\outer.sys");
        modules.Upsert(0x1800, 0x100, @"C:\Windows\System32\drivers\inner.sys");
        modules.Upsert(0x4000, 0x1000, @"C:\Windows\System32\drivers\next.sys");

        Assert.Equal("outer.sys", modules.Resolve(0x2000).Name);
        Assert.Equal(KernelModuleAddressMap.UnknownModuleName, modules.Resolve(0x3500).Name);
        Assert.Equal("next.sys", modules.Resolve(0x4FFF).Name);
        Assert.Equal(KernelModuleAddressMap.UnknownModuleName, modules.Resolve(0x5000).Name);
        Assert.True(KernelModuleAddressMap.IsKernelAddress(0xFFFF_8000_0000_0000UL));
        Assert.False(KernelModuleAddressMap.IsKernelAddress(0x7FFF_FFFF_FFFFUL));
    }

    [Fact]
    public void KernelModuleMap_ImageReachingTheTopOfTheAddressSpaceCoversItsLastAddress()
    {
        var modules = new KernelModuleAddressMap();
        modules.Upsert(ulong.MaxValue - 3, 10, @"C:\Windows\System32\drivers\last.sys");
        modules.Upsert(ulong.MaxValue - 1, 1, @"C:\Windows\System32\drivers\inner.sys");

        Assert.Equal("inner.sys", modules.Resolve(ulong.MaxValue - 1).Name);
        Assert.Equal("last.sys", modules.Resolve(ulong.MaxValue).Name);
        Assert.Equal(KernelModuleAddressMap.UnknownModuleName, modules.Resolve(ulong.MaxValue - 4).Name);
    }

    private static SystemInterruptEventSample Event(
        DateTimeOffset observedAt,
        string kind,
        double durationMilliseconds,
        string moduleName)
    {
        return new SystemInterruptEventSample(
            observedAt.ToUnixTimeMilliseconds(),
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
