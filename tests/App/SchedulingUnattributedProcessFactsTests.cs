using System.Reflection;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Infrastructure.ResourceBreakdown;

namespace Resource_Manager_APP.Tests;

[Collection(SoftwareIdentityCatalogProcessStateCollection.Name)]
public sealed class SchedulingUnattributedProcessFactsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnattributableLiveProcessRetainsSchedulingIdentityBaseScoreAndSampledMetrics(bool missingAttribution)
    {
        using var subscriptions = new HostManagerSamplingSubscriptionTestFixture();
        using var sampler = new WindowsResourceBreakdownSampler(null!, null!, null!, null!, null!, subscriptions.Provider,
            subscriptions.Owner, new PdhProcessGpuReader());
        var request = new SchedulingProcessFactRequest(SchedulingProcessMetricMask.CpuUsage | SchedulingProcessMetricMask.MemoryUsage,
            new(SamplingObservationStatus.NotRequested, 0, 0, 0, 0, 0, 0, []));
        WindowsResourceBreakdownSampler.ProcessResourceSample[] samples = [new(78776, null, 10, "unknown", @"D:\Fixture\unknown.exe",
            1024, 2048, TimeSpan.FromSeconds(5), true, true, true, 17.5, true, false,
            null, null, null, null, null, null, null, null)];
        var batchType = typeof(WindowsResourceBreakdownSampler).GetNestedType("ProcessSampleBatch", BindingFlags.NonPublic)!;
        var batch = Activator.CreateInstance(batchType, [SamplingObservationStatus.Current, 1u, 0u, 0u, samples]);
        var attribution = new WindowsResourceBreakdownSampler.ProcessAttributionSnapshot(samples,
            samples.ToDictionary(sample => sample.ProcessId), missingAttribution
                ? new Dictionary<int, ResourceManager.App.Domain.ProcessAttribution.RuntimeSoftwareAttribution>()
                : new Dictionary<int, ResourceManager.App.Domain.ProcessAttribution.RuntimeSoftwareAttribution>
                    { [78776] = ResourceManager.App.Domain.ProcessAttribution.RuntimeSoftwareAttribution.Unattributed },
            subscriptions.Provider.Current.BaseScore, []);
        var method = typeof(WindowsResourceBreakdownSampler).GetMethod("CreateSchedulingSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var snapshot = (SchedulingProcessFactSnapshot)method.Invoke(sampler, [request, batch, attribution, 1UL, DateTimeOffset.UtcNow.UtcTicks, null])!;
        var process = Assert.Single(snapshot.Processes);
        Assert.Equal(78776, process.ProcessId);
        Assert.Equal("unattributed", process.SoftwareId);
        Assert.Equal(SoftwareKinds.Unattributed, process.SoftwareKind);
        Assert.Equal(30, process.BaseScore);
        Assert.True(process.ValidMetricMask.HasFlag(SchedulingProcessMetricMask.CpuUsage));
        Assert.True(process.ValidMetricMask.HasFlag(SchedulingProcessMetricMask.MemoryUsage));
        Assert.Equal(1024, process.MemoryUsedBytes);
        Assert.Equal(17.5, process.CpuUsagePercent);
        Assert.Equal(process.SoftwareId, Assert.Single(snapshot.AttributionProcesses).SoftwareId);
    }
}
