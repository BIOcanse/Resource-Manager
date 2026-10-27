using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Dependencies;
using ResourceManager.Subscription;

namespace Resource_Manager_APP.Tests;

public sealed class FieldSubscriptionSingleFetchTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);

    [Fact]
    public async Task SingleFetchOwnsNamedFreshDemandAndReplacesOldQueryWithoutOtherSubscribers()
    {
        var scope = new FieldSubscriptionSamplingModeRecordedBorrow();
        var old = scope.CreateReading(scope.QueryA, "old-query", DateTimeOffset.UnixEpoch);
        scope.SetCurrent(old);
        var collect = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scope.CollectionBehavior = async (fields, permit, token) =>
        {
            await collect.Task.WaitAsync(token);
            return scope.Publish(permit, [scope.CreateReading(scope.QueryA, "fresh-query")], fields);
        };
        await using var composition = CreateComposition(scope);
        await composition.StartAsync();
        var backend = new RecordingBackend(composition);
        var readWrite = new ReadWriteCore(scope, new NoDependencies());
        var subscriptions = new FieldSubscriptionCore(readWrite, backend);
        using var cancellation = new CancellationTokenSource(Timeout);
        try
        {
            var pending = subscriptions.FetchOnceAsync("field-single-fetch", [scope.QueryA], cancellation.Token);
            var selected = await scope.CollectionCalls.Reader.ReadAsync(cancellation.Token);
            Assert.Equal(new[] { scope.QueryA }, selected.ToArray());
            Assert.Equal(new[] { scope.QueryA }, scope.ActiveFields.ToArray());
            Assert.False(pending.IsCompleted);
            Assert.Equal(old, readWrite.ReadCached(scope.QueryA));
            Assert.Equal("field-single-fetch", backend.Holder);
            Assert.Equal(FieldSubscriptionSamplingMode.FreshOnTrigger, backend.Options!.SamplingMode);
            Assert.Equal(SubscriptionSingleFetch.Interval, backend.Interval);
            Assert.Equal(1, backend.Acquired);

            collect.TrySetResult();
            var fresh = Assert.Single(await pending.WaitAsync(Timeout));
            Assert.Equal(FieldObservationStatus.Current, fresh.Status);
            Assert.Equal("fresh-query", fresh.Value?.Text);
            Assert.Equal(fresh, readWrite.ReadCached(scope.QueryA));
            Assert.Equal(1, scope.CollectionCount);
            Assert.Equal(1, backend.Released);
            await WaitForNoDemandAsync(scope, cancellation.Token);
            Assert.Empty(scope.ActiveFields);
        }
        finally { collect.TrySetResult(); }
        await composition.StopAsync().WaitAsync(Timeout);
    }

    [Fact]
    public async Task SingleFetchCompletesAndCancelsWithoutRetiringContinuousFieldDemand()
    {
        var scope = new FieldSubscriptionSamplingModeRecordedBorrow();
        await using var composition = CreateComposition(scope);
        await composition.StartAsync();
        var backend = new RecordingBackend(composition);
        var subscriptions = new FieldSubscriptionCore(new ReadWriteCore(scope, new NoDependencies()), backend);
        using var deadline = new CancellationTokenSource(Timeout);
        await using (var continuous = composition.SubscribeAsync("continuous-field", [scope.QueryA],
            TimeSpan.FromMilliseconds(100), FieldSubscriptionOptions.Default, deadline.Token)
            .GetAsyncEnumerator(deadline.Token))
        {
            Assert.True(await continuous.MoveNextAsync());
            var previous = Assert.Single(continuous.Current);
            var fresh = Assert.Single(await subscriptions.FetchOnceAsync("successful-field-once",
                [scope.QueryA], deadline.Token));
            Assert.NotEqual(previous.Value?.Text, fresh.Value?.Text);
            Assert.Equal(1, backend.Released);
            Assert.Equal(new[] { scope.QueryA }, scope.ActiveFields.ToArray());

            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            scope.CollectionBehavior = async (fields, permit, token) =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
                return scope.Publish(permit, [scope.CreateReading(scope.QueryA, "shared-after-cancel")], fields);
            };
            using var cancellation = new CancellationTokenSource();
            var pending = subscriptions.FetchOnceAsync("cancelled-field-once", [scope.QueryA], cancellation.Token);
            try
            {
                await entered.Task.WaitAsync(deadline.Token);
                Assert.False(pending.IsCompleted);
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(deadline.Token));
                Assert.Equal(2, backend.Released);
                Assert.False(release.Task.IsCompleted);
                Assert.Equal(new[] { scope.QueryA }, scope.ActiveFields.ToArray());
            }
            finally { release.TrySetResult(); }
            using var cadence = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            cadence.CancelAfter(TimeSpan.FromMilliseconds(750));
            do { Assert.True(await continuous.MoveNextAsync().AsTask().WaitAsync(cadence.Token)); }
            while (Assert.Single(continuous.Current).Value?.Text != "shared-after-cancel");
            Assert.True(await continuous.MoveNextAsync().AsTask().WaitAsync(cadence.Token));
            Assert.Equal("shared-after-cancel", Assert.Single(continuous.Current).Value?.Text);
        }
        await WaitForNoDemandAsync(scope, deadline.Token);
        await composition.StopAsync().WaitAsync(Timeout);
    }

    [Fact]
    public async Task CancellingBlockedSingleFetchReleasesItsDemandBeforeSharedCollectionFinishes()
    {
        var scope = new FieldSubscriptionSamplingModeRecordedBorrow();
        scope.SetCurrent(scope.QueryA, "old-query");
        var collect = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scope.CollectionBehavior = async (fields, permit, token) =>
        {
            await collect.Task.WaitAsync(token);
            return scope.Publish(permit, [scope.CreateReading(scope.QueryA, "late-shared-query")], fields);
        };
        await using var composition = CreateComposition(scope);
        await composition.StartAsync();
        var backend = new RecordingBackend(composition);
        var subscriptions = new FieldSubscriptionCore(new ReadWriteCore(scope, new NoDependencies()), backend);
        using var cancellation = new CancellationTokenSource(Timeout);
        using var deadline = new CancellationTokenSource(Timeout);
        try
        {
            var pending = subscriptions.FetchOnceAsync("cancel-field-single-fetch", [scope.QueryA], cancellation.Token);
            await scope.CollectionCalls.Reader.ReadAsync(deadline.Token);
            Assert.False(pending.IsCompleted);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(Timeout));
            Assert.Equal("cancel-field-single-fetch", backend.Holder);
            Assert.Equal(FieldSubscriptionSamplingMode.FreshOnTrigger, backend.Options!.SamplingMode);
            Assert.Equal(1, backend.Acquired);
            Assert.Equal(1, backend.Released);
            Assert.False(collect.Task.IsCompleted);
            Assert.Equal("old-query", scope.PeekCurrent(scope.QueryA).Value?.Text);

            // The original shared worker may finish its current collection;
            // the cancelled subscriber owns no remaining demand or delivery.
            collect.TrySetResult();
            await WaitForNoDemandAsync(scope, deadline.Token);
            Assert.Empty(scope.ActiveFields);
            Assert.Equal(1, scope.CollectionCount);
        }
        finally { collect.TrySetResult(); }
        await composition.StopAsync().WaitAsync(Timeout);
    }

    private static async Task WaitForNoDemandAsync(FieldSubscriptionSamplingModeRecordedBorrow scope, CancellationToken token)
    {
        while (!scope.ActiveFields.IsEmpty) await scope.DemandChanges.Reader.ReadAsync(token);
    }

    private static NativeFieldSubscriptionComposition CreateComposition(FieldSubscriptionSamplingModeRecordedBorrow scope)
    {
        var configuration = DefaultFieldSubscriptionCompositionTests.Configuration();
        return new(scope, DefaultFieldSubscriptionCompositionTests.Settings(), in configuration,
            failure => throw new InvalidOperationException("Unexpected recorded collection failure.", failure.Error),
            TimeSpan.FromDays(30));
    }

    // Records admission at the public backend boundary and forwards every
    // delivery and disposal to the production native composition unchanged.
    private sealed class RecordingBackend(NativeFieldSubscriptionComposition composition) : IFieldSubscriptionBackend
    {
        internal string? Holder;
        internal FieldSubscriptionOptions? Options;
        internal TimeSpan Interval;
        internal int Acquired, Released;
        public IDisposable Acquire(string id, ImmutableArray<FieldReference> fields, TimeSpan interval)
            => throw new InvalidOperationException("A single fetch must enumerate its subscription.");
        public IAsyncEnumerable<ImmutableArray<FieldReading>> SubscribeAsync(string id,
            ImmutableArray<FieldReference> fields, TimeSpan interval, CancellationToken token)
            => throw new InvalidOperationException("A single fetch must request FreshOnTrigger.");
        public async IAsyncEnumerable<ImmutableArray<FieldReading>> SubscribeAsync(string id,
            ImmutableArray<FieldReference> fields, TimeSpan interval, FieldSubscriptionOptions options,
            [EnumeratorCancellation] CancellationToken token)
        {
            Holder = id;
            Options = options;
            Interval = interval;
            Acquired++;
            try
            {
                await foreach (var value in composition.SubscribeAsync(id, fields, interval, options, token))
                    yield return value;
            }
            finally { Released++; }
        }
    }

    private sealed class NoDependencies : IReadWriteDependencies
    {
        public DependencyStatus Query(string id) => new(id, DependencyAvailability.Missing, "Recorded query needs no dependency.");
        public ValueTask<DependencyPackage> FetchAsync(string id, Uri source, string destination, CancellationToken token)
            => throw new NotSupportedException();
        public ValueTask<DependencyActionResult> InstallAsync(DependencyPackage package, CancellationToken token)
            => throw new NotSupportedException();
    }
}
