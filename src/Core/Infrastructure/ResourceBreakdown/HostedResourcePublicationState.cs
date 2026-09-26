using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace ResourceManager.App.Infrastructure.ResourceBreakdown;

internal readonly record struct HostedResourcePublicationOwnerToken(
    ulong LifecycleGeneration)
{
    public bool IsValid => LifecycleGeneration != 0;
}

internal sealed record HostedResourcePublicationPreparation(
    HostedResourcePublicationState Owner,
    HostedResourcePublicationOwnerToken OwnerToken,
    SamplingOwnerToken WorkspaceOwnerToken,
    ResourceBreakdownSampleRequest Request,
    NativeItemSamplingSubscriptionScheduleReceipt Schedule,
    ResourceBreakdownSnapshotState Resource,
    SchedulingProcessFactSnapshotState Scheduling,
    bool CanSettleSampled,
    Exception? Failure);

internal sealed class HostedResourcePublicationState
{
    private readonly object gate = new();
    private ulong nextLifecycleGeneration;
    private HostedResourcePublicationOwnerToken activeOwnerToken;
    private bool ownerOpen;

    public HostedResourcePublicationState()
    {
        Resource = new ResourceBreakdownSnapshotState(gate);
        Scheduling = new SchedulingProcessFactSnapshotState(gate);
    }

    public ResourceBreakdownSnapshotState Resource { get; }

    public SchedulingProcessFactSnapshotState Scheduling { get; }

    public HostedResourcePublicationOwnerToken OpenOwner()
    {
        lock (gate)
        {
            if (ownerOpen)
            {
                throw new InvalidOperationException(
                    "The hosted resource publication owner is already open.");
            }

            nextLifecycleGeneration = checked(nextLifecycleGeneration + 1);
            activeOwnerToken = new HostedResourcePublicationOwnerToken(
                nextLifecycleGeneration);
            ownerOpen = true;
            return activeOwnerToken;
        }
    }

    public bool TryAcceptSchedule(
        HostedResourcePublicationOwnerToken ownerToken,
        SamplingOwnerToken workspaceOwnerToken,
        NativeItemSamplingSubscriptionScheduleReceipt schedule)
    {
        ArgumentNullException.ThrowIfNull(workspaceOwnerToken);
        ArgumentNullException.ThrowIfNull(schedule);
        if (!MatchesSchedule(workspaceOwnerToken, schedule))
        {
            return false;
        }

        var accepted = false;
        if (!workspaceOwnerToken.TryPublish(() =>
            {
                lock (gate)
                {
                    if (!IsCurrentOpenOwnerUnsafe(ownerToken)
                        || !Resource.AcceptSchedule(schedule))
                    {
                        return;
                    }

                    Scheduling.AcceptSchedule(schedule);
                    accepted = true;
                }
            }))
        {
            return false;
        }

        return accepted;
    }

    public bool TryPrepareSample(
        HostedResourcePublicationOwnerToken ownerToken,
        SamplingOwnerToken workspaceOwnerToken,
        ResourceBreakdownSampleRequest request,
        ResourceBreakdownSnapshot resourceSample,
        SchedulingProcessFactSnapshot? schedulingSample,
        DateTimeOffset attemptedAt,
        NativeItemSamplingSubscriptionScheduleReceipt schedule,
        out HostedResourcePublicationPreparation? preparation)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(resourceSample);
        ArgumentNullException.ThrowIfNull(workspaceOwnerToken);
        lock (gate)
        {
            if (!IsCurrentOpenOwnerUnsafe(ownerToken)
                || !workspaceOwnerToken.IsActive
                || !MatchesSchedule(workspaceOwnerToken, schedule))
            {
                preparation = null;
                return false;
            }

            preparation = PrepareSampleUnsafe(
                ownerToken,
                workspaceOwnerToken,
                request,
                resourceSample,
                schedulingSample,
                attemptedAt,
                schedule);
            return true;
        }
    }

    public bool TryPrepareFailure(
        HostedResourcePublicationOwnerToken ownerToken,
        SamplingOwnerToken workspaceOwnerToken,
        ResourceBreakdownSampleRequest request,
        DateTimeOffset attemptedAt,
        NativeItemSamplingSubscriptionScheduleReceipt schedule,
        Exception error,
        out HostedResourcePublicationPreparation? preparation)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(workspaceOwnerToken);
        lock (gate)
        {
            if (!IsCurrentOpenOwnerUnsafe(ownerToken)
                || !workspaceOwnerToken.IsActive
                || !MatchesSchedule(workspaceOwnerToken, schedule))
            {
                preparation = null;
                return false;
            }

            preparation = PrepareFailureUnsafe(
                ownerToken,
                workspaceOwnerToken,
                request,
                attemptedAt,
                schedule,
                error);
            return true;
        }
    }

    public bool TryCommit(HostedResourcePublicationPreparation preparation)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        if (!ReferenceEquals(preparation.Owner, this))
        {
            throw new InvalidOperationException(
                "The hosted resource preparation belongs to another publication state.");
        }

        var committed = false;
        if (!preparation.WorkspaceOwnerToken.TryPublish(() =>
        {
            lock (gate)
            {
                if (!IsCurrentOpenOwnerUnsafe(preparation.OwnerToken))
                {
                    return;
                }

                Resource.CommitPrepared(preparation.Resource);
                Scheduling.CommitPrepared(preparation.Scheduling);
                committed = true;
            }
        }))
        {
            return false;
        }
        return committed;
    }

    public bool TryMarkOwnerFault(
        HostedResourcePublicationOwnerToken ownerToken,
        DateTimeOffset failedAt,
        Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        lock (gate)
        {
            if (!IsCurrentOpenOwnerUnsafe(ownerToken))
            {
                return false;
            }

            return true;
        }
    }

    public bool TryCloseOwner(
        HostedResourcePublicationOwnerToken ownerToken,
        DateTimeOffset closedAt,
        Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        lock (gate)
        {
            if (!IsCurrentOpenOwnerUnsafe(ownerToken))
            {
                return false;
            }

            ownerOpen = false;
            return true;
        }
    }

    private HostedResourcePublicationPreparation PrepareSampleUnsafe(
        HostedResourcePublicationOwnerToken ownerToken,
        SamplingOwnerToken workspaceOwnerToken,
        ResourceBreakdownSampleRequest request,
        ResourceBreakdownSnapshot resourceSample,
        SchedulingProcessFactSnapshot? schedulingSample,
        DateTimeOffset attemptedAt,
        NativeItemSamplingSubscriptionScheduleReceipt schedule)
    {
        try
        {
            var preparedResource = Resource.PrepareScheduledCopy(
                request,
                resourceSample,
                attemptedAt,
                schedule);
            var resourceCommitted = preparedResource.RequestedMetricDatasetsCommitted(
                request,
                resourceSample.CapturedAt);

            SchedulingProcessFactSnapshotState preparedScheduling;
            bool schedulingCommitted;
            var hasSchedulingPublications =
                request.SchedulingMetricMask != SchedulingProcessMetricMask.None
                || request.PublicationDatasetIds.Contains(
                    SamplingDatasetIds.ProcessInventory,
                    StringComparer.OrdinalIgnoreCase)
                || request.PublicationDatasetIds.Contains(
                    SamplingDatasetIds.ProcessAttribution,
                    StringComparer.OrdinalIgnoreCase);
            if (!hasSchedulingPublications)
            {
                preparedScheduling = Scheduling.PrepareFailureCopy(
                    request,
                    attemptedAt,
                    new InvalidOperationException(
                        "No scheduling datasets were requested."));
                schedulingCommitted = true;
            }
            else if (schedulingSample is null)
            {
                preparedScheduling = Scheduling.PrepareFailureCopy(
                    request,
                    attemptedAt,
                    new InvalidOperationException(
                        "The capture omitted its requested scheduling projection."));
                schedulingCommitted = false;
            }
            else
            {
                preparedScheduling = Scheduling.PrepareScheduledCopy(
                    request,
                    schedulingSample,
                    attemptedAt,
                    schedule);
                schedulingCommitted = preparedScheduling.RequestedDatasetsCommitted(
                    request,
                    schedulingSample);
            }

            return CreatePreparation(
                ownerToken,
                workspaceOwnerToken,
                request,
                schedule,
                preparedResource,
                preparedScheduling,
                resourceCommitted && schedulingCommitted,
                null);
        }
        catch (Exception error)
        {
            return PrepareFailureUnsafe(
                ownerToken,
                workspaceOwnerToken,
                request,
                attemptedAt,
                schedule,
                error);
        }
    }

    private HostedResourcePublicationPreparation PrepareFailureUnsafe(
        HostedResourcePublicationOwnerToken ownerToken,
        SamplingOwnerToken workspaceOwnerToken,
        ResourceBreakdownSampleRequest request,
        DateTimeOffset attemptedAt,
        NativeItemSamplingSubscriptionScheduleReceipt schedule,
        Exception error)
    {
        var preparedResource = Resource.PrepareFailureCopy(
            request,
            attemptedAt,
            error,
            schedule);
        var preparedScheduling = Scheduling.PrepareFailureCopy(
            request,
            attemptedAt,
            error);
        return CreatePreparation(
            ownerToken,
            workspaceOwnerToken,
            request,
            schedule,
            preparedResource,
            preparedScheduling,
            false,
            error);
    }

    private HostedResourcePublicationPreparation CreatePreparation(
        HostedResourcePublicationOwnerToken ownerToken,
        SamplingOwnerToken workspaceOwnerToken,
        ResourceBreakdownSampleRequest request,
        NativeItemSamplingSubscriptionScheduleReceipt schedule,
        ResourceBreakdownSnapshotState resource,
        SchedulingProcessFactSnapshotState scheduling,
        bool canSettleSampled,
        Exception? failure)
        => new(
            this,
            ownerToken,
            workspaceOwnerToken,
            request,
            schedule,
            resource,
            scheduling,
            canSettleSampled,
            failure);

    private bool IsCurrentOpenOwnerUnsafe(
        HostedResourcePublicationOwnerToken ownerToken)
        => ownerOpen
            && ownerToken.IsValid
            && ownerToken == activeOwnerToken;

    private static bool MatchesSchedule(
        SamplingOwnerToken ownerToken,
        NativeItemSamplingSubscriptionScheduleReceipt schedule)
        => ownerToken.WorkspaceIncarnation == schedule.WorkspaceIncarnation
            && ownerToken.ConfigurationGeneration ==
                schedule.ConfigurationGeneration;
}
