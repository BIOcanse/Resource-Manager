using System.Reflection;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.ProcessAttribution;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Infrastructure.GpuPlacement.External;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.ResourceBreakdown;
using ResourceManager.App.Infrastructure.SoftwareDiscovery;
using ResourceManager.App.Infrastructure.Telemetry.Etw;
using ResourceManager.ReadWrite.Windows.Processes;

namespace Resource_Manager_APP.Tests;

public sealed class KernelEtwActualUseHoldTests
{
    private const KernelTraceEventParser.Keywords Network = KernelTraceEventParser.Keywords.NetworkTCPIP;
    private const KernelTraceEventParser.Keywords Disk = KernelTraceEventParser.Keywords.DiskIO
        | KernelTraceEventParser.Keywords.Process;
    private const KernelTraceEventParser.Keywords Renderer = KernelTraceEventParser.Keywords.Process
        | KernelTraceEventParser.Keywords.ImageLoad;

    [Fact]
    public async Task LongRunningNetworkAndDiskRunsHoldResourcesUntilTheirRunsReturn()
    {
        var broker = new RecordingBroker();
        long tick = 0;
        using var network = new EtwNetworkTcpIpAttributionReader(broker,
            NullLogger<EtwNetworkTcpIpAttributionReader>.Instance, () => tick, static _ => null);
        using var disk = new EtwPhysicalDiskIoAttributionReader(new EtwPhysicalDiskIoCaptureOwner(
            broker, new(), () => tick, static _ => null));
        Assert.Null(network.Read(true).ObservedTraffic);
        Assert.Null(disk.Read(true).ObservedTraffic);
        broker.Checkpoint("no running users");
        await network.StartAsync(default);
        await disk.StartAsync(default);
        Assert.True(SpinWait.SpinUntil(() => broker.Acquires == 2, TimeSpan.FromSeconds(5)));
        try
        {
            Assert.Null(network.Read(true).ObservedTraffic);
            Assert.False(disk.Read(true).ObservedTraffic?.Complete ?? false);
            for (var round = 0; round < 12; round++)
            {
                tick += 10_000;
                broker.Checkpoint($"{round}: both used", ("network-tcpip-attribution", Network), ("physical-disk-io-attribution", Disk));
                var observedTraffic = network.Read(true).ObservedTraffic;
                Assert.NotNull(observedTraffic);
                Assert.Equal(2000, observedTraffic.WindowMilliseconds);
                Assert.Empty(observedTraffic.Processes);
                var observedDiskTraffic = disk.Read(true).ObservedTraffic;
                Assert.NotNull(observedDiskTraffic);
                Assert.True(observedDiskTraffic.Complete);
                Assert.Equal(2000, observedDiskTraffic.WindowMilliseconds);
                Assert.Empty(observedDiskTraffic.Processes);
                var acquires = broker.Acquires;
                for (var read = 0; read < 5; read++)
                {
                    network.Read(true);
                    disk.Read(true);
                }
                Assert.Equal(acquires, broker.Acquires);
                broker.Checkpoint($"{round}: reads retain the same holds", ("network-tcpip-attribution", Network), ("physical-disk-io-attribution", Disk));
            }
        }
        finally
        {
            await network.StopAsync(default);
            await disk.StopAsync(default);
        }
        broker.Checkpoint("runs returned");
        var before = broker.Acquires;
        Assert.Null(network.Read(true).ObservedTraffic);
        Assert.Null(disk.Read(true).ObservedTraffic);
        broker.Checkpoint("reads after stop");
        Assert.Equal(before, broker.Acquires);
        Assert.Equal(broker.Acquires, broker.Releases);
        Assert.Equal(27, broker.Checkpoints.Count);
    }

    [Fact]
    public void StandaloneProcessFieldsHoldOnlyExplicitlyRunningCaptureLanes()
    {
        var broker = new RecordingBroker();
        using var owner = WindowsProcessFieldOwner.CreateDefault(broker);
        Assert.Null(owner.ReadCachedSnapshot());
        broker.Checkpoint("constructed and cached read");
        for (var round = 0; round < 12; round++)
        {
            owner.StartNetworkCapture();
            broker.Checkpoint($"{round}: network lane", ("network-tcpip-attribution", Network));
            owner.StartDiskCapture();
            broker.Checkpoint($"{round}: network and disk lanes", ("network-tcpip-attribution", Network), ("physical-disk-io-attribution", Disk));
            owner.StopNetworkCapture();
            broker.Checkpoint($"{round}: disk lane", ("physical-disk-io-attribution", Disk));
            owner.StopDiskCapture();
            owner.ReadCachedSnapshot();
            broker.Checkpoint($"{round}: no lanes");
        }
        Assert.Equal(broker.Acquires, broker.Releases);
    }

    [Fact]
    public void ExternalRuntimeAndRendererCacheKeepInvalidationOnlyUntilLastActualObserverReturns()
    {
        var broker = new RecordingBroker();
        using var cache = new RendererAbsenceCache(broker);
        var runtime = new WindowsExternalGpuPlacementRuntime(new FixtureEnvironment(), null!,
            NullLogger<WindowsExternalGpuPlacementRuntime>.Instance, null!, cache);
        var identity = new GpuPlacementProcessInstance(601, 6001, "renderer.exe", @"C:\Apps\renderer.exe");
        cache.PublishAbsent(identity, cache.Read(identity));
        broker.Checkpoint("runtime construction and passive cache read");
        for (var round = 0; round < 12; round++)
        {
            var first = runtime.AcquireRendererObservation()!;
            var second = runtime.AcquireRendererObservation()!;
            broker.Checkpoint($"{round}: two observers, one invalidation hold", (broker.SingleId, Renderer));
            var before = cache.Read(identity);
            cache.PublishAbsent(identity, before);
            Assert.True(cache.Read(identity).IsAbsent);
            first.Dispose();
            broker.Checkpoint($"{round}: remaining observer", (broker.SingleId, Renderer));
            second.Dispose();
            broker.Checkpoint($"{round}: no observers");
            cache.PublishAbsent(identity, before);
            Assert.False(cache.Read(identity).CanPublishAbsent);
        }
        Assert.Equal(12, broker.Acquires);
        Assert.Equal(broker.Acquires, broker.Releases);
    }

    [Fact]
    public async Task DiscoveryReturnsProcessHoldOnWorkerFailureBeforeHostedStop()
    {
        var broker = new RecordingBroker();
        var provider = new GatedFailureCatalog();
        using var service = new PortableSoftwareDiscoveryService(broker, provider, null!, null!,
            NullLogger<PortableSoftwareDiscoveryService>.Instance, () => [new(42)], static _ => null);
        broker.Checkpoint("discovery not running");
        using var canceledStart = new CancellationTokenSource();
        canceledStart.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.StartAsync(canceledStart.Token));
        broker.Checkpoint("cancelled start has no event consumer");
        await service.StartAsync(default);
        try
        {
            await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            broker.Checkpoint("discovery consumes process starts", ("portable-software-discovery", KernelTraceEventParser.Keywords.Process));
            provider.Result.TrySetException(new InvalidOperationException("recorded catalog failure"));
            var worker = (Task)typeof(PortableSoftwareDiscoveryService).GetField("workerTask", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
            await worker.WaitAsync(TimeSpan.FromSeconds(5));
            broker.Checkpoint("worker returned before StopAsync");
        }
        finally
        {
            provider.Result.TrySetException(new InvalidOperationException("test cleanup"));
            await service.StopAsync(default);
        }
        broker.Checkpoint("host stopped");
        Assert.Equal(1, broker.Acquires);
        Assert.Equal(1, broker.Releases);
    }

    private sealed class GatedFailureCatalog : IRuntimeProcessAttributionCatalogProvider
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<RuntimeProcessAttributionCatalogLease> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public long SoftwareSnapshotGeneration => 0;
        public Task<RuntimeProcessAttributionCatalogLease> AcquireCatalogAsync(string? windowsSid, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            return Result.Task.WaitAsync(cancellationToken);
        }
        public Task InvalidateSoftwareSnapshotAsync() => Task.CompletedTask;
    }


    private sealed class FixtureEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "KernelEtwActualUseHoldTests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class RecordingBroker : IKernelEtwSessionBroker
    {
        private readonly object gate = new();
        private readonly Dictionary<string, KernelTraceEventParser.Keywords> held = [];
        internal List<(string Phase, KeyValuePair<string, KernelTraceEventParser.Keywords>[] Holds)> Checkpoints { get; } = [];
        internal int Acquires { get; private set; }
        internal int Releases { get; private set; }
        internal string SingleId { get { lock (gate) return Assert.Single(held).Key; } }

        internal void Checkpoint(string phase, params (string Id, KernelTraceEventParser.Keywords Keywords)[] expected)
        {
            lock (gate)
            {
                var observed = held.OrderBy(static item => item.Key).ToArray();
                Checkpoints.Add((phase, observed));
                Assert.True(expected.OrderBy(static item => item.Id).Select(static item => new KeyValuePair<string, KernelTraceEventParser.Keywords>(item.Id, item.Keywords)).SequenceEqual(observed),
                    $"{phase}: held [{string.Join(", ", observed.Select(static item => $"{item.Key}={item.Value}"))}]");
            }
        }
        public IKernelEtwSubscription Subscribe(KernelEtwSubscriptionRequest request)
        {
            var source = new KernelEtwEventSource();
            request.Bind(source, new(1, DateTimeOffset.UnixEpoch));
            source.Activate();
            lock (gate) { held.Add(request.Id, request.Keywords); Acquires++; }
            return new Hold(this, request.Id, source);
        }
        public KernelEtwSessionSnapshot GetSnapshot()
        {
            lock (gate) return new("Running", "recorded", 1, DateTimeOffset.UnixEpoch,
                held.Values.Aggregate(KernelTraceEventParser.Keywords.None, static (all, value) => all | value), held.Count, 0);
        }
        private sealed class Hold(RecordingBroker broker, string id, KernelEtwEventSource source) : IKernelEtwSubscription
        {
            private int disposed;
            public KernelEtwSessionSnapshot GetSnapshot() => broker.GetSnapshot();
            public void Dispose()
            {
                if (Interlocked.Exchange(ref disposed, 1) != 0) return;
                source.Cancel();
                lock (broker.gate) { Assert.True(broker.held.Remove(id)); broker.Releases++; }
            }
        }
    }
}
