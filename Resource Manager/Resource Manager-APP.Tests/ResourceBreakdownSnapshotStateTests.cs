using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.ResourceBreakdown;

namespace Resource_Manager_APP.Tests;

public sealed class ResourceBreakdownSnapshotStateTests
{
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan FreshnessGrace = TimeSpan.FromSeconds(5);

    [Fact]
    public void ReadOrCreate_WithoutCompletedSampleIsWarmingAndEmpty()
    {
        var state = new ResourceBreakdownSnapshotState();

        var snapshot = state.ReadOrCreate(DateTimeOffset.UtcNow);

        Assert.Empty(snapshot.Bars);
        Assert.Equal(ResourceBreakdownSamplingStatuses.Warming, snapshot.Sampling.Status);
        Assert.Null(snapshot.Sampling.LastSuccessAt);
        Assert.Null(snapshot.Sampling.FailureCode);
    }

    [Fact]
    public void CompletedFailureWithoutPriorValuePublishesEmptyFailure()
    {
        var state = new ResourceBreakdownSnapshotState();
        var attemptedAt = DateTimeOffset.UtcNow;
        var request = Request("cpu.usage");

        var transition = state.MarkFailed(
            request,
            attemptedAt,
            new InvalidOperationException("private failure detail"));

        Assert.Equal(ResourceBreakdownFailureTransition.Entered, transition);
        var snapshot = state.ReadOrCreate(attemptedAt, request);
        Assert.Empty(snapshot.Bars);
        Assert.Equal(ResourceBreakdownSamplingStatuses.Failed, snapshot.Sampling.Status);
        Assert.Equal("resource-breakdown-dataset-sample-failed", snapshot.Sampling.FailureCode);
        Assert.Equal("资源数据集未能完成本轮采样。", snapshot.Sampling.FailureMessage);
        Assert.DoesNotContain("private failure detail", snapshot.Sampling.FailureMessage);
        Assert.Null(snapshot.Sampling.LastSuccessAt);
        Assert.Equal(1UL, Assert.Single(snapshot.Datasets).Generation);

        state.MarkFailed(
            request,
            attemptedAt.AddSeconds(1),
            new InvalidOperationException("private failure detail"));

        var repeated = state.ReadOrCreate(attemptedAt.AddSeconds(1), request);
        Assert.Equal(2UL, Assert.Single(repeated.Datasets).Generation);
    }

    [Fact]
    public void CompletedFailureAfterSuccessAtomicallyClearsOnlyRequestedDataset()
    {
        var state = new ResourceBreakdownSnapshotState();
        ApplyDirect(
            state,
            Request("cpu.usage", "memory.usage"),
            SnapshotAt(
                DateTimeOffset.Parse("2026-08-29T12:00:00Z"),
                Bar("cpu.usage", 10),
                Bar("memory.usage", 20)));

        var failedAt = DateTimeOffset.Parse("2026-08-29T12:00:01Z");
        state.MarkFailed(
            Request("cpu.usage"),
            failedAt,
            new InvalidOperationException("failure"));

        var all = Assert.IsType<ResourceBreakdownSnapshot>(state.Read(failedAt));
        var remaining = Assert.Single(all.Bars);
        Assert.Equal("memory.usage", remaining.MetricId);
        Assert.Equal(20, remaining.TotalValue);
        Assert.Equal(ResourceBreakdownSamplingStatuses.Ready, all.Sampling.Status);
        var cpu = Assert.Single(all.Datasets, dataset =>
            dataset.DatasetId == SamplingDatasetIds.ProcessCpuUsage);
        Assert.Equal(ResourceBreakdownSamplingStatuses.Failed, cpu.Status);
        Assert.Null(cpu.LastSuccessAt);
        var memory = Assert.Single(all.Datasets, dataset =>
            dataset.DatasetId == SamplingDatasetIds.ProcessMemoryUsage);
        Assert.Equal(ResourceBreakdownSamplingStatuses.Ready, memory.Status);
    }

    [Fact]
    public void FailureTransitionDistinguishesRepeatChangeAndRecovery()
    {
        var state = new ResourceBreakdownSnapshotState();
        var now = DateTimeOffset.UtcNow;
        var request = Request("cpu.usage");

        Assert.Equal(
            ResourceBreakdownFailureTransition.Entered,
            state.MarkFailed(request, now, new InvalidOperationException("same")));
        Assert.Equal(
            ResourceBreakdownFailureTransition.Repeated,
            state.MarkFailed(request, now.AddSeconds(1), new InvalidOperationException("same")));
        Assert.Equal(
            ResourceBreakdownFailureTransition.Changed,
            state.MarkFailed(request, now.AddSeconds(2), new ArgumentException("changed")));

        ApplyDirect(state, request, Snapshot(Bar("cpu.usage", 1)));

        Assert.Equal(
            ResourceBreakdownFailureTransition.Entered,
            state.MarkFailed(request, now.AddSeconds(3), new ArgumentException("changed")));
    }

    [Fact]
    public void Covers_UsesModeNeutralPayloadAndProjectsEachRequestedScale()
    {
        var state = new ResourceBreakdownSnapshotState();
        var request = new ResourceBreakdownSampleRequest(
            ["cpu.usage"],
            new Dictionary<string, string>
            {
                ["cpu.usage"] = ResourceBreakdownScaleModes.Capacity
            },
            ProcessSampleDetailLevel.ResourceTableBasic);
        ApplyDirect(state, request, Snapshot(Bar("cpu.usage", 10)));

        Assert.True(state.Covers(request));
        var activeRequest = request with
        {
            ScaleModes = new Dictionary<string, string>
            {
                ["cpu.usage"] = ResourceBreakdownScaleModes.Active
            }
        };
        Assert.True(state.Covers(activeRequest));
        var capacity = Assert.Single(state.ReadOrCreate(
            DateTimeOffset.UtcNow,
            request).Bars);
        var active = Assert.Single(state.ReadOrCreate(
            DateTimeOffset.UtcNow,
            activeRequest).Bars);
        Assert.Equal(ResourceBreakdownScaleModes.Capacity, capacity.ScaleMode);
        Assert.Equal(ResourceBreakdownScaleModes.Active, active.ScaleMode);
        Assert.Equal(capacity.TotalValue, active.TotalValue);
        Assert.Equal(capacity.CapacityValue, active.CapacityValue);
        Assert.False(state.Covers(request with
        {
            ProcessDetailLevel = ProcessSampleDetailLevel.ResourceTableFull
        }));
        Assert.False(state.Covers(request with
        {
            MetricIds = ["cpu.usage", "memory.usage"]
        }));
    }

    [Fact]
    public void MissingRequestedOutputPublishesEmptyFailure()
    {
        var state = new ResourceBreakdownSnapshotState();

        ApplyDirect(state, Request("cpu.usage"), Snapshot());

        var snapshot = state.ReadOrCreate(DateTimeOffset.UtcNow, Request("cpu.usage"));
        Assert.Empty(snapshot.Bars);
        Assert.Equal(ResourceBreakdownSamplingStatuses.Failed, snapshot.Sampling.Status);
        Assert.Null(snapshot.Sampling.LastSuccessAt);
    }

    [Fact]
    public void SuccessfulDatasetReplacementDoesNotTouchSiblingDataset()
    {
        var state = new ResourceBreakdownSnapshotState();
        ApplyDirect(
            state,
            Request("cpu.usage", "memory.usage"),
            Snapshot(Bar("cpu.usage", 10), Bar("memory.usage", 20)));

        ApplyDirect(
            state,
            Request("cpu.usage"),
            Snapshot(Bar("cpu.usage", 30)));

        var snapshot = Assert.IsType<ResourceBreakdownSnapshot>(
            state.Read(DateTimeOffset.UtcNow));
        Assert.Equal(2, snapshot.Bars.Count);
        Assert.Equal(30, snapshot.Bars.Single(bar =>
            bar.MetricId == "cpu.usage").TotalValue);
        Assert.Equal(20, snapshot.Bars.Single(bar =>
            bar.MetricId == "memory.usage").TotalValue);
    }

    [Fact]
    public void UnavailableObservationClearsItsDatasetInsteadOfRetainingPayload()
    {
        var state = new ResourceBreakdownSnapshotState();
        var request = Request("disk.io");
        ApplyDirect(state, request, Snapshot(Bar("disk.io", 25)));

        ApplyDirect(
            state,
            request,
            Snapshot(Bar(
                "disk.io",
                0,
                attributionStatus: SamplingObservationStatus.Unavailable)));

        var snapshot = state.ReadOrCreate(DateTimeOffset.UtcNow, request);
        Assert.Empty(snapshot.Bars);
        Assert.Equal(ResourceBreakdownSamplingStatuses.Failed, snapshot.Sampling.Status);
        Assert.Null(snapshot.Sampling.LastSuccessAt);
    }

    [Fact]
    public void TimePassingDoesNotExpireACompletedSuccess()
    {
        var state = new ResourceBreakdownSnapshotState();
        var capturedAt = DateTimeOffset.Parse("2026-08-23T06:03:52Z");
        ApplyDirect(
            state,
            Request("cpu.usage"),
            SnapshotAt(capturedAt, Bar("cpu.usage", 10)));

        var snapshot = Assert.IsType<ResourceBreakdownSnapshot>(
            state.Read(capturedAt.AddYears(10)));

        Assert.Equal(ResourceBreakdownSamplingStatuses.Ready, snapshot.Sampling.Status);
        Assert.Null(snapshot.Sampling.FailureCode);
        Assert.Equal(capturedAt, snapshot.Sampling.LastSuccessAt);
        Assert.Equal(10, Assert.Single(snapshot.Bars).TotalValue);
    }

    [Fact]
    public void OlderScheduleMetadataCannotRejectCurrentOwnerCompletion()
    {
        var state = new ResourceBreakdownSnapshotState();
        var now = DateTimeOffset.Parse("2026-08-23T12:00:00Z");
        var current = Schedule(8, 20, 5, now, now.AddSeconds(1));
        Assert.True(state.ApplyScheduled(
            Request("cpu.usage"),
            SnapshotAt(now, Bar("cpu.usage", 10)),
            now,
            current));

        var olderSchedule = Schedule(
            configurationGeneration: 8,
            stateRevision: 19,
            planEpoch: 4,
            commandAt: now.AddSeconds(1),
            nextWakeAt: now.AddMinutes(1));
        Assert.False(state.AcceptSchedule(olderSchedule));
        Assert.True(state.ApplyScheduled(
            Request("cpu.usage"),
            SnapshotAt(now.AddSeconds(1), Bar("cpu.usage", 99)),
            now.AddSeconds(1),
            olderSchedule));

        var snapshot = Assert.IsType<ResourceBreakdownSnapshot>(
            state.Read(now.AddYears(1)));
        Assert.Equal(ResourceBreakdownSamplingStatuses.Ready, snapshot.Sampling.Status);
        Assert.Equal(99, Assert.Single(snapshot.Bars).TotalValue);
    }

    [Fact]
    public void CompletionOrderWinsEvenWhenLaterCompletionHasOlderCaptureTime()
    {
        var state = new ResourceBreakdownSnapshotState();
        var now = DateTimeOffset.Parse("2026-08-23T12:00:00Z");
        ApplyDirect(
            state,
            Request("cpu.usage"),
            SnapshotAt(now.AddSeconds(2), Bar("cpu.usage", 20)));

        ApplyDirect(
            state,
            Request("cpu.usage"),
            SnapshotAt(now.AddSeconds(1), Bar("cpu.usage", 30)));

        var snapshot = Assert.IsType<ResourceBreakdownSnapshot>(state.Read(now));
        Assert.Equal(30, Assert.Single(snapshot.Bars).TotalValue);
        Assert.Equal(now.AddSeconds(1), snapshot.Sampling.LastSuccessAt);
    }

    [Fact]
    public void WorkspaceReplacementDoesNotMutateCompletedSlots()
    {
        var state = new ResourceBreakdownSnapshotState();
        var now = DateTimeOffset.Parse("2026-08-23T12:00:00Z");
        Assert.True(state.ApplyScheduled(
            Request("cpu.usage", "memory.usage"),
            SnapshotAt(
                now,
                Bar("cpu.usage", 10),
                Bar("memory.usage", 20)),
            now,
            Schedule(8, 20, 5, now, now.AddSeconds(1), workspaceIncarnation: 1)));

        Assert.True(state.AcceptSchedule(Schedule(
            9,
            1,
            1,
            now.AddSeconds(1),
            now.AddSeconds(2),
            workspaceIncarnation: 2)));

        var snapshot = Assert.IsType<ResourceBreakdownSnapshot>(
            state.Read(now.AddYears(1)));
        Assert.Equal(ResourceBreakdownSamplingStatuses.Ready, snapshot.Sampling.Status);
        Assert.Equal(2, snapshot.Bars.Count);
        Assert.All(snapshot.Bars, static bar => Assert.Equal(
            SamplingObservationStatus.Current,
            bar.ObservationStatus));
    }

    private static void ApplyDirect(
        ResourceBreakdownSnapshotState state,
        ResourceBreakdownSampleRequest request,
        ResourceBreakdownSnapshot sample)
    {
        state.ApplyDirect(
            request,
            sample,
            sample.CapturedAt,
            DefaultInterval,
            FreshnessGrace);
    }

    private static NativeItemSamplingSubscriptionScheduleReceipt Schedule(
        ulong configurationGeneration,
        ulong stateRevision,
        ulong planEpoch,
        DateTimeOffset commandAt,
        DateTimeOffset nextWakeAt,
        ulong workspaceIncarnation = 1)
        => new(
            NativeItemSamplingSubscriptionScheduleOrigin.Completion,
            workspaceIncarnation,
            configurationGeneration,
            stateRevision,
            planEpoch,
            commandAt,
            DefaultInterval,
            FreshnessGrace,
            nextWakeAt,
            null,
            ActiveSourceCount: 1,
            ActiveItemCount: 1,
            DueItemCount: 0,
            ExpiredSourceCount: 0);

    private static ResourceBreakdownSampleRequest Request(params string[] metricIds)
        => new(
            metricIds,
            new Dictionary<string, string>(),
            ProcessSampleDetailLevel.ResourceTableBasic);

    private static ResourceBreakdownSnapshot Snapshot(
        params ResourceBreakdownBar[] bars)
        => new(DateTimeOffset.UtcNow, bars);

    private static ResourceBreakdownSnapshot SnapshotAt(
        DateTimeOffset capturedAt,
        params ResourceBreakdownBar[] bars)
        => new(capturedAt, bars);

    private static ResourceBreakdownBar Bar(
        string metricId,
        double total,
        SamplingObservationStatus attributionStatus = SamplingObservationStatus.Current)
        => new(
            metricId,
            metricId,
            "%",
            ResourceBreakdownScaleModes.Capacity,
            total,
            100,
            total,
            $"{total}%",
            [],
            AttributionStatus: attributionStatus);
}
