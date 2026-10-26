using System.Diagnostics;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Endpoints.Transport;
using ResourceManager.App.Infrastructure.ResourceBreakdown;
using ResourceManager.ReadWrite.Windows.Processes;

namespace Resource_Manager_APP.Tests;

public sealed partial class WindowsResourceBreakdownSamplerLifecycleTests
{
    [Theory]
    [InlineData("process_all_cpu_usage_actual")]
    [InlineData("process_all_network_receive_rate_attributed")]
    [InlineData("process_all_network_send_bytes_attributed")]
    [InlineData("process_all_physical_disk_read_bytes_attributed")]
    [InlineData("process_all_physical_disk_write_bytes_attributed")]
    public async Task ResourceMeasuresUseOriginalOwnerOnlyWhileSubscribed(string field)
    {
        using var subscriptions = new HostManagerSamplingSubscriptionTestFixture();
        using var catalog = new VersionedProcessAttributionCatalogProvider();
        var api = new ProcessBasicFieldCollectorTests.Api();
        var time = DateTimeOffset.UtcNow;
        var birth = ProcessBasicFieldCollectorTests.Birth.ToString();
        var network = new ProcessNetworkFieldCollectorTests.NetworkReader(_ => new(time,
            new Dictionary<int, NetworkProcessAttribution>(), new("network", NetworkAttributionProviderStates.Running, ""))
        { ObservedTraffic = new(time, 2000, [new(Environment.ProcessId, birth, "20", "30", null, null), new(999, null, "40", "60", null, null)]) });
        var disk = new MeasureDiskReader(time, birth);
        var snapshot = CreateMemorySnapshot() with
        {
            Items = new Dictionary<string, MetricReadingValue> { ["cpu.usage"] = new("cpu.usage", 25, "%") },
            Datasets = new Dictionary<string, HardwareMetricDatasetObservation>
            { [SamplingDatasetIds.SystemCpuUsage] = new(SamplingDatasetIds.SystemCpuUsage, SamplingObservationStatus.Current, 1, time.UtcTicks, 1, 1, 1, 1) }
        };
        using var sampler = new WindowsResourceBreakdownSampler(new StaticMetricSampler(snapshot), catalog,
            new StaticResourceResidualBreakdownProvider(), disk, network, subscriptions.Provider, subscriptions.Owner,
            new PdhProcessGpuReader(), new DelegateWindowsProcessInventoryReader(static () => [Process.GetCurrentProcess()]),
            new WindowsProcessBasicMetricReader(api));
        var request = ResourceBreakdownSampleRequest.ForResourceTable([field], new Dictionary<string, string>())
            with { ProcessMemoryFactIds = [field] };
        _ = await sampler.GetSnapshotAsync(request, default);
        Assert.Equal(0, api.Opens + disk.Reads + network.Reads);
        await sampler.StartAsync(default);
        try
        {
            using (sampler.AcquireSubscription("resource-measure", request, TimeSpan.FromMilliseconds(100)))
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                ResourceBreakdownSnapshot? current;
                do { await Task.Delay(20, timeout.Token); current = sampler.ReadLatest(request); }
                while (current?.ProcessMemoryFacts.Count != 1 || current.ProcessMemoryFacts[0].Processes.Count == 0);
                var foundations = sampler.ReadLatest(new SchedulingProcessFactRequest(SchedulingProcessMetricMask.None,
                    new(SamplingObservationStatus.NotRequested, 0, 0, 0, 0, 0, 0, [])));
                var fact = Assert.Single(ResourceFactsSnapshot.Create(current, [field], foundations).Metrics);
                Assert.Equal(fact.TotalValue, fact.ProcessSumValue + fact.ResidualValue);
                Assert.Equal(field, fact.ProcessFieldName);
                if (field.Contains("network")) { Assert.True(network.Reads > 0); Assert.Equal(0, disk.Reads); }
                if (field.Contains("disk")) { Assert.True(disk.Reads > 0); Assert.Equal(0, network.Reads); }
                Assert.Equal(0, api.MemoryCalls + api.IoCalls + api.HandleCalls);
            }
            await Task.Delay(200);
            var stopped = (api.Opens, disk.Reads, network.Reads);
            await Task.Delay(250);
            Assert.Equal(stopped, (api.Opens, disk.Reads, network.Reads));
        }
        finally { await sampler.StopAsync(default); }
    }

    private sealed class MeasureDiskReader(DateTimeOffset time, string birth) : IPhysicalDiskIoAttributionReader
    {
        internal int Reads;
        public PhysicalDiskIoAttributionSnapshot Read() => Read(false);
        public PhysicalDiskIoAttributionSnapshot Read(bool includeObservedTraffic)
        {
            Interlocked.Increment(ref Reads);
            Assert.True(includeObservedTraffic);
            return new(time, new Dictionary<int, PhysicalDiskProcessAttribution>(), new("disk", PhysicalDiskIoProviderStates.Running, ""))
            { ObservedTraffic = new(time, 2000, [new(new(1, 0), Environment.ProcessId, birth, "20", "30"), new(new(1, 0), 999, null, "40", "60")]) };
        }
    }
}
