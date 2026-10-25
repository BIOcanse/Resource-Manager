using System.Collections.Concurrent;
using System.Collections.Immutable;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Dependencies;
using ResourceManager.ReadWrite.Publications;
using ResourceManager.Subscription;

internal static class WorkerConsumer
{
    internal static async Task RunAsync()
    {
        await RunDemandAndCancellationAsync();
        await RunFailureRecoveryAsync();
        await RunValueRequestAsync();
        Console.WriteLine("SubscriptionCore worker package consumer passed: actual timed native plans, public collection, shared publication, removal, cancellation settlement, failure retry and restart.");
    }

    private static async Task RunDemandAndCancellationAsync()
    {
        using var owner = new ManagedTrackerConsumer.Owner();
        using var signal = new SemaphoreSlim(0, 1);
        using var client = new HttpClient();
        var backend = new Backend();
        var core = new ReadWriteCore(backend, new DependencyActions(client, []));
        var tracker = Tracker(owner);
        var failures = new ConcurrentQueue<SubscriptionWorkerFailure>();
        var fastOnly = Completion();
        var idleAfterDemand = Completion();
        var enteredBlockedCapture = Completion();
        var restarted = Completion();
        var sawDemand = false;
        var block = false;
        var restart = false;
        var cleanups = 0;
        var worker = new NativeSamplingSubscriptionWorker<Request, string>(tracker, signal, items =>
        {
            if (items.Count > 0) sawDemand = true;
            else if (sawDemand) { Interlocked.Increment(ref cleanups); idleAfterDemand.TrySetResult(); }
        }, failures.Enqueue, TimeSpan.FromMilliseconds(25));
        using var cancellation = new CancellationTokenSource();
        async Task<SubscriptionSampleCompletion> Capture(NativeItemSamplingSubscriptionPlan<Request, string> plan, CancellationToken token)
        {
            Require(plan.OwnerToken == "exact caller context" && owner.Leases > 0, "actual worker retains the caller's publication context and session lease");
            if (Volatile.Read(ref block))
            {
                enteredBlockedCapture.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            }
            var values = await core.CollectAsync([.. plan.Request!.Items.Select(Field)], token);
            Require(values.All(item => item == core.ReadCached(item.Field)), "worker uses the same direct collection/current values");
            if (plan.Request.Items.SequenceEqual(["one"])) fastOnly.TrySetResult();
            if (Volatile.Read(ref restart)) restarted.TrySetResult();
            return SubscriptionSampleCompletion.Sampled;
        }
        var task = worker.RunAsync(Capture, cancellation.Token);
        try
        {
            await Task.Delay(25);
            Require(backend.Batches.IsEmpty, "idle worker does not collect");
            tracker.TrackPersistent("slow", new("slow", ["one", "two"]), DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1));
            tracker.TrackPersistent("fast", new("fast", ["one"]), DateTimeOffset.UtcNow, TimeSpan.FromMilliseconds(50));
            worker.NotifyDemandChanged();
            await fastOnly.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Require(backend.Batches.Any(items => items.Length == 2) && backend.Batches.Any(items => items.SequenceEqual([Field("one")])),
                "actual timed native loop emits merged and fastest-only batches");
            Require(tracker.Remove("fast", DateTimeOffset.UtcNow) && tracker.Remove("slow", DateTimeOffset.UtcNow), "remove actual demands");
            worker.NotifyDemandChanged();
            await idleAfterDemand.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var count = backend.Batches.Count;
            using (var notification = backend.Publications.Subscribe(["two"]))
            {
                var publication = notification.WaitAsync(CancellationToken.None).AsTask();
                var values = await core.CollectAsync([Field("two")]);
                await publication.WaitAsync(TimeSpan.FromSeconds(5));
                Require(values[0].Value!.Number == 0 && values[0] == core.ReadCached(Field("two")), "direct collection shares valid-zero publication");
            }
            await Task.Delay(75);
            Require(backend.Batches.Count == count + 1, "no timed sampling after final demand; direct API adds no subscription");
            Volatile.Write(ref block, true);
            tracker.TrackPersistent("again", new("again", ["one"]), DateTimeOffset.UtcNow, TimeSpan.FromMilliseconds(50));
            worker.NotifyDemandChanged();
            await enteredBlockedCapture.Task.WaitAsync(TimeSpan.FromSeconds(5));
            try { await worker.RunAsync(Capture, CancellationToken.None); throw new InvalidOperationException("Duplicate run accepted"); }
            catch (InvalidOperationException error) when (error.Message == "The sampling worker is already running.") { }
        }
        finally { cancellation.Cancel(); await task.WaitAsync(TimeSpan.FromSeconds(5)); }
        Require(owner.Leases == 0 && Volatile.Read(ref cleanups) > 0, "cancelled capture settles its native lease and releases resource demand");
        Volatile.Write(ref block, false);
        Volatile.Write(ref restart, true);
        using var restartCancellation = new CancellationTokenSource();
        var restartTask = worker.RunAsync(Capture, restartCancellation.Token);
        try { await restarted.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { restartCancellation.Cancel(); await restartTask.WaitAsync(TimeSpan.FromSeconds(5)); }
        Require(tracker.Remove("again", DateTimeOffset.UtcNow), "release retained demand after restart");
        Require(owner.Leases == 0 && failures.IsEmpty, "worker restart leaves no borrow or diagnostic failure");
        using var alreadyCancelled = new CancellationTokenSource();
        alreadyCancelled.Cancel();
        await worker.RunAsync(Capture, alreadyCancelled.Token);
        Require(owner.Leases == 0, "already cancelled run still performs cleanup without capture");
    }

    private static async Task RunFailureRecoveryAsync()
    {
        using var owner = new ManagedTrackerConsumer.Owner();
        using var signal = new SemaphoreSlim(0, 1);
        using var client = new HttpClient();
        var backend = new Backend();
        var core = new ReadWriteCore(backend, new DependencyActions(client, []));
        var tracker = Tracker(owner);
        var failures = new ConcurrentQueue<SubscriptionWorkerFailure>();
        var completed = Completion();
        var failedDemand = false;
        var attempts = 0;
        var worker = new NativeSamplingSubscriptionWorker<Request, string>(tracker, signal, items =>
        {
            if (items.Count > 0 && !failedDemand) { failedDemand = true; throw new IOException("recorded demand failure"); }
        }, failures.Enqueue, TimeSpan.FromMilliseconds(25));
        tracker.TrackPersistent("recovery", new("recovery", ["one"]), DateTimeOffset.UtcNow, TimeSpan.FromMilliseconds(50));
        using var cancellation = new CancellationTokenSource();
        var task = worker.RunAsync(async (plan, token) =>
        {
            switch (++attempts)
            {
                case 1: throw new IOException("recorded capture failure");
                case 2: return (SubscriptionSampleCompletion)77;
                case 3:
                    backend.FailReads = true;
                    var unavailable = await core.CollectAsync([Field("one")], token);
                    Require(unavailable[0].Value is null && core.ReadCached(Field("one")).Value is null, "failed collection commits typed null");
                    return SubscriptionSampleCompletion.Failed;
                default:
                    backend.FailReads = false;
                    await core.CollectAsync([Field("one")], token);
                    completed.TrySetResult();
                    return SubscriptionSampleCompletion.Sampled;
            }
        }, cancellation.Token);
        try { await completed.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { cancellation.Cancel(); await task.WaitAsync(TimeSpan.FromSeconds(5)); }
        Require(attempts >= 4 && owner.Leases == 0, "failed demand/capture/invalid completion retry and settle on the same native session");
        Require(failures.Count(item => item.Stage == SubscriptionWorkerFailureStage.Demand) == 1
            && failures.Count(item => item.Stage == SubscriptionWorkerFailureStage.Collection) == 2,
            "diagnostics report actual faults separately from current values");
        Require(tracker.Remove("recovery", DateTimeOffset.UtcNow), "release recovery demand");
    }

    private static NativeItemSamplingSubscriptionTracker<Request, string> Tracker(ManagedTrackerConsumer.Owner owner)
        => new(owner, 1, static request => request.Id, static request => request.Items,
            static (items, _) => items.Count == 0 ? null : new("due", [.. items]), false);

    private static async Task RunValueRequestAsync()
    {
        using var owner = new ManagedTrackerConsumer.Owner();
        using var signal = new SemaphoreSlim(0, 1);
        var clock = new FixedClock();
        var tracker = new NativeItemSamplingSubscriptionTracker<int, string>(owner, 1,
            static _ => "value-request", static _ => ["one"], static (_, _) => 0, false);
        tracker.TrackPersistent(0, clock.GetUtcNow(), TimeSpan.FromMinutes(1));
        var first = Completion();
        var observedWaiting = Completion();
        var waitingFaulted = 0;
        var captures = 0;
        var failures = new ConcurrentQueue<SubscriptionWorkerFailure>();
        var worker = new NativeSamplingSubscriptionWorker<int, string>(tracker, signal, static _ => { },
            failures.Enqueue, TimeSpan.FromMilliseconds(5), clock);
        using var cancellation = new CancellationTokenSource();
        var task = worker.RunAsync((plan, _) =>
        {
            Require(plan.Request == 0 && plan.DueItemIds.Count > 0, "zero-valued request is valid only when items are due");
            Interlocked.Increment(ref captures);
            first.TrySetResult();
            return Task.FromResult(SubscriptionSampleCompletion.Sampled);
        }, cancellation.Token, planCreated: plan =>
        {
            Require(plan.OwnerToken == "exact caller context", "explicit plan observer gets the caller context even without due collection");
            if (plan.DueItemIds.Count == 0)
            {
                if (Interlocked.Exchange(ref waitingFaulted, 1) == 0)
                    throw new InvalidOperationException("recorded idle-plan adapter failure");
                observedWaiting.TrySetResult();
            }
        });
        try
        {
            await first.Task.WaitAsync(TimeSpan.FromSeconds(5));
            worker.NotifyDemandChanged();
            await observedWaiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { cancellation.Cancel(); await task.WaitAsync(TimeSpan.FromSeconds(5)); }
        Require(captures == 1 && failures.Count == 1 && failures.Single().Stage == SubscriptionWorkerFailureStage.Demand
            && owner.Leases == 0, "idle-plan adapter failure does not settle a default value-type request or start another collection");
        tracker.Clear(clock.GetUtcNow());
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-10-07T00:00:30Z");
    }
    private static TaskCompletionSource Completion() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static FieldReference Field(string item) => item == "one" ? new("cpu-device", "cpu_all_usage_percent")
        : new("memory-device", "memory_all_physical_available_bytes");
    private static void Require(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); }
    private sealed record Request(string Id, string[] Items);

    // Recorded provider; the planner, timed loop, public API and notification
    // implementation are the real packaged components. No hardware is touched.
    private sealed class Backend : IReadWriteBackend
    {
        private ImmutableDictionary<FieldReference, FieldReading> current = ImmutableDictionary<FieldReference, FieldReading>.Empty;
        public SamplingPublicationSignal Publications { get; } = new();
        internal ConcurrentQueue<ImmutableArray<FieldReference>> Batches { get; } = new();
        internal bool FailReads { get; set; }
        public ReadWriteCatalog Catalog { get; } = new([new("cpu-device", "cpu", null), new("memory-device", "memory", null)],
            new[] { Field("one"), Field("two") }.Select(field => new FieldCapability(field,
                new(field.FieldName, field.DeviceIdentity == "cpu-device" ? "%" : "bytes", "float64"),
                new(true, null, []), OperationSupport.Unsupported("read-only"), "recorded-provider")));
        public FieldReading ReadCached(FieldReference field) => Volatile.Read(ref current).GetValueOrDefault(field)
            ?? new(field, FieldObservationStatus.Warming, null, null, "recorded-provider");
        public ValueTask<ReadWriteCatalog> DiscoverAsync(CancellationToken token) => ValueTask.FromResult(Catalog);
        public ValueTask<ImmutableArray<FieldReading>> CollectAsync(ImmutableArray<FieldReference> fields, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            ImmutableArray<FieldReading> values = [.. fields.Select(field => new FieldReading(field,
                FailReads ? FieldObservationStatus.Unavailable : FieldObservationStatus.Current,
                FailReads ? null : new(Number: 0), FailReads ? null : DateTimeOffset.UtcNow, "recorded-provider"))];
            ImmutableInterlocked.Update(ref current, old => old.SetItems(values.Select(item => KeyValuePair.Create(item.Field, item))));
            Batches.Enqueue(fields);
            Publications.Publish(fields.Select(field => field == Field("one") ? "one" : "two"));
            return ValueTask.FromResult(values);
        }
        public ValueTask<FieldWriteResult> WriteAsync(FieldReference field, FieldValue value, CancellationToken token)
            => throw new InvalidOperationException("Sampling does not write.");
    }
}
