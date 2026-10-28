using ResourceManager.Subscription;
using ResourceManager.Subscription.Native;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.Shared.BusinessBlocks.State;

namespace Resource_Manager_APP.Tests;

public sealed class SubscriptionActivityTests
{
    [Fact]
    public async Task PrepublicationSubscriptionsRemainManagedAndFactsAreExplicitlyUnpublished()
    {
        var deployment = new HostManagerDeploymentState();
        var provider = new RuntimePlanProvider(deployment);
        using var owner = new HostManagerSamplingSubscriptionOwner(provider,
            new HostManagerSamplingSubscriptionRuntime(provider, deployment),
            NullLogger<HostManagerSamplingSubscriptionOwner>.Instance);
        using var facts = new SubscriptionActivityFacts(owner, null!, NullLogger<SubscriptionActivityFacts>.Instance);
        var notifications = 0;
        var binding = new SubscriptionActivityFacts.ActivityBinding(() => facts.ReadRole(1),
            () => { notifications++; return Task.CompletedTask; });
        await binding.NotifyAsync();
        Assert.Equal(1, notifications);
        var tracker = new ResourceManager.App.Infrastructure.Monitoring.NativeItemSamplingSubscriptionTracker<Request>(
            owner, 1, static request => request.Key, static request => [request.Item],
            static (_, sources) => sources[0].Request, false);
        owner.Activity.BindField(SubscriptionFieldGroup.DiskAttribution,
            tracker.CreateActivityRead(static request => request.Item == "disk"), handler => tracker.SubscriptionsChanged += handler);
        tracker.ValidatePeriod(TimeSpan.FromMilliseconds(1));
        tracker.TrackPersistent(new("before-plan", "disk"), DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1));
        Assert.False(tracker.HasInUseSubscriptions);
        Assert.Null(owner.GetRoleSettings(1));
        Assert.Equal(NoValueReason.NotPublished, facts.ReadRole(1).NoValueReason);
        Assert.Equal(NoValueReason.NotPublished, facts.ReadField(SubscriptionFieldGroup.DiskAttribution).NoValueReason);
        Assert.Equal((1, 1), tracker.CaptureManagedSourceState());
        await binding.NotifyAsync();
        Assert.Equal(1, notifications);

        var publications = 0;
        owner.SettingsPublished += () => publications++;
        var plan = HostManagerTestPlanFactory.CreatePlan();
        provider.Publish(CompiledRuntimePlan.Default with { Version = 1, HostManager = plan });
        Assert.Equal(1, publications);
        Assert.True(facts.ReadRole(1).Value);
        Assert.True(facts.ReadField(SubscriptionFieldGroup.DiskAttribution).Value);
        Assert.NotNull(owner.GetRoleSettings(1));
        await binding.NotifyAsync();
        Assert.Equal(2, notifications); // NotPublished -> published current value is a validity edge.

        await owner.StopAsync(CancellationToken.None);
        provider.Publish(CompiledRuntimePlan.Default with { Version = 2, HostManager = plan });
        Assert.Equal(2, publications);
        await binding.NotifyAsync();
        Assert.Equal(2, notifications); // The same truth in a new plan produces no state event.
        Assert.True(tracker.Remove("before-plan", DateTimeOffset.UtcNow));
        Assert.False(facts.ReadRole(1).Value);
        await binding.NotifyAsync();
        Assert.Equal(3, notifications);
    }

    [Fact]
    public void StoppedSourceTracksAndReadsActivityWithoutAcquiringWorkspace()
    {
        var clock = new ActivityClock();
        var owner = new StoppedOwner();
        var tracker = Tracker(owner, clock);
        var activity = new SubscriptionActivity();
        activity.BindRole(1, () => tracker.HasInUseSubscriptions, handler => tracker.SubscriptionsChanged += handler);
        activity.BindField(SubscriptionFieldGroup.DiskAttribution,
            () => tracker.HasInUseSubscriptionsFor(static request => request.Item == "disk"),
            handler => tracker.SubscriptionsChanged += handler);
        var roleChanges = 0;
        var fieldChanges = 0;
        activity.RoleChanged += _ => roleChanges++;
        activity.FieldChanged += _ => fieldChanges++;

        tracker.TrackPersistent(new("first", "disk"), clock.GetUtcNow(), TimeSpan.FromSeconds(1));
        tracker.TrackPersistent(new("second", "disk"), clock.GetUtcNow(), TimeSpan.FromSeconds(1));
        Assert.True(activity.HasInUseSubscriptions(1));
        Assert.True(activity.HasInUseSubscriptions(SubscriptionFieldGroup.DiskAttribution));
        Assert.Equal(1, roleChanges);
        Assert.Equal(1, fieldChanges);
        tracker.Remove("first", clock.GetUtcNow());
        Assert.Equal(1, roleChanges);
        tracker.Remove("second", clock.GetUtcNow());
        Assert.False(activity.HasInUseSubscriptions(1));
        Assert.False(activity.HasInUseSubscriptions(SubscriptionFieldGroup.DiskAttribution));
        Assert.Equal(2, roleChanges);
        Assert.Equal(2, fieldChanges);
        Assert.Equal(0, owner.WorkspaceAcquisitions);
    }

    [Fact]
    public void ExpiredSourceReadsFalseWhileStoppedWithoutAddingTimerOrExpiryEvent()
    {
        var clock = new ActivityClock();
        var owner = new StoppedOwner();
        var tracker = Tracker(owner, clock);
        var activity = new SubscriptionActivity();
        activity.BindRole(1, () => tracker.HasInUseSubscriptions, handler => tracker.SubscriptionsChanged += handler);
        var changes = 0;
        activity.RoleChanged += _ => changes++;
        tracker.Track(new("transient", "disk"), clock.GetUtcNow());
        Assert.True(activity.HasInUseSubscriptions(1));
        clock.Advance(TimeSpan.FromMilliseconds(1000));
        Assert.True(activity.HasInUseSubscriptions(1));
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.False(activity.HasInUseSubscriptions(1));
        Assert.Equal(1, changes);
        Assert.Equal(0, owner.WorkspaceAcquisitions);
        tracker.Remove("transient", clock.GetUtcNow());
        Assert.Equal(2, changes);
    }

    [Fact]
    public async Task PublishedActivityReadsDoNotTakeMutationLocksOrClassifyRequests()
    {
        var clock = new ActivityClock();
        var owner = new StoppedOwner();
        var tracker = Tracker(owner, clock);
        var activity = new SubscriptionActivity();
        var classifications = 0;
        var fieldRead = tracker.CreateActivityRead(request => { classifications++; return request.Item == "disk"; });
        activity.BindRole(1, () => tracker.HasInUseSubscriptions, handler => tracker.SubscriptionsChanged += handler);
        activity.BindField(SubscriptionFieldGroup.DiskAttribution, fieldRead, handler => tracker.SubscriptionsChanged += handler);
        tracker.TrackPersistent(new("source", "disk"), clock.GetUtcNow(), TimeSpan.FromSeconds(1));
        var classificationsAfterWrite = classifications;
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var trackerGate = tracker.GetType().GetField("gate", flags)!.GetValue(tracker)!;
        var activityGate = typeof(SubscriptionActivity).GetField("gate", flags)!.GetValue(activity)!;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var writer = Task.Run(() =>
        {
            lock (trackerGate) lock (activityGate)
            {
                entered.Set();
                release.Wait();
            }
        });
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        try
        {
            await Task.Run(() =>
            {
                for (var index = 0; index < 1000; index++)
                {
                    Assert.True(activity.HasInUseSubscriptions(1));
                    Assert.True(activity.HasInUseSubscriptions(SubscriptionFieldGroup.DiskAttribution));
                }
            }).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { release.Set(); await writer; }
        Assert.Equal(classificationsAfterWrite, classifications);

        for (var index = 0; index < 100; index++) _ = activity.HasInUseSubscriptions(1);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 1000; index++)
        {
            _ = activity.HasInUseSubscriptions(1);
            _ = activity.HasInUseSubscriptions(SubscriptionFieldGroup.DiskAttribution);
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public async Task CollectionFailureSettlesAndReturnsInsteadOfRestartingItself()
    {
        using var fixture = new HostManagerSamplingSubscriptionTestFixture();
        var tracker = new ResourceManager.App.Infrastructure.Monitoring.NativeItemSamplingSubscriptionTracker<Request>(
            fixture.Owner, 1, static request => request.Key, static request => [request.Item],
            static (_, sources) => sources[0].Request, false);
        tracker.TrackPersistent(new("source", "cpu.usage"), DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1));
        using var signal = new SemaphoreSlim(0, 1);
        var failures = new List<SubscriptionWorkerFailure>();
        var worker = new NativeSamplingSubscriptionWorker<Request, ResourceManager.App.Infrastructure.RuntimeSpecialization.SamplingOwnerToken>(
            tracker, signal, static _ => { }, failures.Add);
        var attempts = 0;
        await Assert.ThrowsAsync<IOException>(() => worker.RunAsync((_, _) =>
        {
            attempts++;
            throw new IOException("collection failed");
        }, CancellationToken.None));
        Assert.Equal(1, attempts);
        Assert.Single(failures);
        Assert.True(tracker.HasInUseSubscriptions);
        // A new externally authorized run can acquire and plan the same subscription.
        var plan = tracker.CreatePlan(DateTimeOffset.UtcNow.AddSeconds(2));
        if (plan.Request is not null) tracker.MarkSkipped(plan.Request, DateTimeOffset.UtcNow.AddSeconds(2));
        tracker.Clear(DateTimeOffset.UtcNow.AddSeconds(2));
    }

    private static NativeItemSamplingSubscriptionTracker<Request, string> Tracker(StoppedOwner owner, TimeProvider clock)
        => new(owner, 1, static request => request.Key, static request => [request.Item],
            static (_, sources) => sources[0].Request, false, clock);
    private sealed record Request(string Key, string Item);
    private sealed class StoppedOwner : ISamplingSubscriptionSessionOwner<string>, ISamplingSubscriptionSettingsSource
    {
        public int WorkspaceAcquisitions;
        private readonly ISubscriptionRoleSettings settings = new SubscriptionRoleSettings(1, 1000, 1, 1000, 0, 100);
        public ISubscriptionRoleSettings GetRoleSettings(int roleId) => settings;
        public ISamplingSubscriptionSessionLease<string> Acquire(int roleId)
        {
            WorkspaceAcquisitions++;
            throw new InvalidOperationException("The sampling owner is stopped.");
        }
        public ISamplingSubscriptionSessionLease<string>? TryAcquireForCleanup(int roleId) => null;
    }
    private sealed class ActivityClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.Parse("2026-10-27T00:00:00Z");
        private long timestamp;
        public override DateTimeOffset GetUtcNow() => now;
        public override long GetTimestamp() => timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        internal void Advance(TimeSpan elapsed) { now += elapsed; timestamp += elapsed.Ticks; }
    }
}
