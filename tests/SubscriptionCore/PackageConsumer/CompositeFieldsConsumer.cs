using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Publications;
using ResourceManager.Subscription;

internal static class CompositeFieldsConsumer
{
    internal static async Task RunAsync()
    {
        var count = DeviceInformationFieldRegistry.CpuAllPhysicalCoreCountReported;
        var smt = DeviceInformationFieldRegistry.CpuAllSmtEnabled;
        var first = new Owner(count, "2"); var second = new Owner(smt, false);
        var composite = new CompositeReadWriteBackend([first.Reads, second.Reads]);
        var subscriptions = new CompositeFieldSubscriptionBackend(composite, new Dictionary<IReadWriteBackend, IFieldSubscriptionBackend>
            { [first.Reads] = first.Subscriptions, [second.Reads] = second.Subscriptions });
        FieldReference one = new("recorded-cpu", count.Name), two = new("recorded-cpu", smt.Name);
        Require(composite.Catalog.Devices.Length == 1 && composite.Catalog.Fields.Length == 2, "merge metadata for one exact device");
        Require(composite.ReadCached(one).Value?.Integer == "2" && composite.ReadCached(two).Value?.Boolean == false,
            "cache reads retain exact integer and false");
        Require(first.Captures == 0 && second.Captures == 0, "catalog and cached reads do not collect");
        using (subscriptions.Acquire("same-id", [two, one], TimeSpan.FromSeconds(1)))
            Require(first.Leases == 1 && second.Leases == 1, "explicit mixed lease reaches each original owner");
        Require(first.Leases == 0 && second.Leases == 0, "mixed lease disposal releases both owners");

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using (var stream = subscriptions.SubscribeAsync("mixed", [two, one, two], TimeSpan.FromSeconds(1), cancellation.Token).GetAsyncEnumerator())
        {
            var next = stream.MoveNextAsync().AsTask();
            Require(first.Leases == 1 && second.Leases == 1, "mixed stream acquires original demands");
            var collected = await composite.CollectAsync([two, one, two], cancellation.Token);
            Require(first.Captures == 1 && second.Captures == 1, "one batch capture per selected owner");
            Require(collected.Select(reading => reading.Field).SequenceEqual(new[] { two, one, two }), "direct batch preserves requested order and duplicates");
            Require(await next && stream.Current.Select(reading => reading.Field).SequenceEqual(new[] { two, one, two }), "mixed publication preserves selected order");
            Require(stream.Current[0].Value?.Boolean == false && stream.Current[1].Value?.Integer == "2", "mixed values read original publications");
            // End the stream while the other owner's wait is pending. Disposal must settle it and release both demands.
        }
        Require(first.Leases == 0 && second.Leases == 0, "paused mixed stream releases every pending owner");

        await using (var stream = subscriptions.SubscribeAsync("contract", [one, two], TimeSpan.FromSeconds(1), cancellation.Token).GetAsyncEnumerator())
        {
            var next = stream.MoveNextAsync().AsTask();
            first.Current = Owner.Snapshot(count with { Unit = "changed-unit" }, "99");
            second.Publish();
            Require(await next, "legacy publication streams keep the original notification contract");
            Require(stream.Current[0] is { Status: FieldObservationStatus.Unavailable, Value: null }, "contract replacement cannot redirect a retained field");
        }
        first.Current = Owner.Snapshot(count, "2");
        second.FailAcquire = true;
        try { subscriptions.Acquire("partial", [one, two], TimeSpan.FromSeconds(1)); throw new InvalidOperationException("Expected acquisition failure"); }
        catch (IOException) { }
        Require(first.Leases == 0, "partial acquisition failure releases earlier demand");
        second.FailAcquire = false;

        var synchronousOne = new SynchronousStream(false); var synchronousTwo = new SynchronousStream(false);
        var synchronous = new CompositeFieldSubscriptionBackend(composite, new Dictionary<IReadWriteBackend, IFieldSubscriptionBackend>
            { [first.Reads] = synchronousOne, [second.Reads] = synchronousTwo });
        var frames = 0;
        await foreach (var frame in synchronous.SubscribeAsync("sync", [two, one], TimeSpan.FromSeconds(1), cancellation.Token))
        { Require(frame.Length == 2, "finite synchronous mixed batch"); frames++; }
        Require(frames == 1 && synchronousOne.Disposals == 1 && synchronousTwo.Disposals == 1,
            "one complete round preserves both identities and releases synchronous sources");

        var throwing = new SynchronousStream(true); var remaining = new SynchronousStream(false);
        var cleanup = new CompositeFieldSubscriptionBackend(composite, new Dictionary<IReadWriteBackend, IFieldSubscriptionBackend>
            { [first.Reads] = throwing, [second.Reads] = remaining });
        try
        {
            await foreach (var _ in cleanup.SubscribeAsync("cleanup", [one, two], TimeSpan.FromSeconds(1), cancellation.Token)) { }
            throw new InvalidOperationException("Expected disposal failure");
        }
        catch (AggregateException) { }
        Require(throwing.Disposals == 1 && remaining.Disposals == 1, "one disposal error does not leak remaining source");
        try
        {
            _ = new CompositeReadWriteBackend([first.Reads, new Owner(count, "3").Reads]).Catalog;
            throw new InvalidOperationException("Expected duplicate binding failure");
        }
        catch (ArgumentException) { }
        Console.WriteLine("Composite information package consumer passed: direct/cache/stream, exact identity and contract, order, synchronous sources and complete lease cleanup.");
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private sealed class Owner
    {
        internal DeviceInformationSnapshot Current;
        internal int Captures, Leases;
        internal bool FailAcquire;
        private readonly CurrentValuePublicationSignal signal = new();
        internal InformationReadWriteBackend Reads { get; }
        internal InformationFieldSubscriptionBackend Subscriptions { get; }
        internal Owner(DeviceInformationDefinition definition, object value)
        {
            Current = Snapshot(definition, value);
            Reads = new(() => Current, _ => { Captures++; signal.Publish(); return ValueTask.FromResult(Current); }, static _ => new(true, null, []));
            Subscriptions = new(Reads, Acquire, Stream);
        }
        internal static DeviceInformationSnapshot Snapshot(DeviceInformationDefinition definition, object value)
            => new(DateTimeOffset.UtcNow, [new("recorded-cpu", "cpu_topology", [new(definition, JsonSerializer.SerializeToElement(value))])]);
        internal void Publish() => signal.Publish();
        private IDisposable Acquire(string id, TimeSpan interval)
        {
            if (FailAcquire) throw new IOException("recorded acquisition error");
            Leases++; return new Lease(() => Leases--);
        }
        private async IAsyncEnumerable<DeviceInformationSnapshot> Stream(string id, TimeSpan interval,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            using var notification = signal.Subscribe(); using var lease = Acquire(id, interval);
            while (true) { await notification.WaitAsync(cancellationToken); yield return Current; }
        }
    }

    private sealed class Lease(Action release) : IDisposable
    {
        private Action? action = release;
        public void Dispose() => Interlocked.Exchange(ref action, null)?.Invoke();
    }

    private sealed class SynchronousStream(bool failDispose) : IFieldSubscriptionBackend
    {
        public bool SupportsPeriodicDelivery => true;
        internal int Disposals;
        internal bool FailDispose { get; } = failDispose;
        public IDisposable Acquire(string id, ImmutableArray<FieldReference> fields, TimeSpan interval) => new Lease(() => { });
        public IAsyncEnumerable<ImmutableArray<FieldReading>> SubscribeAsync(string id, ImmutableArray<FieldReference> fields, TimeSpan interval, CancellationToken token)
            => new Frames(this, fields);
        private sealed class Frames(SynchronousStream owner, ImmutableArray<FieldReference> fields)
            : IAsyncEnumerable<ImmutableArray<FieldReading>>, IAsyncEnumerator<ImmutableArray<FieldReading>>
        {
            private bool yielded;
            public ImmutableArray<FieldReading> Current => [.. fields.Select(reference => new FieldReading(reference, FieldObservationStatus.Unavailable, null, null, "recorded"))];
            public IAsyncEnumerator<ImmutableArray<FieldReading>> GetAsyncEnumerator(CancellationToken token = default) => this;
            public ValueTask<bool> MoveNextAsync() { var result = !yielded; yielded = true; return ValueTask.FromResult(result); }
            public ValueTask DisposeAsync()
            {
                owner.Disposals++;
                return owner.FailDispose ? ValueTask.FromException(new IOException("recorded disposal error")) : ValueTask.CompletedTask;
            }
        }
    }
}
