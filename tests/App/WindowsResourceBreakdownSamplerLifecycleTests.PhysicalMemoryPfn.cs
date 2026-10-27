using System.Diagnostics;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Infrastructure.ResourceBreakdown;

namespace Resource_Manager_APP.Tests;

public sealed partial class WindowsResourceBreakdownSamplerLifecycleTests
{
    [Fact]
    public async Task PhysicalResidualDetailsUseIndependentDemandAndMinimumWhileCachedReadsDoNoWalk()
    {
        using var subscriptions = new HostManagerSamplingSubscriptionTestFixture();
        using var catalog = new VersionedProcessAttributionCatalogProvider();
        var api = new ProcessBasicFieldCollectorTests.Api { MemoryValue = new(1000,700,900,300) };
        using var sampler = new WindowsResourceBreakdownSampler(new StaticMetricSampler(CreateMemorySnapshot()), catalog,
            new StaticResourceResidualBreakdownProvider(), null!, null!, subscriptions.Provider, subscriptions.Owner,
            new PdhProcessGpuReader(), new DelegateWindowsProcessInventoryReader(static () => [Process.GetCurrentProcess()]),
            new WindowsProcessBasicMetricReader(api));
        var walks = 0;
        sampler.PhysicalMemoryPfnCapture = (attributed, token) =>
        {
            Assert.Equal(300UL, attributed);
            Interlocked.Increment(ref walks);
            return PhysicalMemoryPfnTests.Frame();
        };
        var request = ResourceBreakdownSampleRequest.ForResourceTable([PhysicalMemoryResidualFrame.MetricId], new Dictionary<string,string>());
        Assert.Equal(TimeSpan.FromSeconds(10), sampler.PhysicalMemoryDetailsInterval(TimeSpan.FromMilliseconds(100)));
        Assert.Equal(TimeSpan.FromSeconds(30), sampler.PhysicalMemoryDetailsInterval(TimeSpan.FromSeconds(30)));
        await sampler.StartAsync(default);
        try
        {
            Assert.Null(sampler.ReadLatest(request).PhysicalMemoryResidual);
            await Assert.ThrowsAsync<InvalidOperationException>(() => sampler.CaptureSnapshotAsync(request,default));
            using (sampler.AcquireSubscription("ordinary-private", ResourceBreakdownSampleRequest.ForResourceTable(
                ["process_all_private_working_set_actual"],new Dictionary<string,string>()), TimeSpan.FromMilliseconds(100)))
            {
                await Task.Delay(200);
                Assert.Equal(0,Volatile.Read(ref walks));
                using (sampler.AcquireSubscription("pfn-details",request,TimeSpan.FromMilliseconds(100)))
                {
                    var until=DateTime.UtcNow.AddSeconds(12);
                    while(sampler.ReadLatest(request).PhysicalMemoryResidual is null && DateTime.UtcNow<until) await Task.Delay(20);
                    Assert.NotNull(sampler.ReadLatest(request).PhysicalMemoryResidual);
                    Assert.Equal(1,Volatile.Read(ref walks));
                    await Task.Delay(300); // the faster ordinary field continues; PFNs are not due
                    sampler.ReadLatest(request);
                    sampler.ReadLatest(request);
                    Assert.Equal(1,Volatile.Read(ref walks));
                }
                await Task.Delay(300);
                Assert.Equal(1,Volatile.Read(ref walks));
            }
        }
        finally { await sampler.StopAsync(default); }
    }
}
