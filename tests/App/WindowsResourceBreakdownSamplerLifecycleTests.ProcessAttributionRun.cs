using System.Diagnostics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.ResourceBreakdown;

namespace Resource_Manager_APP.Tests;

public sealed partial class WindowsResourceBreakdownSamplerLifecycleTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProcessAttributionRunKeepsReaderUntilLastSubscriptionOrBlockStop(bool stopBlock)
    {
        using var subscriptions = new HostManagerSamplingSubscriptionTestFixture();
        using var catalog = new VersionedProcessAttributionCatalogProvider();
        using var sampler = new WindowsResourceBreakdownSampler(
            new StaticMetricSampler(CreateMemorySnapshot()), catalog,
            new StaticResourceResidualBreakdownProvider(), null!, null!,
            subscriptions.Provider, subscriptions.Owner, new PdhProcessGpuReader(),
            new DelegateWindowsProcessInventoryReader(static () => [Process.GetCurrentProcess()]));
        var request = new ResourceBreakdownSampleRequest([], new Dictionary<string, string>(),
            ProcessSampleDetailLevel.SmartSchedulingLite)
        { PublicationDatasetIds = [SamplingDatasetIds.ProcessAttribution] };
        using var first = sampler.AcquireSubscription("attribution-run-first", request, TimeSpan.FromMilliseconds(100));
        using var last = sampler.AcquireSubscription("attribution-run-last", request, TimeSpan.FromMilliseconds(100));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await sampler.StartAsync(default);
        try
        {
            await WaitUntil(() => catalog.CaptureCount >= 3 && catalog.ReturnedLeases == catalog.CaptureCount - 1);
            var rounds = catalog.CaptureCount;
            first.Dispose();
            await WaitUntil(() => catalog.CaptureCount > rounds && catalog.ReturnedLeases == catalog.CaptureCount - 1);
            if (stopBlock)
                await sampler.StopAsync(deadline.Token);
            else
                last.Dispose();
            await WaitUntil(() => catalog.ReturnedLeases == catalog.CaptureCount);
            if (!stopBlock)
                Assert.False(sampler.CaptureSamplingWorkerTask()!.IsCompleted); // idle worker released its run readers
        }
        finally { await sampler.StopAsync(default); }

        async Task WaitUntil(Func<bool> condition)
        {
            while (!condition()) await Task.Delay(20, deadline.Token);
        }
    }
}
