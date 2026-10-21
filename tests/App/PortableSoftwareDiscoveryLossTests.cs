using System.Reflection;
using System.Threading.Channels;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Infrastructure.SoftwareDiscovery;
using ResourceManager.App.Infrastructure.Telemetry.Etw;
using ResourceManager.App.Application.ProcessAttribution;
using ResourceManager.App.Application.Adaptation;
using ResourceManager.App.Application.SoftwareIdentity;
using ResourceManager.App.Domain.ProcessAttribution;

namespace Resource_Manager_APP.Tests;

[Collection(SoftwareIdentityCatalogProcessStateCollection.Name)]
public sealed class PortableSoftwareDiscoveryLossTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitScanReturnsItsCatalogLeaseOnInventoryFailureOrCancellation(bool canceled)
    {
        using var catalog = RuntimeProcessAttributionCatalog.CreateOrReuse(null, [], [], [],
            new NullResolvers(), new NullResolvers(), new NullResolvers(), new NullResolvers(),
            SoftwareIdentityCatalogTestData.Empty, SoftwareIdentityCatalogTestData.Owner);
        var provider = new CatalogProvider(catalog);
        using var cancellation = new CancellationTokenSource();
        using var service = new PortableSoftwareDiscoveryService(null!, provider, null!, null!,
            NullLogger<PortableSoftwareDiscoveryService>.Instance, () =>
            {
                Assert.Equal(1, provider.ActiveReaders);
                if (!canceled) throw new IOException("inventory failed after acquiring the catalog");
                cancellation.Cancel();
                return [];
            });
        if (canceled)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ScanRunningProcessesAsync(cancellation.Token));
        else
            await Assert.ThrowsAsync<IOException>(() => service.ScanRunningProcessesAsync(cancellation.Token));
        Assert.Equal(0, provider.ActiveReaders);
        Assert.Equal(1, provider.ReturnedLeases);
    }

    [Fact]
    public async Task IdleWorkerDetectsNewLossAndStopStillCancelsItWhenLeaseReleaseFails()
    {
        var broker = new Broker();
        var scans = 0;
        using var service = new PortableSoftwareDiscoveryService(broker, null!, null!, null!,
            NullLogger<PortableSoftwareDiscoveryService>.Instance, () => { Interlocked.Increment(ref scans); return []; });
        await service.StartAsync(CancellationToken.None);
        Assert.Equal(1, Volatile.Read(ref scans));
        broker.EventsLost = 1;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (Volatile.Read(ref scans) < 2) await Task.Delay(20, timeout.Token);
        Assert.Equal(2, Volatile.Read(ref scans));
        broker.FailRelease = true;

        await Assert.ThrowsAsync<IOException>(() => service.StopAsync(timeout.Token));

        Assert.True(((Task)typeof(PortableSoftwareDiscoveryService)
            .GetField("workerTask", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!).IsCompleted);
        Assert.Equal(1, broker.ReleaseCount);
        Assert.Equal(1, broker.SubscribeCount);
        service.RefreshInventoryAfterLoss(Snapshot(1, 2));
        Assert.Equal(2, Volatile.Read(ref scans));
    }

    [Fact]
    public async Task InitialInventoryFailureReleasesItsLeaseWithoutStartingAWorker()
    {
        var broker = new Broker();
        using var service = new PortableSoftwareDiscoveryService(broker, null!, null!, null!,
            NullLogger<PortableSoftwareDiscoveryService>.Instance, () => throw new IOException("inventory fixture"));
        await Assert.ThrowsAsync<IOException>(() => service.StartAsync(CancellationToken.None));
        Assert.Equal(1, broker.ReleaseCount);
        Assert.Null(typeof(PortableSoftwareDiscoveryService)
            .GetField("workerTask", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service));
    }

    [Fact]
    public void NewLossOrSessionRecoveryRequeuesCurrentInventoryOnce()
    {
        var inventory = new[] { new PortableProcessCandidate(42), new PortableProcessCandidate(73) };
        var scans = 0;
        using var service = Create(() => { scans++; return inventory; });
        var queue = Queue(service);
        service.RefreshInventoryAfterLoss(Snapshot(1, 8));
        Assert.Equal(new[] { 42, 73 }, Drain(queue));
        service.RefreshInventoryAfterLoss(Snapshot(1, 8));
        Assert.Empty(Drain(queue));
        Assert.Equal(1, scans);

        inventory = [new(99)];
        service.RefreshInventoryAfterLoss(Snapshot(1, 9));
        Assert.Equal(new[] { 99 }, Drain(queue));
        service.RefreshInventoryAfterLoss(Snapshot(1, 9));
        Assert.Empty(Drain(queue));
        service.RefreshInventoryAfterLoss(Snapshot(2, 0));
        Assert.Equal(new[] { 99 }, Drain(queue));
        Assert.Equal(3, scans);
    }

    [Fact]
    public void UnavailableOrDisposedSourceDoesNotEnumerateProcesses()
    {
        var scans = 0;
        var service = Create(() => { scans++; return []; });
        service.RefreshInventoryAfterLoss(Snapshot(1, 0, "Unavailable"));
        Assert.Equal(0, scans);
        service.RefreshInventoryAfterLoss(Snapshot(2, 0));
        Assert.Equal(1, scans);
        service.Dispose();
        service.RefreshInventoryAfterLoss(Snapshot(3, 1));
        Assert.Equal(1, scans);
    }

    [Fact]
    public void RecoveryUsesTheExistingBoundedQueue()
    {
        using var service = Create(() => Enumerable.Range(1, 1200).Select(id => new PortableProcessCandidate(id)).ToArray());
        service.RefreshInventoryAfterLoss(Snapshot(1, 1));
        var candidates = Drain(Queue(service));
        Assert.Equal(1024, candidates.Length);
        Assert.Equal(177, candidates[0]);
        Assert.Equal(1200, candidates[^1]);
    }

    private static PortableSoftwareDiscoveryService Create(Func<PortableProcessCandidate[]> capture)
        => new(null!, null!, null!, null!, NullLogger<PortableSoftwareDiscoveryService>.Instance, capture);

    private static KernelEtwSessionSnapshot Snapshot(long generation, int loss, string state = "Running")
        => new(state, "fixture", generation, DateTimeOffset.UnixEpoch, KernelTraceEventParser.Keywords.Process, 1, loss);

    private static Channel<PortableProcessCandidate> Queue(PortableSoftwareDiscoveryService service)
        => (Channel<PortableProcessCandidate>)typeof(PortableSoftwareDiscoveryService)
            .GetField("processStarts", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;

    private static int[] Drain(Channel<PortableProcessCandidate> queue)
    {
        var ids = new List<int>();
        while (queue.Reader.TryRead(out var candidate)) ids.Add(candidate.ProcessId);
        return ids.ToArray();
    }

    private sealed class Broker : IKernelEtwSessionBroker
    {
        internal int EventsLost;
        internal int ReleaseCount;
        internal int SubscribeCount;
        internal bool FailRelease;
        public KernelEtwSessionSnapshot GetSnapshot() => Snapshot(1, Volatile.Read(ref EventsLost));
        public IKernelEtwSubscription Subscribe(KernelEtwSubscriptionRequest request)
        {
            SubscribeCount++;
            return new Lease(this);
        }
        private sealed class Lease(Broker broker) : IKernelEtwSubscription
        {
            public void Dispose()
            {
                broker.ReleaseCount++;
                if (broker.FailRelease) throw new IOException("release fixture");
            }
        }
    }

    private sealed class CatalogProvider(RuntimeProcessAttributionCatalog catalog) : IRuntimeProcessAttributionCatalogProvider
    {
        internal int ActiveReaders;
        internal int ReturnedLeases;
        public long SoftwareSnapshotGeneration => 0;
        public Task<RuntimeProcessAttributionCatalogLease> AcquireCatalogAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref ActiveReaders);
            return Task.FromResult(new RuntimeProcessAttributionCatalogLease(catalog, () =>
            {
                Interlocked.Decrement(ref ActiveReaders);
                Interlocked.Increment(ref ReturnedLeases);
            }));
        }
        public Task InvalidateSoftwareSnapshotAsync() => Task.CompletedTask;
    }

    private sealed class NullResolvers : IRuntimePackageIdentityResolver, IRuntimeServiceIdentityResolver,
        IRuntimeRootIdentityResolver, IRuntimeSystemProcessClassifier
    {
        public RuntimeAttributionObservation Observe(RuntimeProcessIdentity process) => RuntimeAttributionObservation.NoMatch;
    }
}
