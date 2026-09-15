using ResourceManager.App.Domain.ResourceTable;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.ResourceBreakdown;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace Resource_Manager_APP.Tests;

public sealed class HostedResourcePublicationStateTests
{
    [Fact]
    public void PrepareSample_IsInvisibleUntilTheSingleCommitPoint()
    {
        var state = new HostedResourcePublicationState();
        var owner = state.OpenOwner();
        var request = Request(SchedulingProcessMetricMask.None);
        var capturedAt = DateTimeOffset.UtcNow;
        var preparation = PrepareSample(
            state,
            owner,
            request,
            Snapshot(capturedAt, 25),
            null,
            capturedAt,
            Schedule(capturedAt, planEpoch: 1));

        Assert.True(preparation.CanSettleSampled);
        Assert.Empty(state.Resource.ReadOrCreate(capturedAt).Bars);

        Assert.True(state.TryCommit(preparation));

        var published = state.Resource.ReadOrCreate(capturedAt, request);
        Assert.Equal(25, Assert.Single(published.Bars).TotalValue);
        Assert.Equal(ResourceBreakdownSamplingStatuses.Ready, published.Sampling.Status);
    }

    [Fact]
    public void MissingSchedulingProjection_CommitsIndependentResourceValueAndClearsSchedulingValue()
    {
        var state = new HostedResourcePublicationState();
        var owner = state.OpenOwner();
        var request = Request(SchedulingProcessMetricMask.CpuUsage);
        var firstAt = DateTimeOffset.UtcNow;
        var first = PrepareSample(
            state,
            owner,
            request,
            Snapshot(firstAt, 10),
            SchedulingSnapshot(firstAt, generation: 1, cpuUsage: 10),
            firstAt,
            Schedule(firstAt, planEpoch: 1));
        Assert.True(first.CanSettleSampled);
        Assert.True(state.TryCommit(first));

        var secondAt = firstAt.AddSeconds(1);
        var failed = PrepareSample(
            state,
            owner,
            request,
            Snapshot(secondAt, 90),
            null,
            secondAt,
            Schedule(secondAt, planEpoch: 2));

        Assert.False(failed.CanSettleSampled);
        Assert.Null(failed.Failure);
        Assert.Equal(
            10,
            Assert.Single(state.Resource.ReadOrCreate(secondAt, request).Bars).TotalValue);

        Assert.True(state.TryCommit(failed));

        var resource = state.Resource.ReadOrCreate(secondAt, request);
        Assert.Equal(90, Assert.Single(resource.Bars).TotalValue);
        Assert.Equal(ResourceBreakdownSamplingStatuses.Ready, resource.Sampling.Status);
        Assert.Null(state.Scheduling.ReadLatest(
            new SchedulingProcessFactRequest(
                SchedulingProcessMetricMask.CpuUsage,
                null,
                new SchedulingGpuInventorySnapshot(
                    SamplingObservationStatus.Current,
                    1,
                    firstAt.UtcTicks,
                    0,
                    0,
                    0,
                    0,
                    [])),
            secondAt));
    }

    [Fact]
    public void PartialBatchCommitsHealthyDatasetAndClearsOnlyFailedDataset()
    {
        var state = new HostedResourcePublicationState();
        var owner = state.OpenOwner();
        var request = new ResourceBreakdownSampleRequest(
            [
                ResourceBreakdownMetricIds.CpuUsage,
                ResourceBreakdownMetricIds.MemoryUsage
            ],
            new Dictionary<string, string>(),
            ProcessSampleDetailLevel.SmartSchedulingLite);
        var firstAt = DateTimeOffset.UtcNow;
        var first = PrepareSample(
            state,
            owner,
            request,
            new ResourceBreakdownSnapshot(
                firstAt,
                [
                    Bar(ResourceBreakdownMetricIds.CpuUsage, 10),
                    Bar(ResourceBreakdownMetricIds.MemoryUsage, 20)
                ]),
            null,
            firstAt,
            Schedule(firstAt, planEpoch: 1));
        Assert.True(first.CanSettleSampled);
        Assert.True(state.TryCommit(first));

        var secondAt = firstAt.AddSeconds(1);
        var partial = PrepareSample(
            state,
            owner,
            request,
            new ResourceBreakdownSnapshot(
                secondAt,
                [
                    Bar(ResourceBreakdownMetricIds.CpuUsage, 90),
                    Bar(
                        ResourceBreakdownMetricIds.MemoryUsage,
                        99,
                        SamplingObservationStatus.Unavailable)
                ]),
            null,
            secondAt,
            Schedule(secondAt, planEpoch: 2));

        Assert.False(partial.CanSettleSampled);
        Assert.Null(partial.Failure);
        Assert.True(state.TryCommit(partial));

        var published = state.Resource.ReadOrCreate(secondAt, request);
        Assert.Equal(
            90,
            published.Bars.Single(static bar =>
                bar.MetricId == ResourceBreakdownMetricIds.CpuUsage).TotalValue);
        Assert.DoesNotContain(
            published.Bars,
            static bar => bar.MetricId == ResourceBreakdownMetricIds.MemoryUsage);
        Assert.Equal(ResourceBreakdownSamplingStatuses.Ready, published.Sampling.Status);
        Assert.Equal(
            ResourceBreakdownSamplingStatuses.Failed,
            published.Datasets.Single(static dataset =>
                dataset.DatasetId == SamplingDatasetIds.ProcessMemoryUsage).Status);
    }

    [Fact]
    public void ActualCaptureExceptionIsRetainedWithTheEmptyPublication()
    {
        var state = new HostedResourcePublicationState();
        var owner = state.OpenOwner();
        var request = Request(SchedulingProcessMetricMask.None);
        var attemptedAt = DateTimeOffset.UtcNow;
        var schedule = Schedule(attemptedAt, planEpoch: 1);
        var error = new InvalidDataException("malformed capture");

        Assert.True(state.TryPrepareFailure(owner, WorkspaceToken(schedule), request,
            attemptedAt, schedule, error, out var preparation));
        Assert.NotNull(preparation);
        Assert.False(preparation.CanSettleSampled);
        Assert.Same(error, preparation.Failure);
        Assert.True(state.TryCommit(preparation));
        Assert.Empty(state.Resource.ReadOrCreate(attemptedAt, request).Bars);
    }

    [Fact]
    public void CloseOwnerRejectsPreparedPayloadAndKeepsPriorCurrentValue()
    {
        var state = new HostedResourcePublicationState();
        var owner = state.OpenOwner();
        var request = Request(SchedulingProcessMetricMask.CpuUsage);
        var firstAt = DateTimeOffset.UtcNow;
        var first = PrepareSample(
            state,
            owner,
            request,
            Snapshot(firstAt, 10),
            SchedulingSnapshot(firstAt, generation: 1, cpuUsage: 10),
            firstAt,
            Schedule(firstAt, planEpoch: 1));
        Assert.True(state.TryCommit(first));

        var secondAt = firstAt.AddSeconds(1);
        var stalePreparation = PrepareSample(
            state,
            owner,
            request,
            Snapshot(secondAt, 90),
            SchedulingSnapshot(secondAt, generation: 2, cpuUsage: 90),
            secondAt,
            Schedule(secondAt, planEpoch: 2));

        Assert.True(state.TryCloseOwner(
            owner,
            secondAt,
            new OperationCanceledException("owner stopped")));
        Assert.False(state.TryCommit(stalePreparation));

        var published = state.Resource.ReadOrCreate(secondAt, request);
        Assert.Equal(10, Assert.Single(published.Bars).TotalValue);
        Assert.Equal(ResourceBreakdownSamplingStatuses.Ready, published.Sampling.Status);
        Assert.NotNull(state.Scheduling.ReadLatest(
            SchedulingRequest(firstAt),
            secondAt));
    }

    [Fact]
    public void RestartedOwnerRejectsPreviousGenerationAndPublishesOnlyNewPreparation()
    {
        var state = new HostedResourcePublicationState();
        var firstOwner = state.OpenOwner();
        var request = Request(SchedulingProcessMetricMask.None);
        var firstAt = DateTimeOffset.UtcNow;
        var stalePreparation = PrepareSample(
            state,
            firstOwner,
            request,
            Snapshot(firstAt, 10),
            null,
            firstAt,
            Schedule(firstAt, planEpoch: 1));
        Assert.True(state.TryCloseOwner(
            firstOwner,
            firstAt,
            new OperationCanceledException("owner stopped")));

        var secondOwner = state.OpenOwner();
        var secondAt = firstAt.AddSeconds(1);
        var replacement = PrepareSample(
            state,
            secondOwner,
            request,
            Snapshot(secondAt, 25),
            null,
            secondAt,
            Schedule(secondAt, planEpoch: 2));

        Assert.False(state.TryCommit(stalePreparation));
        Assert.True(state.TryCommit(replacement));
        Assert.Equal(
            25,
            Assert.Single(state.Resource.ReadOrCreate(secondAt, request).Bars)
                .TotalValue);
    }

    [Fact]
    public void InterveningScheduleRevisionDoesNotInvalidatePreparedCurrentValue()
    {
        var state = new HostedResourcePublicationState();
        var owner = state.OpenOwner();
        var request = Request(SchedulingProcessMetricMask.None);
        var capturedAt = DateTimeOffset.UtcNow;
        var preparation = PrepareSample(
            state,
            owner,
            request,
            Snapshot(capturedAt, 25),
            null,
            capturedAt,
            Schedule(capturedAt, planEpoch: 1));

        Assert.True(state.TryAcceptSchedule(
            owner,
            WorkspaceToken(Schedule(
                capturedAt.AddMilliseconds(1),
                planEpoch: 2)),
            Schedule(capturedAt.AddMilliseconds(1), planEpoch: 2)));
        Assert.True(state.TryCommit(preparation));
        Assert.Equal(
            25,
            Assert.Single(state.Resource.ReadOrCreate(capturedAt, request).Bars)
                .TotalValue);
    }

    [Fact]
    public void RestartKeepsEachDatasetUntilThatDatasetPublishesAgain()
    {
        var state = new HostedResourcePublicationState();
        var firstOwner = state.OpenOwner();
        var all = new ResourceBreakdownSampleRequest(
            [
                ResourceBreakdownMetricIds.CpuUsage,
                ResourceBreakdownMetricIds.MemoryUsage
            ],
            new Dictionary<string, string>(),
            ProcessSampleDetailLevel.SmartSchedulingLite);
        var firstAt = DateTimeOffset.UtcNow;
        var first = PrepareSample(
            state,
            firstOwner,
            all,
            new ResourceBreakdownSnapshot(
                firstAt,
                [
                    Bar(ResourceBreakdownMetricIds.CpuUsage, 10),
                    Bar(ResourceBreakdownMetricIds.MemoryUsage, 20)
                ]),
            null,
            firstAt,
            Schedule(firstAt, planEpoch: 1));
        Assert.True(state.TryCommit(first));
        Assert.True(state.TryCloseOwner(
            firstOwner,
            firstAt.AddMilliseconds(1),
            new OperationCanceledException("owner stopped")));

        var secondOwner = state.OpenOwner();
        var cpuOnly = Request(SchedulingProcessMetricMask.None);
        var secondAt = firstAt.AddSeconds(1);
        var cpu = PrepareSample(
            state,
            secondOwner,
            cpuOnly,
            Snapshot(secondAt, 90),
            null,
            secondAt,
            Schedule(secondAt, planEpoch: 2));
        Assert.True(state.TryCommit(cpu));

        var published = state.Resource.ReadOrCreate(secondAt, all);
        Assert.Equal(
            90,
            published.Bars.Single(static bar =>
                bar.MetricId == ResourceBreakdownMetricIds.CpuUsage).TotalValue);
        Assert.Equal(
            SamplingObservationStatus.Current,
            published.Bars.Single(static bar =>
                bar.MetricId == ResourceBreakdownMetricIds.CpuUsage)
                .ObservationStatus);
        Assert.Equal(
            20,
            published.Bars.Single(static bar =>
                bar.MetricId == ResourceBreakdownMetricIds.MemoryUsage).TotalValue);
        Assert.Equal(
            SamplingObservationStatus.Current,
            published.Bars.Single(static bar =>
                bar.MetricId == ResourceBreakdownMetricIds.MemoryUsage)
                .ObservationStatus);
        Assert.Equal(ResourceBreakdownSamplingStatuses.Ready, published.Sampling.Status);
    }

    [Fact]
    public void RevokedWorkspaceTokenRejectsCommitWithoutClearingCurrentValues()
    {
        var state = new HostedResourcePublicationState();
        var localOwner = state.OpenOwner();
        var request = Request(SchedulingProcessMetricMask.CpuUsage);
        var firstAt = DateTimeOffset.UtcNow;
        var firstSchedule = Schedule(firstAt, planEpoch: 1);
        var firstWorkspace = WorkspaceToken(firstSchedule);
        var first = PrepareSample(
            state,
            localOwner,
            request,
            Snapshot(firstAt, 10),
            SchedulingSnapshot(firstAt, generation: 1, cpuUsage: 10),
            firstAt,
            firstSchedule,
            firstWorkspace);
        Assert.True(state.TryCommit(first));

        var secondAt = firstAt.AddSeconds(1);
        var stalePreparation = PrepareSample(
            state,
            localOwner,
            request,
            Snapshot(secondAt, 90),
            SchedulingSnapshot(secondAt, generation: 2, cpuUsage: 90),
            secondAt,
            Schedule(secondAt, planEpoch: 2),
            firstWorkspace);
        firstWorkspace.Revoke();

        Assert.False(state.TryCommit(stalePreparation));
        var retained = state.Resource.ReadOrCreate(secondAt, request);
        Assert.Equal(10, Assert.Single(retained.Bars).TotalValue);
        Assert.Equal(ResourceBreakdownSamplingStatuses.Ready, retained.Sampling.Status);
        Assert.NotNull(state.Scheduling.ReadLatest(
            SchedulingRequest(firstAt),
            secondAt));

        var replacementSchedule = Schedule(
            secondAt.AddSeconds(1),
            planEpoch: 1,
            workspaceIncarnation: 2,
            configurationGeneration: 2);
        var replacementWorkspace = WorkspaceToken(replacementSchedule);
        Assert.True(state.TryAcceptSchedule(
            localOwner,
            replacementWorkspace,
            replacementSchedule));
    }

    private static HostedResourcePublicationPreparation PrepareSample(
        HostedResourcePublicationState state,
        HostedResourcePublicationOwnerToken owner,
        ResourceBreakdownSampleRequest request,
        ResourceBreakdownSnapshot resource,
        SchedulingProcessFactSnapshot? scheduling,
        DateTimeOffset attemptedAt,
        NativeItemSamplingSubscriptionScheduleReceipt schedule,
        SamplingOwnerToken? workspaceOwnerToken = null)
    {
        workspaceOwnerToken ??= WorkspaceToken(schedule);
        Assert.True(state.TryPrepareSample(
            owner,
            workspaceOwnerToken,
            request,
            resource,
            scheduling,
            attemptedAt,
            schedule,
            out var preparation));
        return Assert.IsType<HostedResourcePublicationPreparation>(preparation);
    }

    private static SamplingOwnerToken WorkspaceToken(
        NativeItemSamplingSubscriptionScheduleReceipt schedule)
        => new(
            schedule.WorkspaceIncarnation,
            schedule.ConfigurationGeneration);

    private static SchedulingProcessFactRequest SchedulingRequest(
        DateTimeOffset observedAt)
        => new(
            SchedulingProcessMetricMask.CpuUsage,
            null,
            new SchedulingGpuInventorySnapshot(
                SamplingObservationStatus.Current,
                1,
                observedAt.UtcTicks,
                0,
                0,
                0,
                0,
                []));

    private static ResourceBreakdownSampleRequest Request(
        SchedulingProcessMetricMask schedulingMask)
        => new(
            [ResourceBreakdownMetricIds.CpuUsage],
            new Dictionary<string, string>(),
            ProcessSampleDetailLevel.SmartSchedulingLite,
            schedulingMask);

    private static ResourceBreakdownSnapshot Snapshot(
        DateTimeOffset capturedAt,
        double value)
        => new(
            capturedAt,
            [
                new ResourceBreakdownBar(
                    ResourceBreakdownMetricIds.CpuUsage,
                    "CPU 占用率",
                    "%",
                    ResourceBreakdownScaleModes.Capacity,
                    value,
                    100,
                    value,
                    $"{value:0.0}%",
                    [])
            ]);

    private static ResourceBreakdownBar Bar(
        string metricId,
        double value,
        SamplingObservationStatus observationStatus =
            SamplingObservationStatus.Current)
        => new(
            metricId,
            metricId,
            "%",
            ResourceBreakdownScaleModes.Capacity,
            value,
            100,
            value,
            $"{value:0.0}%",
            [],
            observationStatus,
            observationStatus);

    private static SchedulingProcessFactSnapshot SchedulingSnapshot(
        DateTimeOffset observedAt,
        ulong generation,
        double cpuUsage)
        => new(
            SamplingObservationStatus.Current,
            generation,
            observedAt.UtcTicks,
            1,
            1,
            0,
            0,
            SchedulingProcessMetricMask.CpuUsage,
            SchedulingProcessMetricMask.CpuUsage,
            [
                new SchedulingProcessFact(
                    42,
                    100,
                    "worker.exe",
                    @"C:\Tools\worker.exe",
                    "software:test",
                    "Test",
                    "Other",
                    SoftwareDisplayKinds.General,
                    100,
                    SchedulingProcessMetricMask.CpuUsage,
                    cpuUsage,
                    0,
                    generation,
                    [])
            ])
        {
            DatasetObservations = new Dictionary<
                SchedulingProcessMetricMask,
                SchedulingProcessDatasetObservation>
            {
                [SchedulingProcessMetricMask.CpuUsage] =
                    SchedulingProcessDatasetObservation.CreateCurrent(
                    SchedulingProcessMetricMask.CpuUsage,
                    generation,
                    observedAt.UtcTicks,
                    generation,
                    observedAt.UtcTicks)
            }
        };

    private static NativeItemSamplingSubscriptionScheduleReceipt Schedule(
        DateTimeOffset now,
        ulong planEpoch,
        ulong workspaceIncarnation = 1,
        ulong configurationGeneration = 1)
        => new(
            NativeItemSamplingSubscriptionScheduleOrigin.Plan,
            workspaceIncarnation,
            configurationGeneration,
            planEpoch,
            planEpoch,
            now,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(1),
            now.AddSeconds(5),
            now,
            1,
            1,
            1,
            0);
}
