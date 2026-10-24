using System.Reflection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace Resource_Manager_APP.Tests;

// Reused owner/topology setup has no test declaration or dependency on a Fact type.
internal static class MetricSnapshotOwnerTestFixture
{
    internal static NativeMetricSnapshotCatalogTopologySnapshot Topology(
        IReadOnlyList<NativeMetricSnapshotOrdinalCatalogIdentity> storage, bool storageUnavailable = false)
        => new(1, NativeMetricSnapshotCatalogTopologyStatus.Complete, [],
            storageUnavailable ? NativeMetricSnapshotCatalogTopologyStatus.Unavailable : NativeMetricSnapshotCatalogTopologyStatus.Complete,
            storage, NativeMetricSnapshotCatalogTopologyStatus.Unavailable, []);

    internal sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), $"metric-catalog-{Guid.NewGuid():N}");
        private NativeMetricSnapshotCatalogTopologySnapshot topology;
        private int topologyReads;
        public Fixture(NativeMetricSnapshotCatalogTopologySnapshot topology,
            Func<ulong, uint, ResourceManager.ReadWrite.Native.INativeMetricSnapshotScratch>? createScratch = null)
        {
            this.topology = topology;
            Slots = new HostManagerSamplingSubscriptionTestFixture();
            var source = new HostManagerMetricSnapshotCatalogTopologySource(() =>
            {
                Interlocked.Increment(ref topologyReads);
                if (TopologyFailure is { } failure) throw failure;
                return Volatile.Read(ref this.topology);
            });
            Owner = new HostManagerMetricSnapshotOwner(Slots.Provider,
                new HostManagerMetricSnapshotRuntime(Slots.Provider, Slots.Deployment), source,
                new EnvironmentInfo(root), NullLogger<HostManagerMetricSnapshotOwner>.Instance, createScratch ?? ResourceManager.ReadWrite.Native.NativeMetricSnapshotScratch.CreateOrdinary);
            Owner.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        public NativeMetricSnapshotCatalogTopologySnapshot Topology { set => Volatile.Write(ref topology, value); }
        public Exception? TopologyFailure { get; set; }
        public int TopologyReads => Volatile.Read(ref topologyReads);
        public DashboardMonitoringCatalogState Catalog { get; } = new();
        public HostManagerSamplingSubscriptionTestFixture Slots { get; }
        public HostManagerMetricSnapshotOwner Owner { get; }
        public void Dispose()
        {
            Owner.Dispose();
            Slots.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
    private sealed class EnvironmentInfo(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Tests";
        public string ApplicationName { get; set; } = "ResourceManager";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
