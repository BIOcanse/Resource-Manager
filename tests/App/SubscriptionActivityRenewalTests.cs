using System.Collections.Concurrent;
using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.StateItems;
using ResourceManager.Shared.BusinessBlocks.State;
using ResourceManager.Subscription;

namespace Resource_Manager_APP.Tests;

public sealed class SubscriptionActivityRenewalTests
{
    public static IEnumerable<object[]> ActivityKinds()
        => new[] { new object[] { -1 } }.Concat(Enum.GetValues<SubscriptionFieldGroup>()
            .Select(static group => new object[] { (int)group }));

    [Theory]
    [MemberData(nameof(ActivityKinds))]
    public async Task StoppedExpiryReadAndRenewalRetainBothEdgesBeforeDeferredNotification(int group)
    {
        var clock = new Clock();
        var owner = new StoppedOwner();
        var tracker = Tracker(owner, clock);
        var activity = new SubscriptionActivity();
        var (read, subscribe) = Bind(activity, tracker, group);
        var notifications = 0;
        var binding = new SubscriptionActivityFacts.ActivityBinding(
            () => StateRead<bool>.FromValue(read()), () => { notifications++; return Task.CompletedTask; });
        var pending = new List<Task>();
        var edges = new List<bool>();
        subscribe(() => { edges.Add(read()); pending.Add(binding.NotifyAsync()); });
        tracker.Track(new("source", "disk"), clock.GetUtcNow());
        await Task.WhenAll(pending);
        Assert.Equal(1, notifications);
        Assert.Equal(new[] { true }, edges);
        clock.Advance(TimeSpan.FromMilliseconds(1001));
        Assert.False(read());
        Assert.False(read());
        Assert.Equal(1, notifications); // Expiry reads are pure: no event, mutation or workspace acquisition.
        pending.Clear();

        var deferred = new DeferredContext();
        WithContext(deferred, () => tracker.Track(new("source", "disk"), clock.GetUtcNow()));
        Assert.Equal(new[] { true, false, true }, edges);
        Assert.Equal(1, notifications);
        deferred.Drain();
        await Task.WhenAll(pending);
        Assert.Equal(3, notifications);
        Assert.True(read());

        pending.Clear();
        tracker.TrackPersistent(new("another", "disk"), clock.GetUtcNow(), TimeSpan.FromSeconds(1));
        await Task.WhenAll(pending);
        Assert.Equal(new[] { true, false, true }, edges); // Adding while in use does not emit a truth edge.
        Assert.Equal(3, notifications);
        Assert.Equal(0, owner.Acquisitions);
    }

    [Theory]
    [MemberData(nameof(ActivityKinds))]
    public async Task RenewingExpiredActivityRestartsTheActualControllerAfterItObservedFalse(int group)
    {
        var clock = new Clock();
        var tracker = Tracker(new StoppedOwner(), clock);
        var activity = new SubscriptionActivity();
        var (read, subscribe) = Bind(activity, tracker, group);
        var probe = new Probe();
        await using var fixture = NativeBackendBlockTestSupport.CreateFullTreeFixture(
            new Dictionary<int, IBusinessBlock> { [BackendUids.ControlActualStateReader] = probe });
        var current = fixture.Authority.ClaimReadFunction<bool>(StateItemUids.ActualControlValuesInUse,
            () => StateRead<bool>.FromValue(read()));
        var binding = new SubscriptionActivityFacts.ActivityBinding(() => StateRead<bool>.FromValue(read()),
            () => current.NotifyChangedAsync(BusinessBlockTestSupport.Reason()));
        var pending = new List<Task>();
        subscribe(() => pending.Add(binding.NotifyAsync()));
        await InitializeControllerFactsAsync(fixture);
        tracker.Track(new("source", "disk"), clock.GetUtcNow());
        await Task.WhenAll(pending);
        await fixture.WaitForStateAsync(BackendUids.ControlActualStateReader, BusinessBlockState.Running);
        Assert.Equal(1, probe.Starts);

        clock.Advance(TimeSpan.FromMilliseconds(1001));
        Assert.False(read());
        // Another legitimate scheduler turn observes expiry without changing the
        // activity owner's last notification. Renewal must still invalidate it.
        await current.NotifyChangedAsync(BusinessBlockTestSupport.Reason());
        await fixture.WaitForStateAsync(BackendUids.ControlActualStateReader, BusinessBlockState.Stopped);
        pending.Clear();
        var deferred = new DeferredContext();
        WithContext(deferred, () => tracker.Track(new("source", "disk"), clock.GetUtcNow()));
        deferred.Drain();
        await Task.WhenAll(pending);
        await fixture.WaitForStateAsync(BackendUids.ControlActualStateReader, BusinessBlockState.Running);
        Assert.Equal(2, probe.Starts);
    }

    [Theory]
    [MemberData(nameof(ActivityKinds))]
    public async Task RunningNativePlanRemovesExpiredActivityAndStopsTheActualController(int group)
    {
        using var fixture = new HostManagerSamplingSubscriptionTestFixture();
        var clock = new Clock();
        var tracker = new ResourceManager.Subscription.NativeItemSamplingSubscriptionTracker<Request, SamplingOwnerToken>(
            new HostSamplingSessionOwnerAdapter(fixture.Owner), 1, static request => request.Key,
            static request => [request.Item], static (_, sources) => sources.Count == 0 ? null : sources[0].Request,
            false, clock);
        var activity = new SubscriptionActivity();
        var (read, subscribe) = Bind(activity, tracker, group);
        var edges = new List<bool>();
        subscribe(() => edges.Add(read()));
        var probe = new Probe();
        await using var controller = NativeBackendBlockTestSupport.CreateFullTreeFixture(
            new Dictionary<int, IBusinessBlock> { [BackendUids.ControlActualStateReader] = probe });
        var current = controller.Authority.ClaimReadFunction<bool>(StateItemUids.ActualControlValuesInUse,
            () => StateRead<bool>.FromValue(read()));
        var binding = new SubscriptionActivityFacts.ActivityBinding(() => StateRead<bool>.FromValue(read()),
            () => current.NotifyChangedAsync(BusinessBlockTestSupport.Reason()));
        var notifications = new List<Task>();
        subscribe(() => notifications.Add(binding.NotifyAsync()));
        await InitializeControllerFactsAsync(controller);
        tracker.Track(new("source", "cpu.usage"), clock.GetUtcNow());
        await Task.WhenAll(notifications);
        await controller.WaitForStateAsync(BackendUids.ControlActualStateReader, BusinessBlockState.Running);
        Assert.Equal(1, probe.Starts);
        var first = tracker.CreatePlan(clock.GetUtcNow());
        tracker.CompletePlan(first, clock.GetUtcNow(), NativeSamplingSubscriptionCompletionStatus.Sampled);
        clock.Advance(TimeSpan.FromMilliseconds(fixture.Owner.GetRoleSettings(1)!.ActiveTtlMilliseconds + 1));
        Assert.False(read());
        var expired = tracker.CreatePlan(clock.GetUtcNow());
        Assert.Null(expired.Request);
        Assert.Empty(expired.DueItemIds);
        Assert.Empty(expired.Triggers);
        await Task.WhenAll(notifications);
        await controller.WaitForStateAsync(BackendUids.ControlActualStateReader, BusinessBlockState.Stopped);
        Assert.Equal(new[] { true, false }, edges);
        Assert.Equal((0, 0), tracker.CaptureManagedSourceState());
        Assert.False(read());
        tracker.CloseOutputScratch();
    }

    private static async Task InitializeControllerFactsAsync(NativeBackendBlockFixture fixture)
    {
        int[] nativeFacts = [BackendItems.PresentNativeSessionAvailable, BackendItems.GpuAllocationNativeSessionAvailable,
            BackendItems.VidMmNativeSessionAvailable];
        foreach (var declaration in StateRegistry.Items.Where(item => item.ValueType == typeof(bool)
            && item.Storage == StateItemStorage.Reference && !item.PerBlock
            && item.Uid != StateItemUids.ActualControlValuesInUse && !nativeFacts.Contains(item.Uid)))
            await fixture.Authority.ClaimCell<bool>(declaration.Uid).PublishAsync(false, BusinessBlockTestSupport.Reason());
        await fixture.InitializeHostPermitsAsync();
    }

    private static (Func<bool> Read, Action<Action> Subscribe) Bind<TContext>(SubscriptionActivity activity,
        ResourceManager.Subscription.NativeItemSamplingSubscriptionTracker<Request, TContext> tracker, int group)
    {
        if (group < 0)
        {
            activity.BindRole(1, () => tracker.HasInUseSubscriptions, handler => tracker.SubscriptionsChanged += handler);
            return (() => activity.HasInUseSubscriptions(1), handler => activity.RoleChanged += _ => handler());
        }
        var field = (SubscriptionFieldGroup)group;
        activity.BindField(field, tracker.CreateActivityRead(static _ => true), handler => tracker.SubscriptionsChanged += handler);
        return (() => activity.HasInUseSubscriptions(field), handler => activity.FieldChanged += _ => handler());
    }

    private static ResourceManager.Subscription.NativeItemSamplingSubscriptionTracker<Request, string> Tracker(StoppedOwner owner, TimeProvider clock)
        => new(owner, 1, static request => request.Key, static request => [request.Item],
            static (_, sources) => sources.Count == 0 ? null : sources[0].Request, false, clock);
    private sealed record Request(string Key, string Item);
    private sealed class StoppedOwner : ISamplingSubscriptionSessionOwner<string>, ISamplingSubscriptionSettingsSource
    {
        internal int Acquisitions;
        private readonly ISubscriptionRoleSettings settings = new SubscriptionRoleSettings(1, 1000, 1, 1000, 0, 100);
        public ISubscriptionRoleSettings GetRoleSettings(int roleId) => settings;
        public ISamplingSubscriptionSessionLease<string> Acquire(int roleId)
        { Acquisitions++; throw new InvalidOperationException("The owner is stopped."); }
        public ISamplingSubscriptionSessionLease<string>? TryAcquireForCleanup(int roleId) => null;
    }
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        private long timestamp;
        public override DateTimeOffset GetUtcNow() => now;
        public override long GetTimestamp() => timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        internal void Advance(TimeSpan elapsed) { now += elapsed; timestamp += elapsed.Ticks; }
    }
    private sealed class Probe : IBusinessBlock
    {
        internal int Starts;
        public async Task RunAsync(BusinessBlockRunContext context)
        {
            Interlocked.Increment(ref Starts);
            context.ReportRunning();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, context.StopNotification); }
            catch (OperationCanceledException) when (context.StopNotification.IsCancellationRequested) { }
        }
    }
    private static void WithContext(SynchronizationContext context, Action action)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);
        try { action(); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }
    private sealed class DeferredContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> continuations = new();
        public override void Post(SendOrPostCallback callback, object? state) => continuations.Enqueue((callback, state));
        internal void Drain() { while (continuations.TryDequeue(out var item)) item.Callback(item.State); }
    }
}
