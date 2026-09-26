using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.Monitoring;
using System.Text.Json.Nodes;

namespace Resource_Manager_APP.Tests;

public sealed class ItemSamplingSubscriptionTrackerTests
{
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

        var oneSecondPlan = tracker.CreatePlan(startedAt.AddSeconds(1));

        Assert.Equal(["cpu.usage"], oneSecondPlan.DueItemIds);
        Assert.Equal(
            ["cpu.usage"],
            oneSecondPlan.Request!.Ids,
            StringComparer.OrdinalIgnoreCase);
        Assert.Equal(2, oneSecondPlan.Schedule.ActiveItemCount);
        tracker.MarkSampled(oneSecondPlan.Request, startedAt.AddSeconds(1));
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
    }

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
