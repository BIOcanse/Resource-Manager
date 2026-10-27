using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.Monitoring;
using System.Text.Json.Nodes;

namespace Resource_Manager_APP.Tests;

public sealed class ItemSamplingSubscriptionTrackerTests
{
    [Fact]
    public void OverCapacityRequestHasNamedFailureAndPreservesExistingDemand()
    {
        using var fixture = new HostManagerSamplingSubscriptionTestFixture(
            AllMetricSubscriptionTestData.BoundedPlan(2, 2, 2));
        var tracker = CreateMetricTracker(fixture);
        var at = DateTimeOffset.UtcNow;
        tracker.TrackPersistent("old", MetricSampleRequest.ForIds(["cpu.usage"]), at, TimeSpan.FromSeconds(1));
        var failure = Assert.Throws<SamplingSubscriptionCapacityException>(() => tracker.TrackPersistent(
            "large", MetricSampleRequest.ForIds(["one", "two", "three"]), at, TimeSpan.FromSeconds(1)));
        Assert.Equal(NativeSamplingSubscriptionStatus.ItemCapacityExceeded, failure.Reason);
        Assert.Single(ReadNativeState(fixture).Sources);
        Assert.Single(ReadNativeState(fixture).Items);
        tracker.TrackPersistent("small", MetricSampleRequest.ForIds(["memory.usage"]), at, TimeSpan.FromSeconds(1));
        var plan = tracker.CreatePlan(at);
        Assert.Equal(new[] { "cpu.usage", "memory.usage" }, plan.DueItemIds);
        tracker.MarkSampled(plan.Request!, at);
    }

    [Fact]
    public void CaptureDurationBelowIntervalKeepsTheOriginalItemPhase()
    {
        using var fixture = new HostManagerSamplingSubscriptionTestFixture();
        var tracker = CreateMetricTracker(fixture);
        var at = DateTimeOffset.Parse("2026-10-25T00:00:00Z");
        tracker.TrackPersistent("bars", MetricSampleRequest.ForIds(["cpu.usage"]), at, TimeSpan.FromSeconds(1));
        var first = tracker.CreatePlan(at);
        var initial = ReadNativeState(fixture).Items.Single();
        tracker.MarkSampled(first.Request!, at.AddMilliseconds(325));
        var due = ReadNativeState(fixture).Items.Single().ScheduledDueMilliseconds;
        Assert.Equal(initial.ScheduledDueMilliseconds + 1000, due);
        var secondAt = DateTimeOffset.FromUnixTimeMilliseconds(checked((long)due));
        var second = tracker.CreatePlan(secondAt);
        Assert.NotNull(second.Request);
        tracker.MarkSampled(second.Request!, secondAt.AddMilliseconds(200));
        Assert.Equal(due + 1000, ReadNativeState(fixture).Items.Single().ScheduledDueMilliseconds);
    }
    [Fact]
    public async Task NormalDeadlineAndNewDemandWakeWithoutCancellationExceptionsOrOrphanWaits()
    {
        using var fixture = new HostManagerSamplingSubscriptionTestFixture();
        var tracker = CreateMetricTracker(fixture);
        using var signal = new SemaphoreSlim(0, 1);
        var clock = new ManualWakeClock();
        var worker = new NativeSamplingSubscriptionWorker<MetricSampleRequest, SamplingOwnerToken>(
            tracker, signal, _ => { }, failure => throw failure.Error, TimeSpan.Zero, clock);
        var wait = worker.GetType().GetMethod("WaitForDelaySegmentAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var cancellations = 0;
        var observing = new AsyncLocal<bool> { Value = true };
        void Observe(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs args)
        {
            if (observing.Value && args.Exception is OperationCanceledException
                && args.Exception.StackTrace?.Contains("WaitForDelaySegmentAsync") == true) Interlocked.Increment(ref cancellations);
        }
        AppDomain.CurrentDomain.FirstChanceException += Observe;
        try
        {
            for (var i = 0; i < 20; i++)
            {
                var deadline = (Task)wait.Invoke(worker, [TimeSpan.FromSeconds(1), true, CancellationToken.None])!;
                Assert.False(deadline.IsCompleted);
                clock.Fire();
                await deadline.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(clock.Last!.Disposed);
                var demand = (Task)wait.Invoke(worker, [TimeSpan.FromHours(1), true, CancellationToken.None])!;
                Assert.False(demand.IsCompleted);
                worker.NotifyDemandChanged();
                await demand.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(clock.Last!.Disposed);
                Assert.Equal(0, signal.CurrentCount);
            }
            Assert.Equal(0, cancellations);
        }
        finally { AppDomain.CurrentDomain.FirstChanceException -= Observe; }
    }

    private sealed class ManualWakeClock : TimeProvider
    {
        internal WakeTimer? Last;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            => Last = new(callback, state);
        internal void Fire() => Last!.Fire();
        internal sealed class WakeTimer(TimerCallback callback, object? state) : ITimer
        {
            internal bool Disposed;
            internal void Fire() { if (!Disposed) callback(state); }
            public bool Change(TimeSpan dueTime, TimeSpan period) => !Disposed;
            public void Dispose() => Disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    [Fact]
    public void ActiveItemsIncludeSlowDemandDuringNoDueAndFastOnlyPlans()
    {
        using var fixture = new HostManagerSamplingSubscriptionTestFixture();
        var tracker = CreateMetricTracker(fixture);
        Assert.Equal("ResourceManager.SubscriptionCore", tracker.GetType().BaseType!.Assembly.GetName().Name);
        var now = DateTimeOffset.Parse("2026-10-06T00:00:00Z");
        tracker.TrackPersistent("slow", MetricSampleRequest.ForIds(["slow.ratio"]), now, TimeSpan.FromMinutes(1));
        tracker.TrackPersistent("fast", MetricSampleRequest.ForIds(["cpu.usage"]), now, TimeSpan.FromSeconds(1));
        var initial = tracker.CreatePlan(now);
        Assert.Equal(new[] { "cpu.usage", "slow.ratio" }, initial.ActiveItemIds);
        tracker.MarkSampled(initial.Request!, now);
        var waiting = tracker.CreatePlan(now.AddMilliseconds(100));
        Assert.Null(waiting.Request); Assert.Empty(waiting.DueItemIds);
        Assert.Equal(initial.ActiveItemIds, waiting.ActiveItemIds);
        var fast = tracker.CreatePlan(now.AddSeconds(2));
        Assert.Equal(new[] { "cpu.usage" }, fast.DueItemIds);
        Assert.Equal(initial.ActiveItemIds, fast.ActiveItemIds);
        tracker.MarkSampled(fast.Request!, now.AddSeconds(2));
        Assert.True(tracker.Remove("slow", now.AddSeconds(2)));
        Assert.Equal(new[] { "cpu.usage" }, tracker.CreatePlan(now.AddSeconds(2).AddMilliseconds(1)).ActiveItemIds);
        Assert.True(tracker.Remove("fast", now.AddSeconds(2).AddMilliseconds(2)));
        var removed = tracker.CreatePlan(now.AddSeconds(2).AddMilliseconds(3));
        Assert.False(removed.IsActive); Assert.Empty(removed.ActiveItemIds);
    }

    [Fact]
    public void EquivalentPublishedPlanKeepsTheExistingWorkspaceIncarnation()
    {
        using var fixture = new HostManagerSamplingSubscriptionTestFixture();
        using var before = fixture.Owner.Acquire(roleId: 1);
        var equivalent = HostManagerTestPlanFactory.CreatePlan();

        fixture.Provider.Publish(CompiledRuntimePlan.Default with
        {
            Version = 2,
            HostManager = equivalent
        });

        using var after = fixture.Owner.Acquire(roleId: 1);
        Assert.Same(before.Session, after.Session);
        Assert.Equal(before.WorkspaceIncarnation, after.WorkspaceIncarnation);
    }

    [Fact]
    public void CreatePlan_UsesNativeDueItemAsTheExactCompletionSet()
    {
        using var fixture = new HostManagerSamplingSubscriptionTestFixture();
        var tracker = CreateMetricTracker(fixture);
        var startedAt = DateTimeOffset.Parse("2026-07-09T00:00:00Z");
        tracker.Track(MetricSampleRequest.ForIds(["cpu.usage"]), startedAt);

        var plan = tracker.CreatePlan(startedAt.AddSeconds(3));

        Assert.NotNull(plan.Request);
        Assert.Equal(["cpu.usage"], plan.Request!.Ids, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(["cpu.usage"], plan.DueItemIds, StringComparer.OrdinalIgnoreCase);
        Assert.True(tracker.MarkSampled(plan.Request, startedAt.AddSeconds(3)).Delay >= TimeSpan.Zero);
    }

    [Fact]
    public async Task StoppedOwnerRevokesPlanPublicationButRetainedLeaseCanSettleSkipped()
    {
        using var fixture = new HostManagerSamplingSubscriptionTestFixture();
        var tracker = CreateMetricTracker(fixture);
        var startedAt = DateTimeOffset.Parse("2026-08-29T02:00:00Z");
        tracker.Track(MetricSampleRequest.ForIds(["cpu.usage"]), startedAt);
        var plan = tracker.CreatePlan(startedAt);
        Assert.NotNull(plan.Request);
        Assert.True(plan.OwnerToken.IsActive);

        await fixture.Owner.StopAsync(CancellationToken.None);

        Assert.False(plan.OwnerToken.IsActive);
        Assert.False(plan.OwnerToken.TryPublish(static () => { }));
        var completion = tracker.MarkSkipped(
            plan.Request!,
            startedAt.AddMilliseconds(1));
        Assert.True(completion.Delay >= TimeSpan.Zero);
    }

    [Fact]
    public void CreatePlan_ColdDatasetIsImmediatelyDue()
    {
        using var fixture = new HostManagerSamplingSubscriptionTestFixture();
        var tracker = CreateMetricTracker(fixture);
        var startedAt = DateTimeOffset.Parse("2026-07-09T00:00:00Z");
        tracker.Track(MetricSampleRequest.ForIds(["cpu.usage"]), startedAt);

        var plan = tracker.CreatePlan(startedAt);

        Assert.NotNull(plan.Request);
        Assert.Equal(["cpu.usage"], plan.Request!.Ids, StringComparer.OrdinalIgnoreCase);
        Assert.True(tracker.MarkSampled(plan.Request, startedAt).Delay >= TimeSpan.Zero);
    }

    [Fact]
    public void CreatePlan_AlignedColdDatasetsShareOnePhysicalBoundary()
    {
        using var fixture = new HostManagerSamplingSubscriptionTestFixture();
        var tracker = CreateMetricTracker(fixture);
        var startedAt = DateTimeOffset.Parse("2026-08-29T00:00:00Z");
        tracker.TrackPersistent(
            MetricSampleRequest.ForIds(
            [
                "process.cpu.usage",
                "process.memory.usage",
                SamplingDatasetIds.ProcessGpuUsage,
                SamplingDatasetIds.ProcessGpuVram,
                "process.runtime-state"
            ]),
            startedAt,
            TimeSpan.FromSeconds(5));

        var plan = tracker.CreatePlan(startedAt);

        Assert.Equal(5, plan.Schedule.DueItemCount);
        Assert.NotNull(plan.Request);
        Assert.True(new HashSet<string>(plan.DueItemIds, StringComparer.OrdinalIgnoreCase)
            .SetEquals(plan.Request!.Ids));
        Assert.Equal(5, plan.DueItemIds.Count);
        tracker.MarkSampled(plan.Request, startedAt);
    }

    [Fact]
    public void OverlappingSourcesUseTheHighestFrequencyOnlyForTheirSharedDataset()
    {
        using var fixture = new HostManagerSamplingSubscriptionTestFixture();
        var tracker = CreateMetricTracker(fixture);
        var startedAt = DateTimeOffset.Parse("2026-08-29T00:30:00Z");
        tracker.TrackPersistent(
            "scheduler-cpu",
            MetricSampleRequest.ForIds(["cpu.usage"]),
            startedAt,
            TimeSpan.FromSeconds(5));
        tracker.TrackPersistent(
            "ui-cpu",
            MetricSampleRequest.ForIds(["cpu.usage"]),
            startedAt,
            TimeSpan.FromSeconds(1));
        tracker.TrackPersistent(
            "scheduler-memory",
            MetricSampleRequest.ForIds(["memory.usage"]),
            startedAt,
            TimeSpan.FromSeconds(5));

        var initialPlan = tracker.CreatePlan(startedAt);
        Assert.Equal(2, initialPlan.Schedule.DueItemCount);
        Assert.True(new HashSet<string>(initialPlan.DueItemIds, StringComparer.OrdinalIgnoreCase)
            .SetEquals(["cpu.usage", "memory.usage"]));
        tracker.MarkSampled(initialPlan.Request!, startedAt);

        // Read the native completion timestamp: the tracker advances a monotonic
        // command clock as well as accepting the supplied UTC time.
        var sampledAt = ReadNativeState(fixture).Items.Min(static item => item.LastSampledMilliseconds);
        var oneSecondAt = DateTimeOffset.FromUnixTimeMilliseconds(checked((long)sampledAt + 1_000));
        var oneSecondPlan = tracker.CreatePlan(oneSecondAt);

        Assert.Equal(["cpu.usage"], oneSecondPlan.DueItemIds);
        Assert.Equal(
            ["cpu.usage"],
            oneSecondPlan.Request!.Ids,
            StringComparer.OrdinalIgnoreCase);
        Assert.Equal(2, oneSecondPlan.Schedule.ActiveItemCount);
        tracker.MarkSampled(oneSecondPlan.Request, oneSecondAt);
    }

    [Fact]
    public void WorkspaceReplay_KeepsEveryColdDatasetOnTheSharedBoundary()
    {
        using var fixture = new HostManagerSamplingSubscriptionTestFixture();
        var tracker = CreateMetricTracker(fixture);
        var startedAt = DateTimeOffset.Parse("2026-08-29T01:00:00Z");
        tracker.TrackPersistent(
            MetricSampleRequest.ForIds(
            [
                "process.cpu.usage",
                "process.memory.usage",
                SamplingDatasetIds.ProcessGpuUsage,
                SamplingDatasetIds.ProcessGpuVram,
                "process.runtime-state"
            ]),
            startedAt,
            TimeSpan.FromSeconds(5));
        var replacement = HostManagerTestPlanFactory.CreatePlan(root =>
        {
            root["profile_revision"] = root["profile_revision"]!.GetValue<int>() + 1;
        });
        fixture.Provider.Publish(CompiledRuntimePlan.Default with
        {
            Version = 2,
            HostManager = replacement
        });

        var now = startedAt.AddSeconds(1);
        var replay = tracker.CreatePlan(now);
        Assert.True(replay.IsActive);
        Assert.Equal(5, replay.Schedule.DueItemCount);
        Assert.True(new HashSet<string>(replay.DueItemIds, StringComparer.OrdinalIgnoreCase)
            .SetEquals(replay.Request!.Ids));
        tracker.MarkSampled(replay.Request, now);
    }

    [Fact]
    public void FailedCompletionProjectsEffectiveIntervalInsteadOfMinimumInterval()
    {
        using var fixture = new HostManagerSamplingSubscriptionTestFixture();
        var tracker = CreateMetricTracker(fixture);
        var startedAt = DateTimeOffset.Parse("2026-07-09T00:00:00Z");
        var request = MetricSampleRequest.ForIds(["cpu.usage"]);
        tracker.Track(request, startedAt);
        var first = tracker.CreatePlan(startedAt);
        Assert.NotNull(first.Request);

        var completedAt = startedAt.AddSeconds(1);
        var failed = tracker.MarkFailed(first.Request!, completedAt);
        var nextWakeAt = Assert.IsType<DateTimeOffset>(
            failed.Schedule.NextWakeAt);
        Assert.Equal(
            TimeSpan.FromSeconds(1),
            nextWakeAt - failed.Schedule.CommandAt);

        var early = tracker.CreatePlan(nextWakeAt.AddMilliseconds(-1));
        Assert.Null(early.Request);
        Assert.True(early.Delay > TimeSpan.Zero);
    }

    [Fact]
    public void CoalescedCapture_RefreshesTheCompleteLatestSourceRequest()
    {
        using var fixture = new HostManagerSamplingSubscriptionTestFixture();
        var tracker = CreateMetricTracker(fixture, coalesceItemsIntoLatestCapture: true);
        var startedAt = DateTimeOffset.Parse("2026-07-09T00:00:00Z");
        tracker.Track(
            MetricSampleRequest.ForIds(["cpu.usage", "memory.usage", "gpu.0.usage"]),
            startedAt);

        var plan = tracker.CreatePlan(startedAt.AddSeconds(3));

        Assert.NotNull(plan.Request);
        Assert.Equal(
            ["cpu.usage", "gpu.0.usage", "memory.usage"],
            plan.Request!.Ids,
            StringComparer.OrdinalIgnoreCase);
        Assert.True(tracker.MarkSampled(plan.Request, startedAt.AddSeconds(3)).Delay >= TimeSpan.Zero);
    }

    [Fact]
    public void CoalescedCapture_ReplacesPendingSourceContentWithTheLatestRequest()
    {
        using var fixture = new HostManagerSamplingSubscriptionTestFixture();
        var tracker = new NativeItemSamplingSubscriptionTracker<MetricSampleRequest>(
            fixture.Owner,
            roleId: 1,
            static _ => "resource-panel",
            static request => request.Ids,
            static (ids, _) => MetricSampleRequest.ForIds(ids),
            coalesceItemsIntoLatestCapture: true);
        var startedAt = DateTimeOffset.Parse("2026-07-09T00:00:00Z");
        tracker.Track(MetricSampleRequest.ForIds(["cpu.usage"]), startedAt);
        tracker.Track(
            MetricSampleRequest.ForIds(["memory.usage", "gpu.0.usage"]),
            startedAt.AddMilliseconds(10));

        var plan = tracker.CreatePlan(startedAt.AddSeconds(3));

        Assert.NotNull(plan.Request);
        Assert.Equal(
            ["gpu.0.usage", "memory.usage"],
            plan.Request!.Ids,
            StringComparer.OrdinalIgnoreCase);
        Assert.True(tracker.MarkSampled(plan.Request, startedAt.AddSeconds(3)).Delay >= TimeSpan.Zero);
    }

    [Fact]
    public void CoalescedCapture_ReusesTheLatestMembershipUntilASourceChanges()
    {
        using var fixture = new HostManagerSamplingSubscriptionTestFixture();
        var capturedItemSets = new List<IReadOnlyList<string>>();
        var tracker = new NativeItemSamplingSubscriptionTracker<MetricSampleRequest>(
            fixture.Owner,
            roleId: 1,
            static _ => "resource-panel",
            static request => request.Ids,
            (ids, _) =>
            {
                capturedItemSets.Add(ids);
                return MetricSampleRequest.ForIds(ids);
            },
            coalesceItemsIntoLatestCapture: true);
        var startedAt = DateTimeOffset.Parse("2026-07-09T00:00:00Z");
        tracker.TrackPersistent(MetricSampleRequest.ForIds(["cpu.usage"]), startedAt);

        var first = tracker.CreatePlan(startedAt.AddMinutes(1));
        Assert.NotNull(first.Request);
        tracker.MarkSampled(first.Request, startedAt.AddMinutes(1));
        var second = tracker.CreatePlan(startedAt.AddMinutes(2));
        Assert.NotNull(second.Request);
        tracker.MarkSampled(second.Request, startedAt.AddMinutes(2));

        Assert.Same(capturedItemSets[0], capturedItemSets[1]);

        tracker.TrackPersistent(
            MetricSampleRequest.ForIds(["cpu.usage", "gpu.0.usage"]),
            startedAt.AddMinutes(2).AddMilliseconds(1));
        var updated = tracker.CreatePlan(startedAt.AddMinutes(3));

        Assert.NotNull(updated.Request);
        Assert.NotSame(capturedItemSets[1], capturedItemSets[2]);
        Assert.Equal(
            ["cpu.usage", "gpu.0.usage"],
            capturedItemSets[2],
            StringComparer.OrdinalIgnoreCase);
        tracker.MarkSampled(updated.Request, startedAt.AddMinutes(3));
    }

    [Fact]
    public void CreatePlan_IdlesAfterAllItemSubscriptionsExpire()
    {
        using var fixture = new HostManagerSamplingSubscriptionTestFixture();
        var tracker = CreateMetricTracker(fixture);
        var startedAt = DateTimeOffset.Parse("2026-07-09T00:00:00Z");
        var request = MetricSampleRequest.ForIds(["cpu.usage"]);
        tracker.Track(request, startedAt);

        Assert.Equal((1, 1), tracker.CaptureManagedSourceState());

        var expiredPlan = tracker.CreatePlan(startedAt + TimeSpan.FromSeconds(6));

        Assert.False(expiredPlan.IsActive);
        Assert.Null(expiredPlan.Request);
        Assert.Equal((0, 0), tracker.CaptureManagedSourceState());
    }

    [Fact]
    public void ContinuousRenewalKeepsOneSecondSamplingActiveBeyondSourceTtl()
    {
        using var fixture = new HostManagerSamplingSubscriptionTestFixture();
        var tracker = CreateMetricTracker(fixture);
        var startedAt = DateTimeOffset.Parse("2026-08-23T12:00:00Z");
        var request = MetricSampleRequest.ForIds(["cpu.usage"]);
        var sampledCount = 0;

        for (var second = 0; second <= 12; second++)
        {
            var now = startedAt.AddSeconds(second);
            tracker.Track(request, now);
            var plan = tracker.CreatePlan(now);

            Assert.True(plan.IsActive);
            Assert.Equal(1, plan.Schedule.ActiveSourceCount);
            Assert.Equal(1, plan.Schedule.ActiveItemCount);
            Assert.NotNull(plan.Schedule.NextWakeAt);
            if (plan.Request is null)
            {
                continue;
            }

            var completion = tracker.MarkSampled(plan.Request, now);
            Assert.True(completion.Schedule.IsActive);
            Assert.NotNull(completion.Schedule.NextWakeAt);
            Assert.Equal(
                completion.Schedule.NextWakeAt + completion.Schedule.FreshnessGrace,
                completion.Schedule.ReadyUntil);
            sampledCount++;
        }

        Assert.True(sampledCount >= 10);
    }

    [Fact]
    public void Track_IgnoresRequestsWithoutItems()
    {
        using var fixture = new HostManagerSamplingSubscriptionTestFixture();
        var tracker = CreateMetricTracker(fixture);
        var now = DateTimeOffset.Parse("2026-07-09T00:00:00Z");

        tracker.Track(MetricSampleRequest.CatalogProbe, now);

        var plan = tracker.CreatePlan(now);
        Assert.False(plan.IsActive);
        Assert.Null(plan.Request);
    }

    [Fact]
    public void TrackPersistent_RemainsActiveUntilSourceIsRemoved()
    {
        using var fixture = new HostManagerSamplingSubscriptionTestFixture();
        var tracker = CreateMetricTracker(fixture);
        var startedAt = DateTimeOffset.Parse("2026-07-14T00:00:00Z");
        var request = MetricSampleRequest.ForIds(["cpu.usage"]);
        tracker.TrackPersistent(request, startedAt);

        Assert.Equal((1, 1), tracker.CaptureManagedSourceState());

        var retainedPlan = tracker.CreatePlan(startedAt + TimeSpan.FromMinutes(1));
        Assert.True(retainedPlan.IsActive);
        Assert.NotNull(retainedPlan.Request);
        tracker.MarkSampled(retainedPlan.Request!, startedAt + TimeSpan.FromMinutes(1));

        Assert.True(tracker.Remove(request.CacheKey, startedAt + TimeSpan.FromMinutes(1)));
        Assert.Equal((0, 0), tracker.CaptureManagedSourceState());
        var removedPlan = tracker.CreatePlan(startedAt + TimeSpan.FromMinutes(1));
        Assert.False(removedPlan.IsActive);
    }

    [Fact]
    public void TrackPersistent_RejectsSubMillisecondExplicitIntervals()
    {
        using var fixture = new HostManagerSamplingSubscriptionTestFixture();
        var tracker = CreateMetricTracker(fixture);
        var request = MetricSampleRequest.ForIds(["cpu.usage"]);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            tracker.TrackPersistent(
                request,
                DateTimeOffset.UtcNow,
                TimeSpan.FromTicks(TimeSpan.TicksPerMillisecond - 1)));
    }

    [Fact]
    public void TrackPersistent_PreservesRoundedExplicitIntervalAcrossWorkspaceReplay()
    {
        using var fixture = new HostManagerSamplingSubscriptionTestFixture();
        var observedIntervals = new List<TimeSpan>();
        var tracker = new NativeItemSamplingSubscriptionTracker<MetricSampleRequest>(
            fixture.Owner,
            roleId: 1,
            static request => request.CacheKey,
            static request => request.Ids,
            (ids, views) =>
            {
                observedIntervals.Add(Assert.Single(views).Interval);
                return MetricSampleRequest.ForIds(ids);
            },
            coalesceItemsIntoLatestCapture: true);
        var startedAt = DateTimeOffset.Parse("2026-07-14T00:00:00Z");
        var request = MetricSampleRequest.ForIds(["cpu.usage"]);
        var explicitInterval = TimeSpan.FromTicks(
            (1_000 * TimeSpan.TicksPerMillisecond)
            + (TimeSpan.TicksPerMillisecond / 2));
        tracker.TrackPersistent(request, startedAt, explicitInterval);
        var first = tracker.CreatePlan(startedAt);
        Assert.NotNull(first.Request);
        tracker.MarkSampled(first.Request!, startedAt);

        var replacement = HostManagerTestPlanFactory.CreatePlan(root =>
        {
            root["profile_revision"] = root["profile_revision"]!.GetValue<int>() + 1;
        });
        fixture.Provider.Publish(CompiledRuntimePlan.Default with
        {
            Version = 2,
            HostManager = replacement
        });

        var replayed = tracker.CreatePlan(startedAt.AddSeconds(1));
        Assert.NotNull(replayed.Request);
        Assert.Equal(
            [TimeSpan.FromMilliseconds(1_001), TimeSpan.FromMilliseconds(1_001)],
            observedIntervals);
        tracker.MarkSampled(replayed.Request!, startedAt.AddSeconds(1));
    }

    [Fact]
    public void UtcRollback_DoesNotFreezeTheNativeSamplingSession()
    {
        using var fixture = new HostManagerSamplingSubscriptionTestFixture();
        var tracker = CreateMetricTracker(fixture);
        var initial = DateTimeOffset.Parse("2026-07-24T12:00:00Z");
        var request = MetricSampleRequest.ForIds(["cpu.usage"]);
        tracker.TrackPersistent(request, initial);
        var first = tracker.CreatePlan(initial.AddMinutes(1));
        Assert.NotNull(first.Request);
        tracker.MarkSampled(first.Request!, initial.AddMinutes(1));

        var rolledBack = initial.AddMinutes(-10);
        tracker.TrackPersistent(request, rolledBack);
        var afterRollback = tracker.CreatePlan(rolledBack);

        Assert.True(afterRollback.IsActive);
        if (afterRollback.Request is not null)
        {
            tracker.MarkSampled(afterRollback.Request, rolledBack);
        }
    }

    [Fact]
    public void PublishedReplacementRetainsDemandAndSchedulesItsCurrentItem()
    {
        using var fixture = new HostManagerSamplingSubscriptionTestFixture();
        var tracker = CreateMetricTracker(fixture);
        var startedAt = DateTimeOffset.Parse("2026-07-14T00:00:00Z");
        var request = MetricSampleRequest.ForIds(["cpu.usage"]);
        tracker.Track(request, startedAt);
        var plan = tracker.CreatePlan(startedAt.AddSeconds(3));
        Assert.NotNull(plan.Request);

        var replacement = HostManagerTestPlanFactory.CreatePlan(root =>
        {
            root["profile_revision"] = root["profile_revision"]!.GetValue<int>() + 1;
        });
        fixture.Provider.Publish(CompiledRuntimePlan.Default with
        {
            Version = 2,
            HostManager = replacement
        });

        var completed = tracker.MarkSampled(
            plan.Request!,
            startedAt.AddSeconds(3));
        Assert.True(completed.Delay >= TimeSpan.Zero);
        var replacementPlan = tracker.CreatePlan(startedAt.AddSeconds(4));
        Assert.True(replacementPlan.IsActive);
        Assert.NotNull(replacementPlan.Request);
        Assert.Equal(["cpu.usage"], replacementPlan.Request!.Ids);
        Assert.NotEqual(
            completed.Schedule.WorkspaceIncarnation,
            replacementPlan.Schedule.WorkspaceIncarnation);
        Assert.NotEqual(
            completed.Schedule.ConfigurationGeneration,
            replacementPlan.Schedule.ConfigurationGeneration);
        Assert.Equal(
            TimeSpan.FromMilliseconds(
                replacement.SamplingSubscription.HotPublish.Roles[0]
                    .FreshnessGraceMilliseconds),
            replacementPlan.Schedule.FreshnessGrace);
        Assert.NotNull(replacementPlan.Schedule.NextWakeAt);
        tracker.MarkSkipped(replacementPlan.Request!, startedAt.AddSeconds(4));
    }

    [Fact]
    public void PublishedReplacementKeepsConcurrentItemsIndependentAfterCompletion()
    {
        using var fixture = new HostManagerSamplingSubscriptionTestFixture();
        var tracker = new NativeItemSamplingSubscriptionTracker<MetricSampleRequest>(
            fixture.Owner,
            roleId: 1,
            static _ => "resource-panel",
            static request => request.Ids,
            static (ids, _) => MetricSampleRequest.ForIds(ids),
            coalesceItemsIntoLatestCapture: true);
        var startedAt = DateTimeOffset.Parse("2026-07-14T00:00:00Z");
        tracker.Track(MetricSampleRequest.ForIds(["cpu.usage"]), startedAt);
        var inFlight = tracker.CreatePlan(startedAt.AddSeconds(3));
        Assert.NotNull(inFlight.Request);

        var replacement = HostManagerTestPlanFactory.CreatePlan(root =>
        {
            root["profile_revision"] = root["profile_revision"]!.GetValue<int>() + 1;
        });
        fixture.Provider.Publish(CompiledRuntimePlan.Default with
        {
            Version = 2,
            HostManager = replacement
        });

        tracker.Track(
            MetricSampleRequest.ForIds(["memory.usage", "gpu.0.usage"]),
            startedAt.AddSeconds(3).AddMilliseconds(10));
        tracker.MarkSampled(
            inFlight.Request!,
            startedAt.AddSeconds(3).AddMilliseconds(20));

        var replacementPlan = tracker.CreatePlan(startedAt.AddSeconds(6));
        Assert.True(replacementPlan.IsActive);
        Assert.NotNull(replacementPlan.Request);
        Assert.Equal(
            ["gpu.0.usage", "memory.usage"],
            replacementPlan.Request!.Ids);
        Assert.NotNull(replacementPlan.Schedule.NextWakeAt);
        Assert.Equal((1, 1), tracker.CaptureManagedSourceState());
        tracker.MarkSkipped(replacementPlan.Request!, startedAt.AddSeconds(6));
    }

    [Theory]
    [InlineData("remove")]
    [InlineData("expire")]
    [InlineData("replace")]
    public void HistoricalItemsReleaseNativeCapacityAndManagedMappings(string mode)
    {
        var configuration = HostManagerTestPlanFactory.CreatePlan(root =>
        {
            var role = root["host_recreate"]!["sampling_subscription"]!["roles"]![0]!;
            role["maximum_item_count"] = 2;
            role["maximum_due_item_count"] = 2;
        });
        using var fixture = new HostManagerSamplingSubscriptionTestFixture(configuration);
        var tracker = CreateMetricTracker(fixture);
        var now = DateTimeOffset.UtcNow;
        for (var index = 0; index < 12; index++)
        {
            var request = MetricSampleRequest.ForIds([$"recorded-{index}"]);
            if (mode == "expire") tracker.Track(request, now);
            else tracker.TrackPersistent("same-source", request, now, TimeSpan.FromSeconds(1));
            if (mode == "remove") Assert.True(tracker.Remove("same-source", now));
            if (mode == "expire")
            {
                var lastSeen = Assert.Single(ReadNativeState(fixture).Sources).LastSeenMilliseconds;
                now = DateTimeOffset.FromUnixTimeMilliseconds(checked((long)lastSeen +
                    configuration.SamplingSubscription.HotPublish.Roles[0].ActiveTtlMilliseconds + 1));
                Assert.False(tracker.CreatePlan(now).IsActive);
            }
            var state = typeof(ResourceManager.Subscription.NativeItemSamplingSubscriptionTracker<MetricSampleRequest,
                ResourceManager.App.Infrastructure.RuntimeSpecialization.SamplingOwnerToken>);
            var maps = Assert.IsType<Dictionary<ulong, string>>(state.GetField("itemIds",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(tracker));
            Assert.Equal(mode == "replace" ? 1 : 0, maps.Count);
        }
        tracker.Clear(now);
    }

    [Fact]
    public void RemovingInflightDemandRetainsMappingUntilSettlement()
    {
        using var fixture = new HostManagerSamplingSubscriptionTestFixture();
        var tracker = CreateMetricTracker(fixture);
        var now = DateTimeOffset.UtcNow;
        tracker.TrackPersistent("old", MetricSampleRequest.ForIds(["old-field"]), now, TimeSpan.FromSeconds(1));
        var pending = tracker.CreatePlan(now);
        Assert.True(tracker.Remove("old", now));
        tracker.TrackPersistent("new", MetricSampleRequest.ForIds(["new-field"]), now, TimeSpan.FromSeconds(1));
        tracker.MarkSkipped(pending.Request!, now);
        var next = tracker.CreatePlan(now);
        Assert.Equal(["new-field"], next.DueItemIds);
        tracker.MarkSampled(next.Request!, now);
        tracker.Clear(now);
    }

    private static (NativeSamplingSubscriptionSourceView[] Sources, NativeSamplingSubscriptionItemState[] Items)
        ReadNativeState(HostManagerSamplingSubscriptionTestFixture fixture)
        => fixture.Owner.Execute(1, session =>
        {
            var header = new NativeSamplingSubscriptionSnapshotHeader
            {
                AbiVersion = NativeSamplingSubscriptionAbi.Version,
                StructSize = checked((uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeSamplingSubscriptionSnapshotHeader>())
            };
            var sources = new NativeSamplingSubscriptionSourceView[session.Capacity.SnapshotSourceCapacity];
            var items = new NativeSamplingSubscriptionItemState[session.Capacity.SnapshotItemCapacity];
            Assert.Equal(NativeSamplingSubscriptionStatus.Ok, session.Snapshot(ref header, sources, items));
            return (sources[..checked((int)header.SourceOutputCount)], items[..checked((int)header.ItemOutputCount)]);
        });

    private static NativeItemSamplingSubscriptionTracker<MetricSampleRequest> CreateMetricTracker(
        HostManagerSamplingSubscriptionTestFixture fixture,
        bool coalesceItemsIntoLatestCapture = false)
    {
        return new NativeItemSamplingSubscriptionTracker<MetricSampleRequest>(
            fixture.Owner,
            roleId: 1,
            static request => request.CacheKey,
            static request => request.IsCatalogProbe || request.IsEmpty ? [] : request.Ids,
            static (ids, _) => MetricSampleRequest.ForIds(ids),
            coalesceItemsIntoLatestCapture);
    }
}
