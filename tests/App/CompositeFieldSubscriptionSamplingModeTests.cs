using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using ResourceManager.ReadWrite;
using ResourceManager.Subscription;

namespace Resource_Manager_APP.Tests;

public sealed class CompositeFieldSubscriptionSamplingModeTests
{
    [Fact]
    public async Task ProjectionFailureRemainsFirstWhenChildCleanupAlsoFails()
    {
        var first = new Source("one");
        var cleanup = new IOException("child cleanup failed");
        var second = new Source("two") { CleanupFailure = cleanup };
        var composition = Compose(first, second);
        await using var stream = composition.SubscribeAsync("mixed", [first.Field, second.Field],
            TimeSpan.FromSeconds(1), FieldSubscriptionOptions.Default, CancellationToken.None).GetAsyncEnumerator();
        var next = stream.MoveNextAsync().AsTask();
        first.Results.Writer.TryWrite(new(new("wrong", "identity"), FieldObservationStatus.Unavailable, null, null, "recorded"));
        second.Results.Writer.TryWrite(new(second.Field, FieldObservationStatus.Unavailable, null, null, "recorded"));
        var error = await Assert.ThrowsAsync<AggregateException>(() => next.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("Subscription owner changed selected identities.", error.InnerExceptions[0].Message);
        Assert.Same(cleanup, error.InnerExceptions[1]);
        Assert.Equal(0, first.Leases + second.Leases);
    }

    [Fact]
    public async Task FreshMixedOwnersAwaitBothAttemptResultsWithoutReadingAnotherOwnersCache()
    {
        var first = new Source("one");
        var second = new Source("two");
        var composition = Compose(first, second);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var options = new FieldSubscriptionOptions(FieldSubscriptionSamplingMode.FreshOnTrigger);
        await using var stream = composition.SubscribeAsync("mixed", [second.Field, first.Field],
            TimeSpan.FromSeconds(1), options, timeout.Token).GetAsyncEnumerator();
        var next = stream.MoveNextAsync().AsTask();
        Assert.Equal(1, first.Leases);
        Assert.Equal(1, second.Leases);
        first.Results.Writer.TryWrite(new(first.Field, FieldObservationStatus.Current,
            new(Text: "new-query"), DateTimeOffset.UnixEpoch.AddSeconds(10), "recorded", 250));
        Assert.False(next.IsCompleted);
        second.Results.Writer.TryWrite(new(second.Field, FieldObservationStatus.Unavailable, null, null, "recorded"));
        Assert.True(await next);
        Assert.Equal(FieldObservationStatus.Unavailable, stream.Current[0].Status);
        Assert.Null(stream.Current[0].Value);
        Assert.Equal("new-query", stream.Current[1].Value?.Text);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(10), stream.Current[1].ObservedAt);
        Assert.Equal(250UL, stream.Current[1].SampleDurationMilliseconds);
        Assert.Equal(0, first.CachedReads + second.CachedReads);
        Assert.Equal(options, first.Options);
        Assert.Equal(options, second.Options);
    }

    [Fact]
    public async Task EndingOneOwnerSettlesAnotherOwnersPendingWaitAndReleasesBothDemands()
    {
        var first = new Source("one");
        var second = new Source("two");
        var composition = Compose(first, second);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var stream = composition.SubscribeAsync("mixed", [first.Field, second.Field],
            TimeSpan.FromSeconds(1), FieldSubscriptionOptions.Default, timeout.Token).GetAsyncEnumerator();
        var next = stream.MoveNextAsync().AsTask();
        first.Results.Writer.TryComplete();
        Assert.False(await next.WaitAsync(timeout.Token));
        Assert.Equal(0, first.Leases + second.Leases);
    }

    private static CompositeFieldSubscriptionBackend Compose(Source first, Source second)
        => new(new CompositeReadWriteBackend([first, second]),
            new Dictionary<IReadWriteBackend, IFieldSubscriptionBackend> { [first] = first, [second] = second });

    private sealed class Source : IReadWriteBackend, IFieldSubscriptionBackend
    {
        internal Source(string identity)
        {
            Field = new(identity, "recorded");
            Catalog = new([new(identity, "recorded", null)], [new(Field, new("recorded", "text", "string"),
                new(true, null, []), OperationSupport.Unsupported("read-only"), "recorded")]);
        }
        internal readonly FieldReference Field;
        internal readonly Channel<FieldReading> Results = Channel.CreateUnbounded<FieldReading>();
        internal int Leases, CachedReads;
        internal FieldSubscriptionOptions? Options;
        internal Exception? CleanupFailure;
        public ReadWriteCatalog Catalog { get; }
        public ValueTask<ReadWriteCatalog> DiscoverAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Catalog);
        public FieldReading ReadCached(FieldReference field)
        {
            CachedReads++;
            return new(field, FieldObservationStatus.Current, new(Text: "old-cache"), DateTimeOffset.UnixEpoch, "recorded");
        }
        public ValueTask<ImmutableArray<FieldReading>> CollectAsync(ImmutableArray<FieldReference> fields, CancellationToken token)
            => throw new NotSupportedException();
        public ValueTask<FieldWriteResult> WriteAsync(FieldReference field, FieldValue value, CancellationToken token)
            => throw new NotSupportedException();
        public IDisposable Acquire(string id, ImmutableArray<FieldReference> fields, TimeSpan interval)
        { Leases++; return new Lease(() => Leases--); }
        public IDisposable Acquire(string id, ImmutableArray<FieldReference> fields, TimeSpan interval, FieldSubscriptionOptions options)
        { Options = options; return Acquire(id, fields, interval); }
        public IAsyncEnumerable<ImmutableArray<FieldReading>> SubscribeAsync(string id, ImmutableArray<FieldReference> fields,
            TimeSpan interval, CancellationToken token) => SubscribeAsync(id, fields, interval, FieldSubscriptionOptions.Default, token);
        public async IAsyncEnumerable<ImmutableArray<FieldReading>> SubscribeAsync(string id, ImmutableArray<FieldReference> fields,
            TimeSpan interval, FieldSubscriptionOptions options, [EnumeratorCancellation] CancellationToken token)
        {
            using var demand = Acquire(id, fields, interval, options);
            try
            {
                await foreach (var result in Results.Reader.ReadAllAsync(token)) yield return [result];
            }
            finally { if (CleanupFailure is { } error) throw error; }
        }
        private sealed class Lease(Action release) : IDisposable
        {
            private Action? action = release;
            public void Dispose() => Interlocked.Exchange(ref action, null)?.Invoke();
        }
    }
}
