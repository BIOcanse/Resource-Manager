using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Publications;
using ResourceManager.Subscription;
using ResourceManager.Subscription.Native;

namespace Resource_Manager_APP.Tests;

public sealed class DefaultFieldSubscriptionCompositionTests
{
    [Fact]
    public async Task ConstructorDoesNotCaptureAndStopRevokesBeforeDrainWithLateLeaseSafeOnRestart()
    {
        using var scope = new RecordedScope();
        await using var composition = Create(scope);
        Assert.Equal(0, scope.Captures);
        Assert.Throws<InvalidOperationException>(() => composition.Acquire("before-start", [scope.A], TimeSpan.FromSeconds(1)));
        await composition.StartAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => composition.StartAsync());
        var entered = new TaskCompletionSource<IMetricPublicationPermit>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scope.BeforeCollect = async permit => { entered.TrySetResult(permit); await release.Task; };
        var old = composition.Acquire("same", [scope.A], TimeSpan.FromSeconds(1));
        var permit = await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var drain = composition.StopAsync();
        Assert.False(permit.IsActive);
        Assert.False(drain.IsCompleted);
        Assert.False(permit.TryPublish(() => throw new InvalidOperationException("Retired commit ran.")));
        release.SetResult();
        await drain.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, scope.Commits);
        Assert.Empty(scope.Demand);
        scope.BeforeCollect = null;
        await composition.StartAsync();
        using var replacement = composition.Acquire("same", [scope.B], TimeSpan.FromSeconds(1));
        old.Dispose();
        var next = await scope.Calls.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        // Drain the retired recorded request: it did not publish current.
        if (next.Contains(scope.A)) next = await scope.Calls.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains(scope.B, next);
        await composition.StopAsync();
        Assert.Empty(scope.Demand);
    }

    [Fact]
    public async Task LastLeaseWakesOriginalWorkerAndWithdrawsDemandWithoutWaitingForNextSample()
    {
        using var scope = new RecordedScope();
        await using var composition = Create(scope);
        await composition.StartAsync();
        var active = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scope.DemandChanged = fields =>
        {
            if (!fields.IsEmpty) active.TrySetResult();
            else if (active.Task.IsCompleted) released.TrySetResult();
        };
        var lease = composition.Acquire("last", [scope.A], TimeSpan.FromMinutes(1));
        await active.Task.WaitAsync(TimeSpan.FromSeconds(10));
        _ = await scope.Calls.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        lease.Dispose();
        await released.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(scope.Demand);
        Assert.Equal(1, scope.Captures);
        await composition.StopAsync();
    }

    [Fact]
    public void TwoOriginalDatasetsKeepIndependentDueFrequencyAndSharedBasicFieldsStayTogether()
    {
        var configuration = Configuration();
        using var owner = new StandaloneSamplingSubscriptionSessionOwner(Settings(), in configuration, 1);
        static string Item(string field) => field == "rx" ? "network" : "basic";
        var tracker = new NativeItemSamplingSubscriptionTracker<string[], SubscriptionRunPermit>(owner, 1,
            static fields => string.Join(',', fields), fields => fields.Select(Item),
            (due, sources) => sources.SelectMany(static source => source.Request)
                .Where(field => due.Contains(Item(field))).Distinct().Order().ToArray(), false, clock: new FrozenClock());
        var now = DateTimeOffset.FromUnixTimeMilliseconds(59_900);
        tracker.TrackPersistent("image-fast", ["image"], now, TimeSpan.FromSeconds(1));
        tracker.TrackPersistent("sid-slow", ["sid"], now, TimeSpan.FromMinutes(1));
        tracker.TrackPersistent("network-slow", ["rx"], now, TimeSpan.FromMinutes(1));
        var initial = tracker.CreatePlan(now);
        var completion = tracker.MarkSampled(initial.Request!, now);
        Assert.Equal(new[] { "image", "rx", "sid" }, initial.Request!);
        Assert.Equal(now.AddSeconds(1), completion.Schedule.NextWakeAt);
        // Both time inputs are controlled: plan exactly at the returned deadline.
        var fastAt = completion.Schedule.NextWakeAt!.Value;
        var fastOnly = tracker.CreatePlan(fastAt);
        if (fastOnly.Request is { } fast) tracker.MarkSampled(fast, fastAt);
        Assert.Equal(new[] { "basic" }, fastOnly.DueItemIds);
        Assert.Equal(new[] { "image", "sid" }, fastOnly.Request!);
        Assert.Equal(3, fastOnly.SourceViews.Count);
        tracker.Remove("image-fast", fastAt.AddMilliseconds(1));
        var retained = tracker.CreatePlan(now.AddSeconds(2));
        if (retained.Request is { } pending) tracker.MarkSkipped(pending, now.AddSeconds(2));
        Assert.Null(retained.Request);
        Assert.Contains("network", retained.ActiveItemIds);
        Assert.Contains("basic", retained.ActiveItemIds);
        tracker.Clear(now.AddSeconds(2));
    }

    [Fact]
    public void OriginalNativePlanExposesActiveSourceViewsEvenWhenNoItemsAreDue()
    {
        var configuration = Configuration();
        using var owner = new StandaloneSamplingSubscriptionSessionOwner(Settings(), in configuration, 1);
        var tracker = new NativeItemSamplingSubscriptionTracker<string[], SubscriptionRunPermit>(owner, 1,
            static fields => string.Join(',', fields), static _ => ["basic"],
            static (_, sources) => sources.SelectMany(static source => source.Request).Distinct().ToArray(), false);
        var now = DateTimeOffset.UtcNow;
        tracker.TrackPersistent("fast", ["image"], now, TimeSpan.FromSeconds(1));
        tracker.TrackPersistent("slow", ["sid"], now, TimeSpan.FromMinutes(1));
        var first = tracker.CreatePlan(now);
        Assert.Equal(new[] { "image", "sid" }, first.Request!.Order().ToArray());
        tracker.MarkSampled(first.Request!, now);
        var idle = tracker.CreatePlan(now.AddMilliseconds(1));
        Assert.Null(idle.Request);
        Assert.Equal(2, idle.SourceViews.Count);
        Assert.Equal(new[] { "basic" }, idle.ActiveItemIds);
        tracker.Remove("fast", now.AddMilliseconds(2));
        var remaining = tracker.CreatePlan(now.AddMilliseconds(3));
        Assert.Single(remaining.SourceViews);
        Assert.Equal(new[] { "sid" }, remaining.SourceViews[0].Request);
        if (remaining.Request is { } pending) tracker.MarkSkipped(pending, now.AddMilliseconds(3));
        tracker.Clear(now.AddMilliseconds(4));
    }

    [Fact]
    public async Task DuplicateClientLeasesStayIndependentAndSharedDatasetCapturesAllActiveFields()
    {
        using var scope = new RecordedScope();
        await using var composition = Create(scope);
        await composition.StartAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scope.BeforeCollect = async _ => { entered.TrySetResult(); await release.Task; };
        using var fast = composition.Acquire("duplicate", [scope.A], TimeSpan.FromMilliseconds(100));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var slow = composition.Acquire("duplicate", [scope.B], TimeSpan.FromMinutes(1));
        scope.BeforeCollect = null;
        release.SetResult();
        _ = await scope.Calls.Reader.ReadAsync();
        var merged = await scope.Calls.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains(scope.A, merged);
        Assert.Contains(scope.B, merged);
        fast.Dispose();
        Assert.True(scope.Commits > 0);
        await composition.StopAsync();
        Assert.Empty(scope.Demand);
    }

    [Fact]
    public async Task PublicCompositionReturnsCollectionFailureWithoutRetrying()
    {
        using var scope = new RecordedScope();
        var configuration = Configuration();
        var failureReported = new TaskCompletionSource<SubscriptionWorkerFailure>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var composition = new NativeFieldSubscriptionComposition(scope, Settings(), in configuration,
            failure => failureReported.TrySetResult(failure));
        await composition.StartAsync();
        scope.BeforeCollect = _ => throw new InvalidOperationException("Expected collection failure.");
        using var lease = composition.Acquire("fault-return", [scope.A], TimeSpan.FromSeconds(1));

        var failure = await failureReported.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(SubscriptionWorkerFailureStage.Collection, failure.Stage);
        Assert.Equal(1, scope.Captures);
        await Task.Delay(TimeSpan.FromMilliseconds(100));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => composition.StopAsync().WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("Expected collection failure.", error.Message);
        Assert.Equal(1, scope.Captures);
    }

    [Fact]
    public async Task PublicCompositionLongIntervalCanBeWokenByNewDemandAndWithdrawsLastLease()
    {
        using var scope = new RecordedScope();
        scope.BindOverride = field => new(field,
            [field.FieldName == "image" ? "image" : "sid"], ["basic"]);
        var configuration = Configuration();
        var failureReported = new TaskCompletionSource<SubscriptionWorkerFailure>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var composition = new NativeFieldSubscriptionComposition(scope, Settings(), in configuration,
            failure => failureReported.TrySetResult(failure));
        await composition.StartAsync();

        var bothDemanded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scope.DemandChanged = fields =>
        {
            if (fields.Contains(scope.A) && fields.Contains(scope.B)) bothDemanded.TrySetResult();
            else if (fields.IsEmpty && bothDemanded.Task.IsCompleted) released.TrySetResult();
        };

        var interval = TimeSpan.FromDays(365 * 50);
        using var first = composition.Acquire("long-first", [scope.A], interval);
        var firstCapture = await scope.Calls.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains(scope.A, firstCapture);
        await Task.Delay(TimeSpan.FromMilliseconds(100));
        Assert.False(failureReported.Task.IsCompleted);
        Assert.Equal(1, scope.Captures);

        using var second = composition.Acquire("long-second", [scope.B], interval);
        await bothDemanded.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var secondCapture = await scope.Calls.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains(scope.B, secondCapture);
        await Task.Delay(TimeSpan.FromMilliseconds(100));
        Assert.False(failureReported.Task.IsCompleted);
        Assert.Equal(2, scope.Captures);

        second.Dispose();
        first.Dispose();
        await released.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(scope.Demand);
        await composition.StopAsync();
    }

    internal static SubscriptionRoleSettings Settings() => new(1, 1000, 1, 3000, 1000, 1000);
    internal static NativeSamplingSubscriptionConfiguration Configuration() => new()
    {
        AbiVersion = NativeSamplingSubscriptionAbi.Version,
        StructSize = (uint)Unsafe.SizeOf<NativeSamplingSubscriptionConfiguration>(), Generation = 1,
        MaximumSourceCount = 32, MaximumItemCount = 32, MaximumMembershipCount = 128,
        MaximumDueItemCount = 32, MaximumSourceViewCount = 32, MaximumExpiredSourceCount = 32,
        DefaultIntervalMilliseconds = 1000, MinimumIntervalMilliseconds = 1,
        ActiveTtlMilliseconds = 3000, MaximumFutureSkewMilliseconds = 1000
    };
    private static NativeFieldSubscriptionComposition Create(RecordedScope scope)
    {
        var configuration = Configuration();
        return new(scope, Settings(), in configuration, failure => throw new InvalidOperationException("Unexpected worker failure", failure.Error));
    }
    private sealed class FrozenClock : TimeProvider
    {
        public override long GetTimestamp() => 0;
    }

    private sealed class RecordedScope : IReadWriteCollectionBorrow, IReadWriteBackend
    {
        internal readonly FieldReference A = new("recorded-process", "image");
        internal readonly FieldReference B = new("recorded-process", "sid");
        public IReadWriteBackend Backend => this;
        public SamplingPublicationSignal Publications { get; } = new();
        public ReadWriteCatalog Catalog { get; }
        internal readonly Channel<ImmutableArray<FieldReference>> Calls = Channel.CreateUnbounded<ImmutableArray<FieldReference>>();
        internal ImmutableArray<FieldReference> Demand = [];
        internal Func<IMetricPublicationPermit, Task>? BeforeCollect;
        internal Action<ImmutableArray<FieldReference>>? DemandChanged;
        internal Func<FieldReference, FieldCollectionBinding>? BindOverride;
        internal int Captures, Commits;
        private ImmutableArray<FieldReading> current = [];
        internal RecordedScope() => Catalog = new([new("recorded-process", "process", null)],
            new[] { A, B }.Select(field => new FieldCapability(field, new(field.FieldName, "text", "string"),
                new(true, null, []), OperationSupport.Unsupported("read-only"), "recorded")));
        public ImmutableArray<FieldCollectionBinding> Bind(ImmutableArray<FieldReference> fields)
            => [.. fields.Select(field => { Catalog.Require(field); return BindOverride?.Invoke(field)
                ?? new FieldCollectionBinding(field, ["basic"], ["basic"]); })];
        public void SynchronizeDemand(ImmutableArray<FieldReference> activeFields)
        { Demand = activeFields; DemandChanged?.Invoke(activeFields); }
        public async ValueTask<FieldCollectionResult> CollectAsync(ImmutableArray<FieldReference> fields, IMetricPublicationPermit permit, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Captures);
            if (BeforeCollect is { } before) await before(permit);
            var values = fields.Select(field => new FieldReading(field, FieldObservationStatus.Current,
                new(Text: "recorded"), DateTimeOffset.UtcNow, "recorded")).ToImmutableArray();
            var committed = permit.TryPublish(() => { current = values; Interlocked.Increment(ref Commits); Publications.Publish(["basic"]); });
            Calls.Writer.TryWrite(fields);
            return new(values, committed);
        }
        public ValueTask<ReadWriteCatalog> DiscoverAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Catalog);
        public FieldReading ReadCached(FieldReference field) => current.FirstOrDefault(value => value.Field == field)
            ?? new(field, FieldObservationStatus.Unavailable, null, null, "recorded");
        public ValueTask<ImmutableArray<FieldReading>> CollectAsync(ImmutableArray<FieldReference> fields, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<FieldWriteResult> WriteAsync(FieldReference field, FieldValue value, CancellationToken cancellationToken) => throw new NotSupportedException();
        public void Dispose() => Demand = [];
    }
}
