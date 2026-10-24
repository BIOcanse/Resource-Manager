using System.Diagnostics;
using System.Globalization;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Infrastructure.ResourceBreakdown;

namespace Resource_Manager_APP.Tests;

public sealed partial class WindowsResourceBreakdownSamplerLifecycleTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MixedNetworkRequestReadsOnceAndDoesNotUseEstimatedRatesForCanonicalFields(bool completeWindow)
    {
        using var subscriptions = new HostManagerSamplingSubscriptionTestFixture();
        using var catalog = new VersionedProcessAttributionCatalogProvider();
        var hardware = new StaticMetricSampler(CreateMemorySnapshot());
        var network = new MixedNetworkReader(completeWindow);
        using var sampler = new WindowsResourceBreakdownSampler(hardware, catalog,
            new StaticResourceResidualBreakdownProvider(), null!, network,
            subscriptions.Provider, subscriptions.Owner, new PdhProcessGpuReader(),
            new DelegateWindowsProcessInventoryReader(static () => [Process.GetCurrentProcess()]));
        var names = ProcessMetricFieldRegistry.Definitions.Where(static field => field.DatasetId == SamplingDatasetIds.ProcessObservedNetworkTraffic).Select(static field => field.Name).ToArray();
        var snapshot = await sampler.CaptureSnapshotAsync(ResourceBreakdownSampleRequest.ForResourceTable(
            [ResourceBreakdownMetricIds.NetworkRawReceive, .. names], new Dictionary<string, string>()), CancellationToken.None);
        Assert.Equal(1, network.Reads);
        Assert.Equal(1, hardware.CaptureCount);
        Assert.Equal(1000, Assert.Single(snapshot.Bars).TotalValue);
        var row = Assert.Single(snapshot.ProcessFields!.Processes);
        Assert.Equal(completeWindow ? "3" : null, row.Values[0].IntegerValue);
        Assert.Equal(completeWindow ? 1.5 : (double?)null, row.Values[2].NumericValue);
        Assert.Equal(2, snapshot.Datasets.Count);
        Assert.Equal(ResourceBreakdownSamplingStatuses.Ready,
            snapshot.Datasets.Single(dataset => dataset.DatasetId != SamplingDatasetIds.ProcessObservedNetworkTraffic).Status);
        Assert.Equal(completeWindow ? ResourceBreakdownSamplingStatuses.Ready : ResourceBreakdownSamplingStatuses.Failed,
            snapshot.Datasets.Single(dataset => dataset.DatasetId == SamplingDatasetIds.ProcessObservedNetworkTraffic).Status);
    }

    private sealed class MixedNetworkReader(bool completeWindow) : INetworkAttributionReader
    {
        internal int Reads;
        public NetworkAttributionSnapshot Read(NetworkAttributionReadRequest request)
        {
            Reads++;
            Assert.Equal(NetworkAttributionReadMode.LegacyAndObservedTraffic, request.Mode);
            using var process = Process.GetCurrentProcess();
            var captured = DateTimeOffset.UtcNow;
            return new(captured, new Dictionary<int, NetworkProcessAttribution>
                { [process.Id] = new(process.Id, 1000, 2000, 1, 0, true) }, new("ip-helper-network", NetworkAttributionProviderStates.Active, "fixture"))
            { ObservedTraffic = completeWindow ? new(captured, 2000, [new(process.Id,
                process.StartTime.ToUniversalTime().ToFileTimeUtc().ToString(CultureInfo.InvariantCulture), "3", "7", 1.5, 3.5)]) : null };
        }
    }
}
