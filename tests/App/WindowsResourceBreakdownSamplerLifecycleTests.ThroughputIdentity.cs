using System.Diagnostics;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Infrastructure.ResourceBreakdown;

namespace Resource_Manager_APP.Tests;

public sealed partial class WindowsResourceBreakdownSamplerLifecycleTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ThroughputBarsJoinEtwBirthAndRejectOnlyTheMismatchedContribution(bool staleDisk, bool staleNetwork)
    {
        using var subscriptions = new HostManagerSamplingSubscriptionTestFixture();
        using var catalog = new VersionedProcessAttributionCatalogProvider();
        var reader = new ExactThroughputReader(staleDisk, staleNetwork);
        using var sampler = new WindowsResourceBreakdownSampler(new StaticMetricSampler(CreateMemorySnapshot()), catalog,
            new StaticResourceResidualBreakdownProvider(), reader, reader,
            subscriptions.Provider, subscriptions.Owner, new PdhProcessGpuReader(),
            new DelegateWindowsProcessInventoryReader(static () => [Process.GetCurrentProcess()]));
        var snapshot = await sampler.CaptureSnapshotAsync(ResourceBreakdownSampleRequest.ForResourceTable(
            [ResourceBreakdownMetricIds.DiskRead, ResourceBreakdownMetricIds.NetworkRawReceive], new Dictionary<string, string>()), default);
        var disk = snapshot.Bars.Single(bar => bar.MetricId == ResourceBreakdownMetricIds.DiskRead);
        var network = snapshot.Bars.Single(bar => bar.MetricId == ResourceBreakdownMetricIds.NetworkRawReceive);
        Assert.Equal(staleDisk ? 0 : 1000, disk.TotalValue);
        Assert.Equal(staleNetwork ? 0 : 2000, network.TotalValue);
        if (!staleDisk) Assert.Contains(disk.Software.SelectMany(row => row.Processes), row => row.ProcessId == Environment.ProcessId);
        if (!staleNetwork) Assert.Contains(network.Software.SelectMany(row => row.Processes), row => row.ProcessId == Environment.ProcessId);
    }

    private sealed class ExactThroughputReader(bool staleDisk, bool staleNetwork) : IPhysicalDiskIoAttributionReader, INetworkAttributionReader
    {
        PhysicalDiskIoAttributionSnapshot IPhysicalDiskIoAttributionReader.Read()
        {
            using var process = Process.GetCurrentProcess();
            return new(DateTimeOffset.UtcNow, new Dictionary<int, PhysicalDiskProcessAttribution>
            {
                [process.Id] = new(process.Id, 1000, 0, 1, 0)
                    { ProcessStartKey = (ulong)process.StartTime.ToFileTimeUtc() + (staleDisk ? 1UL : 0) }
            }, new("etw-fileio-disk", PhysicalDiskIoProviderStates.Running, "fixture"));
        }
        NetworkAttributionSnapshot INetworkAttributionReader.Read(NetworkAttributionReadRequest request)
        {
            using var process = Process.GetCurrentProcess();
            return new(DateTimeOffset.UtcNow, new Dictionary<int, NetworkProcessAttribution>
            {
                [process.Id] = new(process.Id, 2000, 0, 0, 0, false)
                    { ProcessStartKey = (ulong)process.StartTime.ToFileTimeUtc() + (staleNetwork ? 1UL : 0) }
            }, new("etw-network-tcpip", NetworkAttributionProviderStates.Running, "fixture"));
        }
    }
}
