using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Infrastructure.ResourceBreakdown;

namespace Resource_Manager_APP.Tests;

public sealed partial class WindowsResourceBreakdownSamplerLifecycleTests
{
    [Fact]
    public async Task SingleFetchSamplesWithoutSmartOrUiAndEachCallReleasesItsDemand()
    {
        using var subscriptions = new HostManagerSamplingSubscriptionTestFixture();
        using var catalog = new VersionedProcessAttributionCatalogProvider();
        var hardware = new StaticMetricSampler(CreateMemorySnapshot());
        var inventoryReads = 0;
        using var sampler = new WindowsResourceBreakdownSampler(hardware, catalog,
            new StaticResourceResidualBreakdownProvider(), null!, null!, subscriptions.Provider,
            subscriptions.Owner, new PdhProcessGpuReader(),
            new DelegateWindowsProcessInventoryReader(() => { Interlocked.Increment(ref inventoryReads); return []; }));
        var request = ResourceBreakdownSampleRequest.ForResourceTable(
            [ResourceBreakdownMetricIds.MemoryUsage], new Dictionary<string, string>());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Assert.Equal(0U, ReadResourceSubscriptionCounts(subscriptions.Owner).ActiveSourceCount);
        await sampler.StartAsync(timeout.Token);
        try
        {
            var firstStarted = DateTimeOffset.UtcNow;
            var first = await sampler.GetSnapshotAsync(request, timeout.Token);
            Assert.Equal(1d, Assert.Single(first.Bars).TotalValue);
            Assert.Equal(ResourceBreakdownSamplingStatuses.Ready, first.Sampling.Status);
            Assert.True(Assert.Single(first.Datasets).LastAttemptAt >= firstStarted);
            Assert.True(inventoryReads > 0);
            Assert.Equal(catalog.CaptureCount, catalog.ReturnedLeases);
            Assert.Equal(0, hardware.ActiveSubscriptions);
            Assert.Equal(0U, ReadResourceSubscriptionCounts(subscriptions.Owner).ActiveSourceCount);

            var captureCount = inventoryReads;
            var secondStarted = DateTimeOffset.UtcNow;
            var second = await sampler.GetSnapshotAsync(request, timeout.Token);
            Assert.Equal(1d, Assert.Single(second.Bars).TotalValue);
            Assert.True(Assert.Single(second.Datasets).LastAttemptAt >= secondStarted);
            Assert.True(second.Sampling.LastAttemptAt > first.Sampling.LastAttemptAt);
            Assert.True(inventoryReads > captureCount);
            Assert.Equal(catalog.CaptureCount, catalog.ReturnedLeases);
            Assert.Equal(0, hardware.ActiveSubscriptions);
            Assert.Equal(0U, ReadResourceSubscriptionCounts(subscriptions.Owner).ActiveSourceCount);
        }
        finally { await sampler.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task SingleFetchCancellationWithWorkerStoppedReleasesItsDemand()
    {
        using var subscriptions = new HostManagerSamplingSubscriptionTestFixture();
        using var catalog = new VersionedProcessAttributionCatalogProvider();
        var hardware = new StaticMetricSampler(CreateMemorySnapshot());
        using var sampler = new WindowsResourceBreakdownSampler(hardware, catalog,
            new StaticResourceResidualBreakdownProvider(), null!, null!, subscriptions.Provider,
            subscriptions.Owner, new PdhProcessGpuReader(),
            new DelegateWindowsProcessInventoryReader(static () => []));
        await sampler.StartAsync(CancellationToken.None);
        await sampler.StopAsync(CancellationToken.None);
        var request = ResourceBreakdownSampleRequest.ForResourceTable(
            [ResourceBreakdownMetricIds.MemoryUsage], new Dictionary<string, string>());
        using var cancellation = new CancellationTokenSource();
        var pending = sampler.GetSnapshotAsync(request, cancellation.Token);

        Assert.False(pending.IsCompleted);
        Assert.Equal(1, hardware.ActiveSubscriptions);
        Assert.Equal(1U, ReadResourceSubscriptionCounts(subscriptions.Owner).ActiveSourceCount);
        Assert.Equal(0, catalog.CaptureCount);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(0, hardware.ActiveSubscriptions);
        Assert.Equal(0U, ReadResourceSubscriptionCounts(subscriptions.Owner).ActiveSourceCount);
        Assert.Equal(0, catalog.CaptureCount);
    }
}
