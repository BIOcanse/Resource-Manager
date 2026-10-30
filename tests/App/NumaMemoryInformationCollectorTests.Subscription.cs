using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace Resource_Manager_APP.Tests;

public sealed partial class NumaMemoryInformationCollectorTests
{
    [Fact]
    public async Task RealWorkerReadsOnlySubscribedNodeAndGetRemainsCacheOnly()
    {
        var root = Path.Combine(Path.GetTempPath(), $"numa-worker-{Guid.NewGuid():N}");
        using var slots = new HostManagerSamplingSubscriptionTestFixture();
        using var owner = new HostManagerMetricSnapshotOwner(slots.Provider,
            new HostManagerMetricSnapshotRuntime(slots.Provider, slots.Deployment),
            new HostManagerMetricSnapshotCatalogTopologySource(() => Topology([new(0), new(9)])), new NumaEnvironment(root),
            NullLogger<HostManagerMetricSnapshotOwner>.Instance, ResourceManager.ReadWrite.Native.NativeMetricSnapshotScratch.CreateOrdinary);
        using var workspaceHolding = owner.Hold();
        var dashboard = new HardwareMetricCatalogState();
        using (var lease = owner.Acquire())
            dashboard.PublishCatalogProbe(NativeMetricSnapshotCommittedProjection.Create(frame: lease.Workspace.ReadCommitted(), workspaceIdentity: lease.WorkspaceIdentity));
        var reads = new List<ushort>();
        var capacityReads = new List<ushort>();
        var memory = new WindowsMemoryMonitoringZone(node => { reads.Add(node); return 9_007_199_254_740_993UL; },
            () => throw new InvalidOperationException("NUMA-only demand must not read global memory."),
            selected =>
            {
                capacityReads.AddRange(selected);
                return selected.ToDictionary(static node => node, static _ => 123UL);
            });
        using var sampler = new WindowsHardwareMetricSampler(new WindowsCpuMonitoringZone(), memory,
            null!, null!, null!, null!, null!, null!, null!, null!, null!, new AmdIntegratedGpuSensorRoute(), null!, owner,
            dashboard,  slots.Owner, slots.Provider, NullLogger<WindowsHardwareMetricSampler>.Instance);
        var request = MetricDeviceFields.CreateRequest(dashboard.Current!, "windows-numa-node|9", [FieldName, CapacityFieldName]);
        Assert.Equal(2, request.Ids.Count);
        // Explicit cache inspection: GetSnapshotAsync acquires a single-fetch demand and waits for sampling.
        _ = sampler.ReadLatest(request);
        Assert.Empty(reads);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await sampler.StartAsync(timeout.Token);
        try
        {
            using var demand = sampler.AcquireSubscription("numa-consumer", request, TimeSpan.FromMinutes(10));
            var current = await HardwareMetricCurrentTestWait.ReadAsync(sampler, request, timeout.Token);
            AssertNode(current, "windows-numa-node|9", "9007199254740993");
            AssertCapacityNode(current, "windows-numa-node|9", "123");
            Assert.Equal(new ushort[] { 9 }, reads);
            Assert.Equal(new ushort[] { 9 }, capacityReads);
            var snapshot = sampler.ReadLatest(request)!;
            AssertNode(snapshot, "windows-numa-node|9", "9007199254740993");
            AssertCapacityNode(snapshot, "windows-numa-node|9", "123");
            sampler.ReadLatest(request);
            Assert.Single(reads);
            Assert.Single(capacityReads);
        }
        finally
        {
            await sampler.StopAsync(CancellationToken.None);
            var count = reads.Count;
            var capacityCount = capacityReads.Count;
            _ = sampler.ReadLatest(request);
            Assert.Equal(count, reads.Count);
            Assert.Equal(capacityCount, capacityReads.Count);
        }
    }

    private sealed class NumaEnvironment(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Tests";
        public string ApplicationName { get; set; } = "ResourceManager";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
