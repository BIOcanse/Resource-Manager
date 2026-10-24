using System.Collections;
using System.Diagnostics;
using System.Reflection;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Infrastructure.ResourceBreakdown;

namespace Resource_Manager_APP.Tests;

public sealed partial class WindowsResourceBreakdownSamplerLifecycleTests
{
    [Fact]
    public async Task FileMetadataIsRetainedForLiveExecutableAndRemovedAfterItsLastProcessExits()
    {
        using var subscriptions = new HostManagerSamplingSubscriptionTestFixture();
        using var catalog = new VersionedProcessAttributionCatalogProvider();
        var running = true;
        using var sampler = new WindowsResourceBreakdownSampler(new StaticMetricSampler(CreateMemorySnapshot()), catalog,
            new StaticResourceResidualBreakdownProvider(), null!, null!, subscriptions.Provider, subscriptions.Owner,
            new PdhProcessGpuReader(), new DelegateWindowsProcessInventoryReader(() =>
                running ? [Process.GetCurrentProcess()] : []));
        var request = ResourceBreakdownSampleRequest.ForResourceTable([ResourceBreakdownMetricIds.MemoryUsage],
            new Dictionary<string, string>());
        var cache = (IDictionary)typeof(WindowsResourceBreakdownSampler).GetField("fileMetadataCache",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(sampler)!;

        await sampler.CaptureSnapshotAsync(request, CancellationToken.None);
        Assert.Equal(1, cache.Count);
        await sampler.CaptureSnapshotAsync(request, CancellationToken.None);
        Assert.Equal(1, cache.Count);
        running = false;
        await sampler.CaptureSnapshotAsync(request, CancellationToken.None);
        Assert.Empty(cache);
    }
}
