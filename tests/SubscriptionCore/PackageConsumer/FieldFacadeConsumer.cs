using ResourceManager.App.Domain.Metrics;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Dependencies;
using ResourceManager.Subscription;

internal static class FieldFacadeConsumer
{
    internal static async Task RunAsync()
    {
        using var owner = new ManagedTrackerConsumer.Owner();
        using var client = new HttpClient();
        var hardware = new RecordedBackend();
        var core = new ReadWriteCore(hardware, new DependencyActions(client, []));
        var adapter = new NativeAdapter(core, owner);
        var subscriptions = new FieldSubscriptionCore(core, adapter);
        var fields = core.Catalog.Fields.Select(static item => item.Field).ToImmutableArray();
        Require(core.ReadCached(fields[0]).Value is null && hardware.Calls.Count == 0, "metadata/cache do not collect");
        var direct = await core.CollectAsync(fields);
        Require(direct[0].Value!.Integer == MetricInteger.Parse("18446744073709551615") && direct[1].Value!.Number == 0,
            "exact integer and valid zero through direct API");
        Require(!adapter.Tracker.CreatePlan(adapter.Now).IsActive, "direct collection has no demand");
        var slow = subscriptions.Acquire("same-label", fields, TimeSpan.FromSeconds(1));
        var fast = subscriptions.Acquire("same-label", [fields[0]], TimeSpan.FromMilliseconds(250));
        var first = adapter.Tracker.CreatePlan(adapter.Now);
        Require(first.Schedule.ActiveSourceCount == 2 && first.Schedule.ActiveItemCount == 2,
            "field facade reaches the original native demand table");
        var completion = adapter.Tracker.MarkSampled(first.Request!, adapter.Now);
        adapter.Now = completion.Schedule.NextWakeAt!.Value.AddMilliseconds(100);
        var faster = adapter.Tracker.CreatePlan(adapter.Now);
        Require(faster.Request!.Fields.SequenceEqual([fields[0]]), "exact fastest field only");
        adapter.Tracker.MarkSampled(faster.Request!, adapter.Now);
        fast.Dispose();
        slow.Dispose();
        Require(!adapter.Tracker.CreatePlan(adapter.Now).IsActive, "last field lease stops future plans");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try { _ = subscriptions.SubscribeAsync("cancelled", fields, TimeSpan.FromSeconds(1), cancelled.Token); throw new Exception("No cancellation"); }
        catch (OperationCanceledException) { }
        Require(!adapter.Tracker.CreatePlan(adapter.Now).IsActive, "cancelled call never registers demand");
        adapter.Now += TimeSpan.FromSeconds(1);
        await using (var stream = subscriptions.SubscribeAsync("stream", fields, TimeSpan.FromSeconds(1)).GetAsyncEnumerator())
        {
            Require(await stream.MoveNextAsync(), "actual native plan calls the same public collection API");
            Require(stream.Current.Length == fields.Length
                && fields.All(field => stream.Current.Single(reading => reading.Field == field) == core.ReadCached(field)),
                "subscriber and direct caller share exact current values");
            Require(hardware.Calls.Count == 2 && hardware.Calls[1].Length == fields.Length
                && hardware.Calls[1].ToHashSet().SetEquals(fields), "one exact due batch, no subscription-forwarded direct call");
        }
        Require(!adapter.Tracker.CreatePlan(adapter.Now).IsActive && owner.Leases == 0, "stream disposal ends demand and releases native lease");
        Console.WriteLine("SubscriptionCore field package consumer passed: public facade, actual native planning, shared direct collection and independent cancellation.");
    }

    private static void Require(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); }

    private sealed class RecordedBackend : IReadWriteBackend
    {
        public ReadWriteCatalog Catalog { get; } = new([new("recorded-A", "memory", null), new("recorded-B", "memory", null)],
        [
            new(new("recorded-A", "memory_all_physical_available_bytes"), new("memory_all_physical_available_bytes", "bytes", "unsigned64"),
                new(true, null, []), OperationSupport.Unsupported("read-only"), "recorded-memory"),
            new(new("recorded-B", "memory_all_physical_available_bytes"), new("memory_all_physical_available_bytes", "bytes", "float64"),
                new(true, null, []), OperationSupport.Unsupported("read-only"), "recorded-memory")
        ]);
        internal List<ImmutableArray<FieldReference>> Calls { get; } = [];
        private ImmutableArray<FieldReading> current = [];
        public FieldReading ReadCached(FieldReference field) => current.FirstOrDefault(item => item.Field == field)
            ?? new(field, FieldObservationStatus.Warming, null, null, "recorded-memory");
        public ValueTask<ReadWriteCatalog> DiscoverAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Catalog);
        public ValueTask<ImmutableArray<FieldReading>> CollectAsync(ImmutableArray<FieldReference> fields, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(fields);
            current = [.. fields.Select(field => new FieldReading(field, FieldObservationStatus.Current,
                field.DeviceIdentity == "recorded-A" ? new(Integer: MetricInteger.Parse("18446744073709551615")) : new(Number: 0),
                DateTimeOffset.UnixEpoch, "recorded-memory"))];
            return ValueTask.FromResult(current);
        }
        public ValueTask<FieldWriteResult> WriteAsync(FieldReference field, FieldValue value, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Read subscriptions do not write.");
    }

    private sealed record Request(string Id, ImmutableArray<FieldReference> Fields);
    // This consumer drives one deterministic tick explicitly; it does not replace
    // the packaged planner or simulate a native result. The read provider is recorded.
    private sealed class NativeAdapter : IFieldSubscriptionBackend
    {
        private readonly ReadWriteCore core;
        private readonly Dictionary<FieldReference, string> byField;
        private readonly Dictionary<string, FieldReference> byItem;
        private int nextId;
        internal DateTimeOffset Now = DateTimeOffset.Parse("2026-10-07T00:00:00Z");
        internal NativeItemSamplingSubscriptionTracker<Request, string> Tracker { get; }
        internal NativeAdapter(ReadWriteCore core, ManagedTrackerConsumer.Owner owner)
        {
            this.core = core;
            // Explicit device/field identity mapping, without classifying field names.
            byField = core.Catalog.Fields.Select((item, index) => (item.Field, Id: index.ToString(System.Globalization.CultureInfo.InvariantCulture)))
                .ToDictionary(static item => item.Field, static item => item.Id);
            byItem = byField.ToDictionary(static item => item.Value, static item => item.Key);
            Tracker = new(owner, 1, static request => request.Id, request => request.Fields.Select(field => byField[field]),
                (items, _) => items.Count == 0 ? null : new("due", [.. items.Select(item => byItem[item])]), false);
        }
        public IDisposable Acquire(string subscriptionId, ImmutableArray<FieldReference> fields, TimeSpan interval)
        {
            var id = $"{Interlocked.Increment(ref nextId)}:{subscriptionId}";
            Tracker.TrackPersistent(id, new(id, fields), Now, interval);
            return new Lease(this, id);
        }
        public async IAsyncEnumerable<ImmutableArray<FieldReading>> SubscribeAsync(string subscriptionId,
            ImmutableArray<FieldReference> fields, TimeSpan interval, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            using var lease = Acquire(subscriptionId, fields, interval);
            var plan = Tracker.CreatePlan(Now);
            Require(plan.Request is not null, "native plan issues a due field batch");
            ImmutableArray<FieldReading> readings;
            try { readings = await core.CollectAsync(plan.Request!.Fields, cancellationToken); }
            catch { Tracker.MarkFailed(plan.Request!, Now); throw; }
            Tracker.MarkSampled(plan.Request!, Now);
            yield return readings;
        }
        private sealed class Lease(NativeAdapter owner, string id) : IDisposable
        {
            private NativeAdapter? holder = owner;
            public void Dispose() { var previous = Interlocked.Exchange(ref holder, null); if (previous is not null) previous.Tracker.Remove(id, previous.Now); }
        }
    }
}
