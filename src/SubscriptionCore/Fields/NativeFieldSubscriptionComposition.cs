using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using ResourceManager.ReadWrite;
using ResourceManager.Subscription.Native;

namespace ResourceManager.Subscription;

/// <summary>Explicitly started original native tracker/worker over a borrowed
/// canonical owner. Construction does not discover, capture or start sources.
/// The caller retains and disposes the read/write owner.</summary>
public sealed class NativeFieldSubscriptionComposition : IFieldSubscriptionBackend, IAsyncDisposable
{
    public bool SupportsPeriodicDelivery => true;

    private readonly object lifecycleGate = new();
    private readonly IReadWriteCollectionBorrow capture;
    private readonly SubscriptionRoleSettings settings;
    private readonly NativeSamplingSubscriptionConfiguration configuration;
    private readonly Action<SubscriptionWorkerFailure> reportFailure;
    private readonly TimeProvider clock;
    private Run? run;
    private ulong incarnation;
    private long nextLease;
    private bool disposed;
    private Task? disposeTask;

    public NativeFieldSubscriptionComposition(IReadWriteCollectionBorrow capture,
        ISubscriptionRoleSettings settings, in NativeSamplingSubscriptionConfiguration configuration,
        Action<SubscriptionWorkerFailure> reportFailure, TimeProvider? clock = null)
    {
        this.capture = capture ?? throw new ArgumentNullException(nameof(capture));
        ArgumentNullException.ThrowIfNull(settings);
        this.settings = new(settings.RoleId, settings.DefaultIntervalMilliseconds, settings.MinimumIntervalMilliseconds,
            settings.ActiveTtlMilliseconds, settings.MaximumFutureSkewMilliseconds, settings.FreshnessGraceMilliseconds);
        this.configuration = configuration;
        this.reportFailure = reportFailure ?? throw new ArgumentNullException(nameof(reportFailure));
        this.clock = clock ?? TimeProvider.System;
        if (configuration.Generation == 0 || settings.MinimumIntervalMilliseconds <= 0
            || settings.DefaultIntervalMilliseconds < settings.MinimumIntervalMilliseconds
            || settings.ActiveTtlMilliseconds <= 0 || settings.MaximumFutureSkewMilliseconds < 0
            || settings.FreshnessGraceMilliseconds < 0
            || configuration.DefaultIntervalMilliseconds != (ulong)settings.DefaultIntervalMilliseconds
            || configuration.MinimumIntervalMilliseconds != (ulong)settings.MinimumIntervalMilliseconds
            || configuration.ActiveTtlMilliseconds != (ulong)settings.ActiveTtlMilliseconds
            || configuration.MaximumFutureSkewMilliseconds != (ulong)settings.MaximumFutureSkewMilliseconds)
            throw new ArgumentException("Role policy and native configuration must describe the same explicit sampling contract.");
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (run is not null) throw new InvalidOperationException("The subscription composition is already running or draining.");
            var owner = new StandaloneSamplingSubscriptionSessionOwner(settings, in configuration, checked(++incarnation));
            var current = new Run(owner);
            try
            {
                current.Tracker = new(owner, settings.RoleId, static request => request.SourceKey,
                    static request => request.Bindings.SelectMany(static binding => binding.CollectionItemIds),
                    CreateRequest, false);
                current.Worker = new(current.Tracker, current.Signal,
                    items => capture.SynchronizeDemand(items.Count == 0 ? [] : current.ActiveFields),
                    reportFailure, clock);
                run = current;
                current.Task = current.Worker.RunAsync(async (plan, token) =>
                {
                    var result = await capture.CollectAsync(plan.Request!.Fields, plan.OwnerToken, token);
                    current.PendingResult = result;
                    return result.Committed ? SubscriptionSampleCompletion.Sampled : SubscriptionSampleCompletion.Skipped;
                }, current.Cancellation.Token, plan =>
                {
                    current.PendingResult = null;
                    current.ActiveFields = [.. plan.SourceViews.SelectMany(static view => view.Request.Fields).Distinct()];
                    if (plan.Request is null) Deliver(current, plan, null, freshOnly: false);
                }, (plan, completion) =>
                {
                    var result = current.PendingResult;
                    current.PendingResult = null;
                    if (completion != SubscriptionSampleCompletion.Canceled)
                        Deliver(current, plan, result, freshOnly: plan.Request is null ? true : null);
                });
                return Task.CompletedTask;
            }
            catch { run = null; current.Dispose(); throw; }
        }
    }

    public IDisposable Acquire(string subscriptionId, ImmutableArray<FieldReference> fields, TimeSpan interval)
        => Acquire(subscriptionId, fields, interval, FieldSubscriptionOptions.Default);

    public IDisposable Acquire(string subscriptionId, ImmutableArray<FieldReference> fields, TimeSpan interval,
        FieldSubscriptionOptions options)
        => AddDemand(subscriptionId, fields, interval, options, registerDelivery: false).Lease;

    private (Run Current, IDisposable Lease, NativeFieldSubscriptionDelivery.Registration? Registration) AddDemand(
        string subscriptionId, ImmutableArray<FieldReference> fields,
        TimeSpan interval, FieldSubscriptionOptions options, bool registerDelivery)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionId);
        if (fields.IsDefaultOrEmpty) throw new ArgumentException("Select at least one exact field.", nameof(fields));
        if (interval.Ticks % TimeSpan.TicksPerMillisecond != 0
            || interval.Ticks / TimeSpan.TicksPerMillisecond < settings.MinimumIntervalMilliseconds)
            throw new ArgumentOutOfRangeException(nameof(interval), "The native period must be whole milliseconds and meet the explicit role minimum.");
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        lock (lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var current = run;
            if (current is null || current.StopTask is not null) throw new InvalidOperationException("Explicit Start is required before acquiring demand.");
            var key = $"{current.Owner.Permit.WorkspaceIncarnation}:{checked(++nextLease)}:{subscriptionId}";
            var bindings = capture.Bind(fields);
            var delivery = registerDelivery
                ? current.FieldDelivery.Register(key, capture.Backend, fields, bindings)
                : null;
            var flags = NativeSamplingSubscriptionSourceFlags.None;
            if (delivery is not null || options.SamplingMode == FieldSubscriptionSamplingMode.FreshOnTrigger)
                flags |= NativeSamplingSubscriptionSourceFlags.PeriodicTriggers;
            if (options.SamplingMode == FieldSubscriptionSamplingMode.FreshOnTrigger)
                flags |= NativeSamplingSubscriptionSourceFlags.FreshOnTrigger;
            if (options.AlignSameItemPhase)
                flags |= NativeSamplingSubscriptionSourceFlags.AlignSameItemPhase | NativeSamplingSubscriptionSourceFlags.PeriodicTriggers;
            if (delivery is not null) current.Deliveries.Add(key, delivery);
            try { current.Tracker.TrackPersistent(key, new(key, fields, bindings), clock.GetUtcNow(), interval, flags); }
            catch
            {
                current.Deliveries.Remove(key);
                delivery?.Dispose();
                throw;
            }
            current.Worker.NotifyDemandChanged();
            return (current, new DemandLease(this, current, key), delivery);
        }
    }

    public IAsyncEnumerable<ImmutableArray<FieldReading>> SubscribeAsync(string subscriptionId,
        ImmutableArray<FieldReference> fields, TimeSpan interval,
        CancellationToken cancellationToken)
        => SubscribeAsync(subscriptionId, fields, interval, FieldSubscriptionOptions.Default, cancellationToken);

    public async IAsyncEnumerable<ImmutableArray<FieldReading>> SubscribeAsync(string subscriptionId,
        ImmutableArray<FieldReference> fields, TimeSpan interval, FieldSubscriptionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (current, demand, registration) = AddDemand(subscriptionId, fields, interval, options, registerDelivery: true);
        var delivery = registration ?? throw new InvalidOperationException("The field subscription has no delivery registration.");
        using (demand)
        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, current.Token))
        {
            await using var reader = delivery.ReadAllAsync(linked.Token).GetAsyncEnumerator(linked.Token);
            while (true)
            {
                var stopped = false;
                bool available = false;
                try { available = await reader.MoveNextAsync(); linked.Token.ThrowIfCancellationRequested(); }
                catch (OperationCanceledException) when (current.Token.IsCancellationRequested && !cancellationToken.IsCancellationRequested) { stopped = true; }
                if (stopped || !available) yield break;
                yield return reader.Current;
            }
        }
    }

    public Task StopAsync()
    {
        lock (lifecycleGate)
        {
            var current = run;
            if (current is null) return Task.CompletedTask;
            if (current.StopTask is not null) return current.StopTask;
            // Revoke synchronously before returning a task; an old capture cannot
            // cross the same permit gate and commit during asynchronous drain.
            current.Owner.Permit.Revoke();
            current.Cancellation.Cancel();
            current.StopTask = DrainAsync(current);
            return current.StopTask;
        }
    }

    private async Task DrainAsync(Run current)
    {
        try { await current.Task.ConfigureAwait(false); }
        finally
        {
            lock (lifecycleGate)
            {
                current.Tracker.Clear(clock.GetUtcNow());
                current.Dispose();
                if (ReferenceEquals(run, current)) run = null;
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (lifecycleGate)
        {
            if (disposeTask is not null) return new(disposeTask);
            disposed = true;
            disposeTask = DisposeCoreAsync(StopAsync());
            return new(disposeTask);
        }
    }

    private async Task DisposeCoreAsync(Task drain)
    {
        try { await drain.ConfigureAwait(false); }
        finally { capture.Dispose(); }
    }
    private void Remove(Run original, string key)
    {
        lock (lifecycleGate)
        {
            if (!ReferenceEquals(run, original) || original.StopTask is not null) return;
            original.Tracker.Remove(key, clock.GetUtcNow());
            if (original.Deliveries.Remove(key, out var delivery)) delivery.Dispose();
            original.Worker.NotifyDemandChanged();
        }
    }

    private void Deliver(Run current, NativeItemSamplingSubscriptionPlan<Selection, SubscriptionRunPermit> plan,
        FieldCollectionResult? result, bool? freshOnly = null)
    {
        if (plan.Triggers.IsDefaultOrEmpty) return;
        lock (lifecycleGate)
        {
            if (!ReferenceEquals(run, current) || current.StopTask is not null || current.Token.IsCancellationRequested) return;
            current.FieldDelivery.Deliver(plan.Triggers, result, freshOnly);
        }
    }

    private static Selection? CreateRequest(IReadOnlyList<string> due,
        IReadOnlyList<NativeItemSamplingSubscriptionSourceView<Selection>> sources)
    {
        if (due.Count == 0) return null;
        var dueSet = due.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var bindings = sources.SelectMany(static source => source.Request.Bindings)
            .Where(binding => binding.CollectionItemIds.Any(dueSet.Contains))
            .DistinctBy(static binding => binding.Field).ToImmutableArray();
        return bindings.IsEmpty ? null : new("planned", [.. bindings.Select(static binding => binding.Field)], bindings);
    }
    private sealed record Selection(string SourceKey, ImmutableArray<FieldReference> Fields,
        ImmutableArray<FieldCollectionBinding> Bindings);
    private sealed class DemandLease(NativeFieldSubscriptionComposition owner, Run original, string key) : IDisposable
    {
        private NativeFieldSubscriptionComposition? holder = owner;
        public void Dispose() => Interlocked.Exchange(ref holder, null)?.Remove(original, key);
    }
    private sealed class Run : IDisposable
    {
        internal readonly StandaloneSamplingSubscriptionSessionOwner Owner;
        internal Run(StandaloneSamplingSubscriptionSessionOwner owner) { Owner = owner; token = Cancellation.Token; }
        internal readonly SemaphoreSlim Signal = new(0, 1);
        internal readonly CancellationTokenSource Cancellation = new();
        internal CancellationToken Token => token;
        private readonly CancellationToken token;
        internal NativeItemSamplingSubscriptionTracker<Selection, SubscriptionRunPermit> Tracker = null!;
        internal NativeSamplingSubscriptionWorker<Selection, SubscriptionRunPermit> Worker = null!;
        internal ImmutableArray<FieldReference> ActiveFields = [];
        internal readonly NativeFieldSubscriptionDelivery FieldDelivery = new();
        internal readonly Dictionary<string, NativeFieldSubscriptionDelivery.Registration> Deliveries = new(StringComparer.Ordinal);
        internal FieldCollectionResult? PendingResult;
        internal Task Task = System.Threading.Tasks.Task.CompletedTask;
        internal Task? StopTask;
        public void Dispose()
        {
            FieldDelivery.RetireAll();
            Deliveries.Clear();
            PendingResult = null;
            Owner.Dispose(); Signal.Dispose(); Cancellation.Dispose();
        }
    }
}




