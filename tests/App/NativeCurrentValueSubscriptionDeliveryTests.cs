using System.Collections.Immutable;
using ResourceManager.Subscription;

namespace Resource_Manager_APP.Tests;

public sealed class NativeCurrentValueSubscriptionDeliveryTests
{
    [Fact]
    public async Task CurrentSnapshotWaitsForAllOriginalMembersAndKeepsOnlyLatestCompleteRound()
    {
        var delivery = new NativeCurrentValueSubscriptionDelivery<(int A, int B)>();
        var current = (A: 1, B: 2);
        var reads = 0;
        using var registration = delivery.Register("snapshot", ["a", "b"], () => { reads++; return current; });
        delivery.Deliver(Triggers(1, "a"));
        Assert.Equal(0, reads);
        current = (3, 4);
        delivery.Deliver(Triggers(1, "b"));
        Assert.Equal(1, reads);
        current = (5, 6);
        delivery.Deliver(Triggers(2, "a", "b"));
        current = (7, 8); // Reading consumes the complete delivery, not a later untriggered value.
        await using var reader = registration.ReadAllAsync().GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal((5, 6), reader.Current);
        Assert.Equal(2, reads);
    }

    [Fact]
    public async Task CancellationAndRetirementRejectBufferedValuesAndReleaseTheSourceKey()
    {
        var delivery = new NativeCurrentValueSubscriptionDelivery<int>();
        using var registration = delivery.Register("snapshot", ["a"], () => 42);
        delivery.Deliver(Triggers(1, "a"));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await using (var reader = registration.ReadAllAsync(canceled.Token).GetAsyncEnumerator())
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.MoveNextAsync().AsTask());
        delivery.RetireAll();
        await using (var retired = registration.ReadAllAsync().GetAsyncEnumerator())
            Assert.False(await retired.MoveNextAsync());
        using var replacement = delivery.Register("snapshot", ["a"], () => 43);
        delivery.Deliver(Triggers(2, "a"));
        await using var next = replacement.ReadAllAsync().GetAsyncEnumerator();
        Assert.True(await next.MoveNextAsync());
        Assert.Equal(43, next.Current);
    }

    private static ImmutableArray<NativeItemSamplingSubscriptionTrigger<string>> Triggers(ulong sequence, params string[] items)
        => [.. items.Select(item => new NativeItemSamplingSubscriptionTrigger<string>("snapshot", item, 1000, sequence, "request", false))];
}
