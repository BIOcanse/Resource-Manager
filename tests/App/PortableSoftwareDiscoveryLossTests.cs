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
    [Fact]
    public async Task InvalidAndExitedCandidatesAreDroppedAndNewCandidateCanBeObserved()
    {
        var resolver = new CandidateResolver();
        using var catalog = RuntimeProcessAttributionCatalog.CreateOrReuse(null, [], [], [],
            resolver, new NullResolvers(), new NullResolvers(), new NullResolvers(),
            SoftwareIdentityCatalogTestData.Empty, SoftwareIdentityCatalogTestData.Owner);
        var registry = new RecordingRegistry();
        var logger = new RecordingLogger();
        var exited = new PortableProcessCandidate(Environment.ProcessId, 2);
        PortableProcessCandidate candidate = exited;
        var reads = 0;
        using var service = new PortableSoftwareDiscoveryService(new Broker(), new CatalogProvider(catalog),
            new MatchingCatalog(), registry, logger, () => [new(0), new(-1), new(4), candidate], item =>
            {
                reads++;
                if (ReferenceEquals(item, exited)) { item.HasExited = true; return null; }
                return ReadFixtureIdentity(item);
            });
        for (var i = 0; i < 20; i++) await service.ScanRunningProcessesAsync(default);
        Assert.Equal(1, reads); Assert.Empty(logger.Warnings); Assert.Equal(0, registry.Observations);
        candidate = new(Environment.ProcessId, 2);
        await service.ScanRunningProcessesAsync(default);
        Assert.Equal(2, reads); Assert.Equal(1, registry.Observations);
    }

    [Fact]
    public void NativeIdentityReaderTreatsDisappearedPidAsLifetimeAndAcceptsNewBirth()
    {
        var reader = new WindowsPortableProcessIdentityReader();
        var missing = new PortableProcessCandidate(int.MaxValue);
        for (var i = 0; i < 20; i++) Assert.Null(reader.TryRead(missing));
        Assert.True(missing.HasExited);
        using var current = System.Diagnostics.Process.GetCurrentProcess();
        var birth = current.StartTime.ToFileTimeUtc();
        var old = new PortableProcessCandidate(current.Id, StartKey: birth - 1);
        Assert.Null(reader.TryRead(old)); Assert.True(old.HasExited);
        var delayedEvent = new PortableProcessCandidate(current.Id, StartedNoLaterThan: birth - 1);
        Assert.Null(reader.TryRead(delayedEvent)); Assert.True(delayedEvent.HasExited);
        Assert.Equal(birth, reader.TryRead(new(current.Id, StartKey: birth))!.StartKey);
    }

    [Fact]
    public async Task ConcurrentScanAndWorkerObserveEveryCandidateAndCancellationStopsWorker()
    {
        var resolver = new CandidateResolver();
        using var catalog = RuntimeProcessAttributionCatalog.CreateOrReuse(null, [], [], [],
            resolver, new NullResolvers(), new NullResolvers(), new NullResolvers(),
            SoftwareIdentityCatalogTestData.Empty, SoftwareIdentityCatalogTestData.Owner);
        var registry = new RecordingRegistry();
        var logger = new RecordingLogger();
        using var service = new PortableSoftwareDiscoveryService(new Broker(), new CatalogProvider(catalog),
            new MatchingCatalog(), registry, logger,
            () => Enumerable.Range(0, 24).Select(_ => new PortableProcessCandidate(Environment.ProcessId, 2)).ToArray(), ReadFixtureIdentity);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await service.StartAsync(cancellation.Token);
        await service.ScanRunningProcessesAsync(cancellation.Token);
        while (Volatile.Read(ref registry.Observations) < 48) await Task.Delay(10, cancellation.Token);
        cancellation.Cancel();
        await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(Worker(service).IsCompletedSuccessfully);
        Assert.Equal(48, resolver.Attempts);
        Assert.Equal(48, registry.Observations);
        Assert.Empty(logger.Warnings);
        Assert.Empty(logger.Errors);
    }

    [Theory]
    [InlineData(false, "native-StaleFrame")]
    [InlineData(true, "native-StaleFrame")]
    [InlineData(false, "ObjectDisposedException")]
    [InlineData(true, "OperationCanceledException")]
    public async Task FailedResolutionDoesNotStopLaterWorkerOrScanCandidates(bool scan, string failureKind)
    {
        var resolver = new CandidateResolver(failureKind);
        using var catalog = RuntimeProcessAttributionCatalog.CreateOrReuse(null, [], [], [],
            resolver, new NullResolvers(), new NullResolvers(), new NullResolvers(),
            SoftwareIdentityCatalogTestData.Empty, SoftwareIdentityCatalogTestData.Owner);
        var logger = new RecordingLogger();
        var registry = new RecordingRegistry();
        var candidates = new[] { new PortableProcessCandidate(Environment.ProcessId, 1),
            new PortableProcessCandidate(Environment.ProcessId, 1), new PortableProcessCandidate(Environment.ProcessId, 2) };
        using var service = new PortableSoftwareDiscoveryService(new Broker(), new CatalogProvider(catalog),
            new MatchingCatalog(), registry, logger, () => candidates, ReadFixtureIdentity);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        if (scan) await service.ScanRunningProcessesAsync(timeout.Token);
        else
        {
            await service.StartAsync(timeout.Token);
            await resolver.Succeeded.Task.WaitAsync(timeout.Token);
            await service.StopAsync(timeout.Token);
            Assert.True(Worker(service).IsCompletedSuccessfully);
        }
        Assert.Equal(3, resolver.Attempts);
        Assert.Equal(1, registry.Observations);
        Assert.Single(logger.Warnings);
        Assert.Contains(failureKind, logger.Warnings.Single());
        Assert.Empty(logger.Errors);
    }

    [Fact]
    public async Task MissingResolutionSessionStopsWorkerWithVisibleError()
    {
        using var catalog = RuntimeProcessAttributionCatalog.CreateOrReuse(null, [], [], [],
            new MissingSessionResolver(), new NullResolvers(), new NullResolvers(), new NullResolvers(),
            SoftwareIdentityCatalogTestData.Empty, SoftwareIdentityCatalogTestData.Owner);
        var logger = new RecordingLogger();
        using var service = new PortableSoftwareDiscoveryService(new Broker(), new CatalogProvider(catalog),
            new MatchingCatalog(), null!, logger, () => [new(Environment.ProcessId)]);
        await service.StartAsync(CancellationToken.None);
        await Worker(service).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Single(logger.Errors);
        Assert.Contains("unavailable for this service lifetime", logger.Errors.Single());
        Assert.Empty(logger.Warnings);
        await service.StopAsync(CancellationToken.None);
    }

    private static Task Worker(PortableSoftwareDiscoveryService service) => (Task)typeof(PortableSoftwareDiscoveryService)
        .GetField("workerTask", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;

    private static RuntimeProcessIdentity ReadFixtureIdentity(PortableProcessCandidate candidate)
        => new(candidate.ProcessId, candidate.ParentProcessId, "fixture", @"D:\Fixture\fixture.exe", false);

    private sealed class CandidateResolver(string failureKind = "native-StaleFrame") : IRuntimePackageIdentityResolver
    {
        internal int Attempts;
        internal TaskCompletionSource Succeeded = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public RuntimeAttributionObservation Observe(RuntimeProcessIdentity process)
        {
            Interlocked.Increment(ref Attempts);
            if (process.ParentProcessId == 1)
            {
                if (failureKind == "ObjectDisposedException") throw new ObjectDisposedException("process");
                if (failureKind == "OperationCanceledException") throw new OperationCanceledException("provider canceled this process");
                throw new ResourceManager.App.Infrastructure.NativeCore.NativeSoftwareIdentityException("resolution",
                    ResourceManager.App.Infrastructure.NativeCore.NativeSoftwareIdentityStatus.StaleFrame);
            }
            Succeeded.TrySetResult();
            return RuntimeAttributionObservation.Matched(new("catalog:fixture", "Fixture", "other", "other",
                [Path.GetDirectoryName(Environment.ProcessPath!)!]));
        }
    }

    private sealed class MissingSessionResolver : IRuntimePackageIdentityResolver
    {
        public RuntimeAttributionObservation Observe(RuntimeProcessIdentity process)
            => throw new ObjectDisposedException("NativeSoftwareIdentityResolutionSession");
    }

    private sealed class MatchingCatalog : ISoftwareIdentityCatalog
    {
        public string Version => "fixture";
        public IReadOnlyList<ResourceManager.App.Domain.SoftwareIdentity.SoftwareIdentityCatalogEntry> Entries => [];
        public ResourceManager.App.Domain.SoftwareIdentity.SoftwareIdentityCatalogEntry? MatchInstalledSoftware(
            ResourceManager.App.Domain.Software.InstalledSoftwareEntry software) => null;
        public ResourceManager.App.Domain.SoftwareIdentity.SoftwareIdentityCatalogEntry? MatchProcess(RuntimeProcessIdentity process) => null;
        public ResourceManager.App.Domain.SoftwareIdentity.PortableSoftwareIdentityMatch? MatchPortableProcess(RuntimeProcessIdentity process)
            => new(new("fixture", "Fixture", "other", "fixture"),
                ResourceManager.App.Domain.SoftwareIdentity.PortableSoftwareIdentityConfidence.Confirmed, []);
    }

    private sealed class RecordingRegistry : ResourceManager.App.Application.SoftwareDiscovery.IPortableSoftwareRegistry
    {
        internal int Observations;
        public void Observe(ResourceManager.App.Domain.SoftwareDiscovery.PortableSoftwareObservation observation)
            => Interlocked.Increment(ref Observations);
        public IReadOnlyList<ResourceManager.App.Domain.SoftwareDiscovery.PortableSoftwareRegistration> GetSnapshot() => [];
        public Task<IReadOnlyList<ResourceManager.App.Domain.SoftwareDiscovery.PortableSoftwareRegistration>> RefreshAsync(CancellationToken token)
            => Task.FromResult(GetSnapshot());
        public Task<ResourceManager.App.Domain.SoftwareDiscovery.PortableSoftwareRootConfirmationResult> ConfirmRootPathAsync(
            ResourceManager.App.Domain.SoftwareDiscovery.PortableSoftwareRootConfirmationRequest request, CancellationToken token)
            => throw new NotSupportedException();
    }

    private sealed class RecordingLogger : Microsoft.Extensions.Logging.ILogger<PortableSoftwareDiscoveryService>
    {
        internal System.Collections.Concurrent.ConcurrentBag<string> Warnings = [];
        internal System.Collections.Concurrent.ConcurrentBag<string> Errors = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel level) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel level, Microsoft.Extensions.Logging.EventId id,
            TState state, Exception? error, Func<TState, Exception?, string> formatter)
        {
            if (level == Microsoft.Extensions.Logging.LogLevel.Warning) Warnings.Add(formatter(state, error));
            if (level == Microsoft.Extensions.Logging.LogLevel.Error) Errors.Add(formatter(state, error));
        }
    }

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
            public KernelEtwSessionSnapshot GetSnapshot() => broker.GetSnapshot();

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
        public Task<RuntimeProcessAttributionCatalogLease> AcquireCatalogAsync(string? windowsSid, CancellationToken cancellationToken)
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
