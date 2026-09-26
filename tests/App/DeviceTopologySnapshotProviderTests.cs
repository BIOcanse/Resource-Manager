using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.DeviceTopology;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Domain.Messages;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;
using Resource_Manager_APP.Tests;

namespace ResourceManager.App.Tests;

public sealed class DeviceTopologySnapshotProviderTests
{
    [Fact]
    public async Task ReadStateDoesNotTrackDemandOrReadTopology()
    {
        var reader = new FakeReader(CreateSnapshot(DateTimeOffset.UtcNow));
        using var fixture = CreateProvider(reader, new FakeStore());
        var provider = fixture.Provider;

        await provider.StartAsync(CancellationToken.None);
        var state = provider.ReadState();
        await Task.Delay(50);
        await provider.StopAsync(CancellationToken.None);

        Assert.Equal(DeviceTopologySnapshotStatus.Warming, state.State);
        Assert.Null(state.Snapshot);
        Assert.Equal(0, reader.ReadCount);
    }

    [Fact]
    public async Task SemanticMatchKeepsPublishedSnapshotAndGeneration()
    {
        var first = CreateSnapshot(DateTimeOffset.UtcNow);
        var second = CreateSnapshot(first.CapturedAt.AddSeconds(1));
        var reader = new FakeReader(first, second);
        var store = new FakeStore();
        using var fixture = CreateProvider(reader, store);
        var provider = fixture.Provider;

        await provider.SampleNowAsync(CancellationToken.None);
        var initial = provider.ReadState();
        await provider.SampleNowAsync(CancellationToken.None);
        var refreshed = provider.ReadState();

        Assert.Equal(DeviceTopologySnapshotStatus.Ready, refreshed.State);
        Assert.Equal(1, refreshed.ContentGeneration);
        Assert.Same(initial.Snapshot, refreshed.Snapshot);
        Assert.Equal(1, store.SaveCount);
        Assert.Equal(2, reader.ReadCount);
    }

    [Fact]
    public async Task FailedRefreshKeepsLastGoodSnapshot()
    {
        var snapshot = CreateSnapshot(DateTimeOffset.UtcNow);
        var reader = new FakeReader(snapshot, new InvalidOperationException("test failure"));
        using var fixture = CreateProvider(reader, new FakeStore());
        var provider = fixture.Provider;

        var firstCompletion = await provider.SampleNowAsync(CancellationToken.None);
        var lastGood = provider.ReadState().Snapshot;
        var failedCompletion = await provider.SampleNowAsync(CancellationToken.None);
        var failed = provider.ReadState();

        Assert.Equal(DeviceTopologySampleCompletion.Sampled, firstCompletion);
        Assert.Equal(DeviceTopologySampleCompletion.Failed, failedCompletion);
        Assert.Equal(DeviceTopologySnapshotStatus.Failed, failed.State);
        Assert.Same(lastGood, failed.Snapshot);
        Assert.Equal("device-topology-read-failed", failed.FailureCode);
        Assert.Empty(failed.AttemptDiagnostics);
    }

    [Fact]
    public async Task IncompleteSourcePublishesOrderedDiagnosticsAndKeepsLastGood()
    {
        var snapshot = CreateSnapshot(DateTimeOffset.UtcNow);
        var diagnostics = new[]
        {
            new DeviceTopologySourceDiagnostic(
                "display-coordinator",
                DeviceTopologySourceDiagnosticStatus.RequiredIncomplete,
                "device-topology-display-coordinator-incomplete",
                BackendMessage.Create(
                    BackendMessageDomains.DeviceTopology,
                    BackendMessageCodes.DeviceTopology.DisplayCoordinatorNotReady,
                    "Warming")),
            new DeviceTopologySourceDiagnostic(
                "storage-capabilities",
                DeviceTopologySourceDiagnosticStatus.RequiredIncomplete,
                "device-topology-storage-capabilities-incomplete",
                BackendMessage.Create(
                    BackendMessageDomains.DeviceTopology,
                    BackendMessageCodes.DeviceTopology.StorageCapabilitiesIncomplete,
                    "fixture"))
        };
        var reader = new FakeReader(
            snapshot,
            new DeviceTopologyIncompleteSourcesException(diagnostics));
        var store = new FakeStore();
        using var fixture = CreateProvider(reader, store);
        var provider = fixture.Provider;

        await provider.SampleNowAsync(CancellationToken.None);
        var lastGood = provider.ReadState();
        await provider.SampleNowAsync(CancellationToken.None);
        var failed = provider.ReadState();

        Assert.Same(lastGood.Snapshot, failed.Snapshot);
        Assert.Equal(lastGood.ContentGeneration, failed.ContentGeneration);
        Assert.Equal(diagnostics, failed.AttemptDiagnostics);
        Assert.Equal(diagnostics[0].Code, failed.FailureCode);
        Assert.Equal(1, store.SaveCount);
    }

    [Fact]
    public async Task StartLoadsPersistedSnapshotBeforeServingRequests()
    {
        var snapshot = CreateSnapshot(DateTimeOffset.UtcNow.AddMinutes(-1));
        var semanticComparer = new DeviceTopologySemanticComparer();
        var store = new FakeStore(new DeviceTopologyPersistedSnapshot(
            DeviceTopologySnapshotState.CurrentSchemaVersion,
            "test",
            7,
            semanticComparer.ComputeHash(snapshot),
            snapshot));
        using var fixture = CreateProvider(new FakeReader(snapshot), store, semanticComparer);
        var provider = fixture.Provider;

        await provider.StartAsync(CancellationToken.None);
        var state = provider.ReadState();
        await provider.StopAsync(CancellationToken.None);

        Assert.Equal(DeviceTopologySnapshotStatus.Ready, state.State);
        Assert.Equal(DeviceTopologySnapshotSource.Persisted, state.Source);
        Assert.Equal(7, state.ContentGeneration);
        Assert.Same(snapshot, state.Snapshot);
    }

    [Fact]
    public async Task SubscriptionWaitsForCompletedSampleAndReleasesDemand()
    {
        var snapshot = CreateSnapshot(DateTimeOffset.UtcNow);
        var reader = new FakeReader(snapshot);
        using var fixture = CreateProvider(reader, new FakeStore());
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using (var subscription = fixture.Provider.SubscribeAsync(
            "test.callback", TimeSpan.FromSeconds(1), cancellation.Token)
            .GetAsyncEnumerator())
        {
            var next = subscription.MoveNextAsync().AsTask();
            Assert.False(next.IsCompleted);
            Assert.Equal(0, reader.ReadCount);

            await fixture.Provider.SampleNowAsync(cancellation.Token);

            Assert.True(await next);
            Assert.Same(snapshot, subscription.Current.Snapshot);
            Assert.Same(fixture.Provider.ReadState(), subscription.Current);
            next = subscription.MoveNextAsync().AsTask();
            Assert.False(next.IsCompleted);
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => next);
        }

        await fixture.Provider.StartAsync(CancellationToken.None);
        await Task.Delay(50);
        await fixture.Provider.StopAsync(CancellationToken.None);
        Assert.Equal(1, reader.ReadCount);
    }

    [Fact]
    public async Task SamplingDoesNotPublishAnIntermediateRefreshingState()
    {
        using var reader = new BlockingReader(CreateSnapshot(DateTimeOffset.UtcNow));
        using var fixture = CreateProvider(reader, new FakeStore());
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var subscription = fixture.Provider.SubscribeAsync(
            "test.atomic", TimeSpan.FromSeconds(1), cancellation.Token)
            .GetAsyncEnumerator();
        var previous = fixture.Provider.ReadState();
        var next = subscription.MoveNextAsync().AsTask();
        var capture = Task.Run(() => fixture.Provider.SampleNowAsync(cancellation.Token));
        try
        {
            Assert.True(reader.Entered.Wait(TimeSpan.FromSeconds(2)));
            Assert.False(next.IsCompleted);
            Assert.Same(previous, fixture.Provider.ReadState());
        }
        finally
        {
            reader.Release.Set();
            await capture;
        }
        Assert.True(await next);
        Assert.Equal(DeviceTopologySnapshotStatus.Ready, subscription.Current.State);
    }

    [Fact]
    public async Task StopClearsSubscriptionsBeforeSamplingOwnerStops()
    {
        var snapshot = CreateSnapshot(DateTimeOffset.UtcNow);
        using var fixture = CreateProvider(
            new FakeReader(snapshot),
            new FakeStore());

        await fixture.Provider.StartAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        await using var subscription = fixture.Provider.SubscribeAsync(
            "test.shutdown-order", TimeSpan.FromSeconds(1), cancellation.Token)
            .GetAsyncEnumerator();
        var next = subscription.MoveNextAsync().AsTask();
        await fixture.Provider.StopAsync(CancellationToken.None);
        await cancellation.CancelAsync();
        try { _ = await next; }
        catch (OperationCanceledException) { }
        await subscription.DisposeAsync();
        await fixture.Sampling.Owner.StopAsync(CancellationToken.None);

        var error = Record.Exception(fixture.Provider.Dispose);

        Assert.Null(error);
    }

    [Fact]
    public async Task CanceledSampleBeforeGateEntryReturnsCanceledCompletion()
    {
        using var fixture = CreateProvider(
            new FakeReader(CreateSnapshot(DateTimeOffset.UtcNow)),
            new FakeStore());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var completion = await fixture.Provider.SampleNowAsync(cancellation.Token);

        Assert.Equal(DeviceTopologySampleCompletion.Canceled, completion);
    }

    private static ProviderFixture CreateProvider(
        IDeviceTopologyReader reader,
        IDeviceTopologySnapshotStore store,
        DeviceTopologySemanticComparer? semanticComparer = null)
    {
        var sampling = new HostManagerSamplingSubscriptionTestFixture();
        var provider = new DeviceTopologySnapshotProvider(
            reader,
            store,
            semanticComparer ?? new DeviceTopologySemanticComparer(),
            sampling.Owner,
            NullLogger<DeviceTopologySnapshotProvider>.Instance);
        return new ProviderFixture(sampling, provider);
    }

    private static DeviceTopologySnapshot CreateSnapshot(DateTimeOffset capturedAt)
    {
        return new DeviceTopologySnapshot(
            capturedAt,
            new DeviceTopologySystemIdentity(
                "Test Manufacturer",
                "Test Model",
                "Test Brand",
                "TEST",
                "1.0.0",
                "Test Board Vendor",
                "Test Board"),
            [],
            [BackendMessage.Create(BackendMessageDomains.DeviceTopology, BackendMessageCodes.DeviceTopology.EnumerationOnly)]);
    }

    private sealed class FakeReader(params object[] results) : IDeviceTopologyReader
    {
        private readonly Queue<object> queue = new(results);

        public int ReadCount { get; private set; }

        public DeviceTopologySnapshot ReadSnapshot()
        {
            ReadCount += 1;
            var result = queue.Count > 1 ? queue.Dequeue() : queue.Peek();
            return result switch
            {
                DeviceTopologySnapshot snapshot => snapshot,
                Exception exception => throw exception,
                _ => throw new InvalidOperationException("Unsupported fake result.")
            };
        }
    }

    private sealed class BlockingReader(DeviceTopologySnapshot snapshot)
        : IDeviceTopologyReader, IDisposable
    {
        public ManualResetEventSlim Entered { get; } = new();
        public ManualResetEventSlim Release { get; } = new();

        public DeviceTopologySnapshot ReadSnapshot()
        {
            Entered.Set();
            if (!Release.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("The test did not release the topology capture.");
            }
            return snapshot;
        }

        public void Dispose()
        {
            Entered.Dispose();
            Release.Dispose();
        }
    }

    private sealed record ProviderFixture(
        HostManagerSamplingSubscriptionTestFixture Sampling,
        DeviceTopologySnapshotProvider Provider) : IDisposable
    {
        public void Dispose()
        {
            Provider.Dispose();
            Sampling.Dispose();
        }
    }

    private sealed class FakeStore(DeviceTopologyPersistedSnapshot? persisted = null)
        : IDeviceTopologySnapshotStore
    {
        public int SaveCount { get; private set; }

        public Task<DeviceTopologyPersistedSnapshot?> LoadAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(persisted);
        }

        public Task SaveAsync(
            DeviceTopologyPersistedSnapshot snapshot,
            CancellationToken cancellationToken)
        {
            SaveCount += 1;
            persisted = snapshot;
            return Task.CompletedTask;
        }
    }
}
