using System.Collections.Immutable;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite;

namespace Resource_Manager_APP.Tests;

public sealed class FieldSubscriptionSamplingModeTests
{
    [Fact]
    public async Task HighestFrequencySharedDeliversLatestCurrentAtTheSlowSubscriberTrigger()
    {
        var scope = new FieldSubscriptionSamplingModeRecordedBorrow();
        scope.SetCurrent(scope.QueryA, "seed");
        var sample = 0;
        scope.CollectionBehavior = (fields, permit, _) =>
        {
            var readings = fields.Select(field => scope.CreateReading(field,
                $"sample-{Interlocked.Increment(ref sample)}")).ToImmutableArray();
            return ValueTask.FromResult(scope.Publish(permit, readings,
                [.. fields.Where(scope.IsSourceQuery)]));
        };

        await using var composition = CreateComposition(scope);
        await composition.StartAsync();
        using var fast = composition.Acquire("fast-current", [scope.QueryA], TimeSpan.FromMilliseconds(45));
        _ = await scope.CollectionCalls.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(8));
        _ = await scope.CollectionCalls.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(8));
        _ = await scope.BindingCalls.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        // Start a slow subscription and observe a later fast capture while its
        // first one-minute trigger is still pending. This avoids a fixed delay
        // or a narrow assertion near the trigger boundary.
        using (var preflightCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(8)))
        {
            var preflight = composition.SubscribeAsync("slow-trigger-pending", [scope.QueryA],
                TimeSpan.FromMinutes(1), FieldSubscriptionOptions.Default, preflightCancellation.Token)
                .GetAsyncEnumerator(preflightCancellation.Token);
            var pending = preflight.MoveNextAsync().AsTask();
            _ = await scope.BindingCalls.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

            var capturesAtRegistration = scope.CollectionCount;
            while (scope.CollectionCount == capturesAtRegistration)
                _ = await scope.CollectionCalls.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(pending.IsCompleted);

            preflightCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await pending.WaitAsync(TimeSpan.FromSeconds(2)));
            await preflight.DisposeAsync();
        }

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var slow = composition.SubscribeAsync("slow-current", [scope.QueryA],
            TimeSpan.FromMilliseconds(180), FieldSubscriptionOptions.Default, cancellation.Token)
            .GetAsyncEnumerator(cancellation.Token);
        Assert.True(await slow.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(8)));

        var delivered = Assert.Single(slow.Current);
        Assert.Equal(FieldObservationStatus.Current, delivered.Status);
        Assert.NotNull(delivered.Value?.Text);
        Assert.NotEqual("seed", delivered.Value?.Text);
        Assert.True(scope.CollectionCount >= 2);

        // The composition's corresponding cached read is the exact value it
        // delivered. This checks the original current owner, not a stream cache.
        var currentRead = await scope.CachedReads.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(delivered, currentRead);

        await slow.DisposeAsync();
        fast.Dispose();
        await composition.StopAsync().WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Empty(scope.ActiveFields);
    }

    [Fact]
    public async Task FreshOnTriggerReturnsQueriedValuesAndWaitsForEverySelectedCollectionItem()
    {
        var scope = new FieldSubscriptionSamplingModeRecordedBorrow();
        scope.BindOverride = field => new(field, [field.FieldName], [$"publication-{field.FieldName}"],
            scope.SourceKind(field));
        scope.SetCurrent(scope.Passive, "passive-current");
        scope.SetCurrent(scope.QueryA, "old-query-a");
        scope.SetCurrent(scope.QueryB, "old-query-b");
        scope.CollectionBehavior = (fields, permit, _) =>
        {
            var readings = fields.Select(field => scope.IsSourceQuery(field)
                ? scope.CreateReading(field, $"fresh-{field.FieldName}", DateTimeOffset.FromUnixTimeMilliseconds(1_800_000_000_000))
                : scope.PeekCurrent(field)).ToImmutableArray();
            return ValueTask.FromResult(scope.Publish(permit, readings,
                [.. fields.Where(scope.IsSourceQuery)]));
        };

        await using var composition = CreateComposition(scope);
        await composition.StartAsync();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var subscription = composition.SubscribeAsync("fresh-mixed-items",
            [scope.Passive, scope.QueryA, scope.QueryB], TimeSpan.FromMilliseconds(100),
            new(FieldSubscriptionSamplingMode.FreshOnTrigger), cancellation.Token)
            .GetAsyncEnumerator(cancellation.Token);

        Assert.True(await subscription.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(8)));
        var delivered = subscription.Current;
        Assert.Equal(new[] { scope.Passive, scope.QueryA, scope.QueryB },
            delivered.Select(static item => item.Field).ToArray());

        var passive = delivered[0];
        Assert.Equal(FieldObservationStatus.Unsupported, passive.Status);
        Assert.Null(passive.Value);
        Assert.Null(passive.ObservedAt);

        foreach (var query in delivered.Skip(1))
        {
            Assert.Equal(FieldObservationStatus.Current, query.Status);
            Assert.Equal($"fresh-{query.Field.FieldName}", query.Value?.Text);
            Assert.NotNull(query.ObservedAt);
            Assert.Equal(query, scope.PeekCurrent(query.Field));
        }

        Assert.True(scope.CollectionCount > 0);
        await subscription.DisposeAsync();
        await composition.StopAsync().WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Empty(scope.ActiveFields);
    }

    [Fact]
    public async Task FreshOnTriggerDoesNotReturnAnUncommittedQueryOrOldCurrent()
    {
        var scope = new FieldSubscriptionSamplingModeRecordedBorrow();
        var old = scope.CreateReading(scope.QueryA, "last-current");
        scope.SetCurrent(old);
        scope.CollectionBehavior = (fields, _, _) => ValueTask.FromResult(
            new FieldCollectionResult([old], Committed: false, QueriedFields: [scope.QueryA]));

        await using var composition = CreateComposition(scope);
        await composition.StartAsync();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var subscription = composition.SubscribeAsync("fresh-uncommitted", [scope.QueryA],
            TimeSpan.FromMilliseconds(80), new(FieldSubscriptionSamplingMode.FreshOnTrigger), cancellation.Token)
            .GetAsyncEnumerator(cancellation.Token);

        Assert.True(await subscription.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(8)));
        var reading = Assert.Single(subscription.Current);
        Assert.Equal(FieldObservationStatus.Unavailable, reading.Status);
        Assert.Null(reading.Value);
        Assert.Null(reading.ObservedAt);
        Assert.Equal(old, scope.PeekCurrent(scope.QueryA));

        await subscription.DisposeAsync();
        await composition.StopAsync().WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Empty(scope.ActiveFields);
    }

    [Fact]
    public async Task FreshOnTriggerCollectionExceptionDoesNotFallBackToOldCurrent()
    {
        var scope = new FieldSubscriptionSamplingModeRecordedBorrow();
        scope.SetCurrent(scope.QueryA, "old-current");
        var failed = new TaskCompletionSource<SubscriptionWorkerFailure>(TaskCreationOptions.RunContinuationsAsynchronously);
        scope.CollectionBehavior = (_, _, _) => ValueTask.FromException<FieldCollectionResult>(
            new InvalidOperationException("Recorded query failure."));

        await using var composition = CreateComposition(scope, failure => failed.TrySetResult(failure));
        await composition.StartAsync();
        var subscription = composition.SubscribeAsync("fresh-exception", [scope.QueryA],
            TimeSpan.FromMilliseconds(80), new(FieldSubscriptionSamplingMode.FreshOnTrigger), CancellationToken.None)
            .GetAsyncEnumerator();
        var pendingMove = subscription.MoveNextAsync().AsTask();

        var failure = await failed.Task.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Equal(SubscriptionWorkerFailureStage.Collection, failure.Stage);
        Assert.True(await pendingMove.WaitAsync(TimeSpan.FromSeconds(8)));
        var reading = Assert.Single(subscription.Current);
        Assert.Equal(FieldObservationStatus.Unavailable, reading.Status);
        Assert.Null(reading.Value);
        Assert.Null(reading.ObservedAt);
        Assert.Equal("old-current", scope.PeekCurrent(scope.QueryA).Value?.Text);

        await subscription.DisposeAsync();
        var returnedFailure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => composition.StopAsync().WaitAsync(TimeSpan.FromSeconds(8)));
        Assert.Equal("Recorded query failure.", returnedFailure.Message);
        Assert.Empty(scope.ActiveFields);
    }

    [Fact]
    public async Task PublicOptionsRejectFractionalAndBelowMinimumIntervalsAndUnknownModes()
    {
        var scope = new FieldSubscriptionSamplingModeRecordedBorrow();
        await using var composition = CreateComposition(scope);
        await composition.StartAsync();

        Assert.Throws<ArgumentOutOfRangeException>(() => composition.Acquire("fractional", [scope.QueryA],
            TimeSpan.FromTicks(TimeSpan.TicksPerMillisecond + 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => composition.Acquire("below-minimum", [scope.QueryA],
            TimeSpan.FromTicks(TimeSpan.TicksPerMillisecond / 2)));
        Assert.Throws<ArgumentOutOfRangeException>(() => composition.Acquire("unknown-mode", [scope.QueryA],
            TimeSpan.FromMilliseconds(10), new((FieldSubscriptionSamplingMode)int.MaxValue)));

        Assert.Empty(scope.ActiveFields);
        await composition.StopAsync().WaitAsync(TimeSpan.FromSeconds(8));
    }

    [Fact]
    public async Task StopCancelsInFlightFreshQueryDrainsWorkerAndReleasesLastDemand()
    {
        var scope = new FieldSubscriptionSamplingModeRecordedBorrow();
        scope.SetCurrent(scope.QueryA, "before-stop");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scope.CollectionBehavior = async (_, _, token) =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException)
            {
                canceled.TrySetResult();
                throw;
            }
            throw new InvalidOperationException("The recorded query should only end by cancellation.");
        };

        await using var composition = CreateComposition(scope);
        await composition.StartAsync();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var subscription = composition.SubscribeAsync("fresh-stop", [scope.QueryA],
            TimeSpan.FromMilliseconds(80), new(FieldSubscriptionSamplingMode.FreshOnTrigger), cancellation.Token)
            .GetAsyncEnumerator(cancellation.Token);
        var pendingMove = subscription.MoveNextAsync().AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(8));

        var drain = composition.StopAsync();
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(8));
        await drain.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.False(await pendingMove.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal("before-stop", scope.PeekCurrent(scope.QueryA).Value?.Text);
        Assert.Empty(scope.ActiveFields);

        await subscription.DisposeAsync();
    }

    private static NativeFieldSubscriptionComposition CreateComposition(
        FieldSubscriptionSamplingModeRecordedBorrow scope,
        Action<SubscriptionWorkerFailure>? reportFailure = null)
    {
        var configuration = DefaultFieldSubscriptionCompositionTests.Configuration();
        return new(scope, DefaultFieldSubscriptionCompositionTests.Settings(), in configuration,
            reportFailure ?? (failure => throw new InvalidOperationException("Unexpected recorded worker failure.", failure.Error)));
    }
}
