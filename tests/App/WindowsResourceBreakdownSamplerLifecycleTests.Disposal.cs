using System.Reflection;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.ResourceBreakdown;

namespace Resource_Manager_APP.Tests;

public sealed partial class WindowsResourceBreakdownSamplerLifecycleTests
{
    [Fact]
    public async Task FinalDisposeClearsOutstandingRegistrationsClosesStreamsAndIgnoresLateLeaseReturns()
    {
        using var subscriptions = new HostManagerSamplingSubscriptionTestFixture();
        using var catalog = new VersionedProcessAttributionCatalogProvider();
        using var sampler = new WindowsResourceBreakdownSampler(new StaticMetricSampler(CreateMemorySnapshot()), catalog,
            new StaticResourceResidualBreakdownProvider(), null!, null!, subscriptions.Provider, subscriptions.Owner,
            new PdhProcessGpuReader(), new DelegateWindowsProcessInventoryReader(static () => []));
        var request = ResourceBreakdownSampleRequest.ForResourceTable([ResourceBreakdownMetricIds.MemoryUsage],
            new Dictionary<string, string>());
        using var lease = sampler.AcquireSubscription("dispose-outstanding", request, TimeSpan.FromSeconds(1));
        await using var stream = sampler.SubscribeAsync("dispose-stream", request, TimeSpan.FromSeconds(1),
            CancellationToken.None).GetAsyncEnumerator();
        var pending = stream.MoveNextAsync().AsTask();
        var tracker = ResourceTrackerForFinalDispose(sampler);
        Assert.Equal((2, 2), tracker.CaptureManagedSourceState());
        Assert.True(subscriptions.Owner.Activity.HasInUseSubscriptions(3));

        sampler.Dispose();

        Assert.Throws<ObjectDisposedException>(() => sampler.AcquireSubscription(
            "new-after-dispose", request, TimeSpan.FromSeconds(1)));
        Assert.Equal((0, 0), tracker.CaptureManagedSourceState());
        Assert.Equal((0U, 0U), ReadResourceSubscriptionCounts(subscriptions.Owner));
        Assert.False(subscriptions.Owner.Activity.HasInUseSubscriptions(3));
        Assert.False(await pending.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Null(Record.Exception(lease.Dispose));
        Assert.Null(Record.Exception(lease.Dispose));
        Assert.Null(Record.Exception(sampler.Dispose));
        Assert.Equal((0, 0), tracker.CaptureManagedSourceState());
    }

    [Fact]
    public void FinalDisposeRetriesSubscriptionClearBeforeClosingRemainingStorage()
    {
        using var subscriptions = new HostManagerSamplingSubscriptionTestFixture();
        using var catalog = new VersionedProcessAttributionCatalogProvider();
        using var sampler = new WindowsResourceBreakdownSampler(new StaticMetricSampler(CreateMemorySnapshot()), catalog,
            new StaticResourceResidualBreakdownProvider(), null!, null!, subscriptions.Provider, subscriptions.Owner,
            new PdhProcessGpuReader(), new DelegateWindowsProcessInventoryReader(static () => []));
        var request = ResourceBreakdownSampleRequest.ForResourceTable([ResourceBreakdownMetricIds.MemoryUsage],
            new Dictionary<string, string>());
        using var lease = sampler.AcquireSubscription("clear-pending", request, TimeSpan.FromSeconds(1));
        var tracker = ResourceTrackerForFinalDispose(sampler);
        var plan = tracker.CreatePlan(DateTimeOffset.UtcNow);
        Assert.NotNull(plan.Request);
        try
        {
            Assert.Throws<InvalidOperationException>(sampler.Dispose);
            Assert.Equal(0, typeof(WindowsResourceBreakdownSampler)
                .GetField("ownedResourcesDisposed", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(sampler));
            var gate = Assert.IsType<SemaphoreSlim>(typeof(WindowsResourceBreakdownSampler)
                .GetProperty("sampleGate", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(sampler));
            Assert.True(gate.Wait(0));
            gate.Release();
        }
        finally { tracker.MarkSampled(plan.Request!, DateTimeOffset.UtcNow); }

        sampler.Dispose();
        Assert.Equal((0, 0), tracker.CaptureManagedSourceState());
        Assert.Equal((0U, 0U), ReadResourceSubscriptionCounts(subscriptions.Owner));
        Assert.False(subscriptions.Owner.Activity.HasInUseSubscriptions(3));
        Assert.Null(Record.Exception(lease.Dispose));
        Assert.Null(Record.Exception(sampler.Dispose));
    }

    private static NativeItemSamplingSubscriptionTracker<ResourceBreakdownSampleRequest> ResourceTrackerForFinalDispose(
        WindowsResourceBreakdownSampler sampler)
        => Assert.IsType<NativeItemSamplingSubscriptionTracker<ResourceBreakdownSampleRequest>>(
            typeof(WindowsResourceBreakdownSampler).GetField("subscriptions", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(sampler));
}
