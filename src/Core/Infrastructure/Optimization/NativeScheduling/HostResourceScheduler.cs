using ResourceManager.Adapter;
using ResourceManager.Adapter.NativeScheduling;
using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace ResourceManager.App.Infrastructure.Optimization.NativeScheduling;

internal sealed record HostResourceSchedulerPlanResult(
    CompiledHostManagerPlan HostPlan,
    ulong PlanEpoch,
    ulong StateGeneration,
    ulong PlanLease,
    CompiledHostManagerResourceSchedulerHotPublishPlan Dispatch,
    ResourceSchedulerPlanResult NativePlan);

public sealed class HostResourceScheduler : IDisposable
{
    private readonly IRuntimePlanProvider runtimePlanProvider;
    private readonly HostManagerDeploymentState deploymentState;
    private readonly object sync = new();
    private readonly Dictionary<ulong, SessionHolder> sessions = [];
    private readonly ulong hostSessionIncarnation;
    private SessionHolder? current;
    private CompiledHostManagerResourceSchedulerRecreatePlan? appliedRecreatePlan;
    private CompiledHostManagerResourceSchedulerHotPublishPlan? appliedHotPlan;
    private CompiledHostManagerPlan? appliedHostPlan;
    private ulong appliedPlanEpoch;
    private ulong stateGenerationSequence;

    public HostResourceScheduler(
        IRuntimePlanProvider runtimePlanProvider,
        HostManagerDeploymentState deploymentState,
        HostManagerRuntimeIdentity runtimeIdentity)
    {
        this.runtimePlanProvider = runtimePlanProvider;
        this.deploymentState = deploymentState;
        ArgumentNullException.ThrowIfNull(runtimeIdentity);
        hostSessionIncarnation = runtimeIdentity.InstanceId;
        stateGenerationSequence = runtimeIdentity.InstanceId;
    }

    internal CompiledHostManagerPlan CurrentHostPlan
        => runtimePlanProvider.Current.HostManager.RequirePublished();

    internal HostResourceSchedulerPlanResult Plan(
        CompiledHostManagerPlan expectedHostPlan,
        ulong requestId,
        ulong nowMonotonicTimestamp,
        AdapterResourceActionMask requestedActions,
        byte enabledTierMask,
        bool emitCandidates,
        IReadOnlyList<ResourceSchedulerTarget> targets,
        IReadOnlyList<ResourceSchedulerPrivateResource> resources,
        ResourceSchedulerFactBatch factBatch)
    {
        lock (sync)
        {
            var hostPlan = runtimePlanProvider.Current.HostManager.RequirePublished();
            if (!ReferenceEquals(hostPlan, expectedHostPlan))
            {
                throw new InvalidOperationException(
                    "The Host resource scheduler cycle plan publication was superseded.");
            }
            RequireReservationsRepresented(factBatch.JournalPending);
            if (current is null
                || !ReferenceEquals(appliedHostPlan, hostPlan)
                || appliedRecreatePlan != hostPlan.HostRecreate.ResourceScheduler
                || appliedHotPlan != hostPlan.HotPublish.ResourceScheduler)
            {
                return CreateReplacementAndPlan(
                    hostPlan,
                    requestId,
                    nowMonotonicTimestamp,
                    requestedActions,
                    enabledTierMask,
                    emitCandidates,
                    targets,
                    resources,
                    factBatch);
            }

            var dispatch = hostPlan.HotPublish.ResourceScheduler;
            var configuration = dispatch.Configuration
                ?? throw new InvalidOperationException("The Host resource scheduler configuration is unpublished.");
            var request = new ResourceSchedulerPlanRequest(
                requestId,
                configuration.Generation,
                nowMonotonicTimestamp,
                requestedActions,
                enabledTierMask,
                emitCandidates);
            var holder = RequireCurrent();
            var preparedPlanLease = holder.PrepareNextPlanLease();
            var nativePlan = holder.Session.Plan(
                request,
                targets,
                resources,
                factBatch);
            holder.CommitPreparedPlanLease(preparedPlanLease);
            return new HostResourceSchedulerPlanResult(
                hostPlan,
                appliedPlanEpoch,
                holder.StateGeneration,
                preparedPlanLease,
                dispatch,
                nativePlan);
        }
    }

    internal ResourceSchedulerReservationToken ReserveSelection(
        HostResourceSchedulerPlanResult plan,
        ResourceSchedulerSelection selection,
        ulong nowMonotonicTimestamp,
        ulong deadlineTimestamp,
        ulong pendingGeneration)
    {
        lock (sync)
        {
            if (!ReferenceEquals(
                    runtimePlanProvider.Current.HostManager,
                    plan.HostPlan))
            {
                throw new InvalidOperationException(
                    "The resource scheduler plan publication was superseded before reservation.");
            }
            var holder = RequireHolder(plan.StateGeneration);
            holder.RequireReservable(plan, selection);
            RequireGlobalReservationCapacity(plan.Dispatch, selection.TargetKey);
            var trackingSlot = holder.PrepareReservationSlot();
            var token = holder.Session.ReserveSelection(
                selection,
                nowMonotonicTimestamp,
                deadlineTimestamp,
                pendingGeneration,
                checked((uint)plan.Dispatch.MaximumInFlightActions),
                checked((uint)plan.Dispatch.MaximumInFlightActionsPerTarget));
            holder.CommitReservation(trackingSlot, token, selection, deadlineTimestamp);
            return token;
        }
    }

    internal ResourceSchedulerSelection CreateCandidateSelection(
        HostResourceSchedulerPlanResult plan,
        ResourceSchedulerCandidate candidate)
    {
        lock (sync)
        {
            var holder = RequireHolder(plan.StateGeneration);
            return holder.RequireReservableCandidate(plan, candidate);
        }
    }

    internal ResourceSchedulerReservationToken ReserveCandidate(
        HostResourceSchedulerPlanResult plan,
        ResourceSchedulerCandidate candidate,
        ResourceSchedulerSelection selection,
        ulong nowMonotonicTimestamp,
        ulong deadlineTimestamp,
        ulong pendingGeneration)
    {
        lock (sync)
        {
            if (!ReferenceEquals(
                    runtimePlanProvider.Current.HostManager,
                    plan.HostPlan))
            {
                throw new InvalidOperationException(
                    "The resource scheduler plan publication was superseded before candidate reservation.");
            }
            var holder = RequireHolder(plan.StateGeneration);
            var expected = holder.RequireReservableCandidate(plan, candidate);
            if (selection != expected)
            {
                throw new InvalidDataException(
                    "The ledger-capacity candidate selection changed before reservation.");
            }
            RequireGlobalReservationCapacity(plan.Dispatch, selection.TargetKey);
            var trackingSlot = holder.PrepareReservationSlot();
            var token = holder.Session.ReserveSelection(
                selection,
                nowMonotonicTimestamp,
                deadlineTimestamp,
                pendingGeneration,
                checked((uint)plan.Dispatch.MaximumInFlightActions),
                checked((uint)plan.Dispatch.MaximumInFlightActionsPerTarget));
            holder.CommitReservation(
                trackingSlot,
                token,
                selection,
                deadlineTimestamp);
            return token;
        }
    }

    internal void CompleteRecoveredReservationFeedback(
        HostManagerResourceTransactionPayload payload,
        ResourceSchedulerActionFeedback feedback)
    {
        if (!payload.IsValid
            || feedback.Authority != payload.Selection.Authority
            || feedback.RequestId != payload.Selection.RequestId)
        {
            throw new InvalidDataException(
                "The recovered resource reservation feedback is invalid.");
        }
        lock (sync)
        {
            if (!sessions.TryGetValue(
                    payload.Reservation.StateGeneration,
                    out var holder))
            {
                if (payload.HostSessionIncarnation !=
                    hostSessionIncarnation)
                {
                    return;
                }
                throw new InvalidOperationException(
                    "A same-session resource recovery lost its native reservation authority.");
            }
            holder.RequireRecoveredReservation(payload);
            if (holder.IsSettledPendingJournalAck(payload.Reservation))
            {
                return;
            }
            holder.RequireRecoveryFeedback(payload.Reservation);
            var result = holder.Session.ApplyFeedback(
                payload.Reservation,
                new ResourceSchedulerFeedbackRequest(
                    payload.Selection.RequestId,
                    PolicyResultCode: 1,
                    PolicyDangerFlags: 0,
                    PolicyAccepted: true,
                    ActionResultCount: 1),
                payload.Selection,
                payload.Capacity,
                feedback);
            if (result.ReservationDisposition !=
                ResourceSchedulerReservationDisposition.Completed)
            {
                throw new InvalidOperationException(
                    "The recovered resource feedback did not complete its exact native reservation.");
            }
            holder.MarkSettledPendingJournalAck(payload.Reservation);
        }
    }

    internal void CompleteRecoveredReservationAfterJournalAck(
        HostManagerResourceTransactionPayload payload)
    {
        if (!payload.IsValid)
        {
            throw new InvalidDataException(
                "The acknowledged recovered resource reservation payload is invalid.");
        }
        lock (sync)
        {
            if (sessions.TryGetValue(
                    payload.Reservation.StateGeneration,
                    out var holder))
            {
                holder.RequireRecoveredReservation(payload);
                if (!holder.IsSettledPendingJournalAck(
                        payload.Reservation))
                {
                    throw new InvalidOperationException(
                        "A journal-acknowledged resource action still has an active native reservation.");
                }
                holder.Remove(payload.Reservation);
                TryCleanupRetired(holder);
                return;
            }
            if (payload.HostSessionIncarnation == hostSessionIncarnation)
            {
                throw new InvalidOperationException(
                    "A same-session acknowledged resource recovery lost its settlement proof.");
            }
        }
    }

    internal void CancelRecoveredReservationNoEffect(
        HostManagerResourceTransactionPayload payload)
    {
        if (!payload.IsValid)
        {
            throw new InvalidDataException(
                "The recovered no-effect resource payload is invalid.");
        }
        lock (sync)
        {
            if (!sessions.TryGetValue(
                    payload.Reservation.StateGeneration,
                    out var holder))
            {
                if (payload.HostSessionIncarnation !=
                    hostSessionIncarnation)
                {
                    return;
                }
                throw new InvalidOperationException(
                    "A same-session no-effect recovery lost its native reservation authority.");
            }
            holder.RequireRecoveredReservation(payload);
            if (holder.IsSettledPendingJournalAck(payload.Reservation))
            {
                return;
            }
            holder.RequireRecoveryCancellation(payload.Reservation);
            holder.Session.CancelReservation(payload.Reservation);
            holder.MarkSettledPendingJournalAck(payload.Reservation);
        }
    }

    internal void BindReservationToJournal(
        ResourceSchedulerReservationToken token,
        HostManagerResourceJournalPrepareProof prepareProof)
    {
        ArgumentNullException.ThrowIfNull(prepareProof);
        lock (sync)
        {
            var holder = RequireHolder(token);
            holder.RequireJournalBinding(token, prepareProof);
            holder.Session.BindReservationToJournal(
                token,
                prepareProof.Payload.JournalTransactionIdLow,
                prepareProof.Payload.JournalTransactionIdHigh);
            holder.CommitJournalBinding(
                token,
                prepareProof.Payload.JournalTransactionIdLow,
                prepareProof.Payload.JournalTransactionIdHigh);
        }
    }

    internal ResourceSchedulerRevalidationResult RevalidateAndBeginReservation(
        ResourceSchedulerReservationToken token,
        ResourceSchedulerSelection selection,
        ResourceSchedulerRevalidationResource resource)
    {
        lock (sync)
        {
            var holder = RequireHolder(token);
            var result = holder.Session.RevalidateAndBeginReservation(
                token,
                selection,
                resource);
            if (result.Allowed)
            {
                holder.MarkActive(token);
            }
            return result;
        }
    }

    internal void CompleteReservation(ResourceSchedulerReservationToken token)
    {
        lock (sync)
        {
            var holder = RequireHolder(token);
            holder.Session.CompleteReservation(token);
            Release(holder, token);
        }
    }

    internal void CancelReservation(ResourceSchedulerReservationToken token)
    {
        lock (sync)
        {
            var holder = RequireHolder(token);
            holder.Session.CancelReservation(token);
            Release(holder, token);
        }
    }

    internal void CancelJournalBoundReservation(
        ResourceSchedulerReservationToken token)
    {
        lock (sync)
        {
            var holder = RequireHolder(token);
            holder.RequireJournalBound(token);
            holder.Session.CancelReservation(token);
            holder.MarkSettledPendingJournalAck(token);
        }
    }

    internal ResourceSchedulerFeedbackResult ApplyJournalFeedback(
        HostManagerResourceTransactionPayload payload,
        ResourceSchedulerFeedbackRequest request,
        ResourceSchedulerActionFeedback action)
    {
        if (!payload.IsValid
            || action.Authority != payload.Selection.Authority
            || action.RequestId != payload.Selection.RequestId)
        {
            throw new InvalidDataException(
                "The journal-bound resource feedback is invalid.");
        }
        lock (sync)
        {
            var holder = RequireHolder(payload.Reservation);
            holder.RequireRecoveredReservation(payload);
            holder.RequireRecoveryFeedback(payload.Reservation);
            var result = holder.Session.ApplyFeedback(
                payload.Reservation,
                request,
                payload.Selection,
                payload.Capacity,
                action);
            if (result.ReservationDisposition ==
                ResourceSchedulerReservationDisposition.Completed)
            {
                holder.MarkSettledPendingJournalAck(payload.Reservation);
            }
            return result;
        }
    }

    internal void AbandonReservation(ResourceSchedulerReservationToken token)
    {
        lock (sync)
        {
            var holder = RequireHolder(token);
            holder.Session.AbandonReservation(token);
            holder.MarkEffectUncertain(token);
        }
    }

    internal ResourceSchedulerFeedbackResult ApplyFeedback(
        ResourceSchedulerReservationToken token,
        ResourceSchedulerFeedbackRequest request,
        ResourceSchedulerSelection selection,
        ResourceSchedulerCapacity capacity,
        ResourceSchedulerActionFeedback action)
    {
        lock (sync)
        {
            var holder = RequireHolder(token);
            var result = holder.Session.ApplyFeedback(token, request, selection, capacity, action);
            if (result.ReservationDisposition ==
                ResourceSchedulerReservationDisposition.Completed)
            {
                Release(holder, token);
            }
            return result;
        }
    }

    private HostResourceSchedulerPlanResult CreateReplacementAndPlan(
        CompiledHostManagerPlan hostPlan,
        ulong requestId,
        ulong nowMonotonicTimestamp,
        AdapterResourceActionMask requestedActions,
        byte enabledTierMask,
        bool emitCandidates,
        IReadOnlyList<ResourceSchedulerTarget> targets,
        IReadOnlyList<ResourceSchedulerPrivateResource> resources,
        ResourceSchedulerFactBatch factBatch)
    {
        var nextRecreate = hostPlan.HostRecreate.ResourceScheduler;
        var nextHotPlan = hostPlan.HotPublish.ResourceScheduler;
        var configuration = nextHotPlan.Configuration
            ?? throw new InvalidOperationException("The Host Manager resource scheduler configuration is unpublished.");
        if (current is { HasReservations: true } active
            && !active.AllReservationsRepresented(factBatch.JournalPending))
        {
            throw new InvalidOperationException(
                "The Host resource scheduler cannot replace a session until every retained reservation is represented by the same indivisible journal-pending fact batch.");
        }

        var operation = current is null
            ? appliedHostPlan is null
                ? HostManagerDeploymentOperation.InitialCreate
                : HostManagerDeploymentOperation
                    .HostRecreateAndHotPublish
            : appliedRecreatePlan != nextRecreate
                ? HostManagerDeploymentOperation.HostRecreateAndHotPublish
                : HostManagerDeploymentOperation.HotPublish;
        var preparedStateGeneration = PrepareNextStateGeneration();
        var attempt = deploymentState.BeginAttempt(
            HostManagerModuleKind.ResourceScheduler,
            hostPlan,
            operation);
        SessionHolder? replacement = null;
        var replacementRegistered = false;
        var settled = false;
        try
        {
            var stateGeneration = preparedStateGeneration;
            var native = new NativeResourceSchedulerSession(
                nextRecreate.TargetCapacity,
                nextRecreate.PrivateResourceCapacity,
                nextRecreate.PendingCapacity,
                nextRecreate.JournalPendingCapacity,
                configuration,
                stateGeneration);
            replacement = new SessionHolder(
                native,
                stateGeneration,
                nextRecreate.PendingCapacity,
                hostPlan.PlanEpoch,
                nextHotPlan);
            if (native.ResidentBytes > nextRecreate.MaximumResidentBytes)
            {
                throw new InvalidOperationException(
                    "The Host resource scheduler resident set exceeds its explicit budget.");
            }
            var request = new ResourceSchedulerPlanRequest(
                requestId,
                configuration.Generation,
                nowMonotonicTimestamp,
                requestedActions,
                enabledTierMask,
                emitCandidates);
            var preparedPlanLease = replacement.PrepareNextPlanLease();
            var stagedPlan = replacement.Session.Plan(
                request,
                targets,
                resources,
                factBatch);
            replacement.CommitPreparedPlanLease(preparedPlanLease);
            var latest = runtimePlanProvider.Current.HostManager.RequirePublished();
            if (!ReferenceEquals(latest, hostPlan))
            {
                throw new InvalidOperationException(
                    "The Host resource scheduler replacement was superseded before commit.");
            }
            sessions.EnsureCapacity(checked(sessions.Count + 1));
            sessions.Add(stateGeneration, replacement);
            replacementRegistered = true;
            HostManagerDeploymentAttemptSettlementGuard.RequireApplied(
                deploymentState.CompleteAttemptSucceeded(attempt),
                HostManagerModuleKind.ResourceScheduler);
            settled = true;
            var previous = current;
            current = replacement;
            replacement = null;
            replacementRegistered = false;
            appliedRecreatePlan = nextRecreate;
            appliedHotPlan = nextHotPlan;
            appliedHostPlan = hostPlan;
            appliedPlanEpoch = hostPlan.PlanEpoch;
            stateGenerationSequence = stateGeneration;
            previous?.Retire();
            TryCleanupRetired(previous);
            return new HostResourceSchedulerPlanResult(
                hostPlan,
                appliedPlanEpoch,
                current.StateGeneration,
                preparedPlanLease,
                nextHotPlan,
                stagedPlan);
        }
        catch (Exception exception)
        {
            if (settled)
            {
                throw;
            }
            if (replacementRegistered && replacement is not null)
            {
                sessions.Remove(replacement.StateGeneration);
            }
            Exception? cleanupException = null;
            try
            {
                replacement?.Dispose();
            }
            catch (Exception cleanup)
            {
                cleanupException = cleanup;
            }
            Exception? settlementException = null;
            try
            {
                HostManagerDeploymentAttemptSettlementGuard.RequireFailed(
                    deploymentState.CompleteAttemptFailed(
                        attempt,
                        "resource-scheduler-replacement-failed",
                        ToNativeResult(exception)),
                    HostManagerModuleKind.ResourceScheduler);
            }
            catch (Exception settlement)
            {
                settlementException = settlement;
            }
            if (cleanupException is not null || settlementException is not null)
            {
                var failures = new List<Exception> { exception };
                if (cleanupException is not null)
                {
                    failures.Add(cleanupException);
                }
                if (settlementException is not null)
                {
                    failures.Add(settlementException);
                }
                throw new AggregateException(
                    "The resource scheduler replacement, cleanup, or deployment settlement failed.",
                    failures);
            }
            throw;
        }
    }

    private static HostManagerNativeResultSnapshot? ToNativeResult(Exception exception)
        => exception is NativeResourceSchedulerException native
            ? new HostManagerNativeResultSnapshot("resource-scheduler", native.ResultCode)
            : null;

    public void Dispose()
    {
        lock (sync)
        {
            foreach (var holder in sessions.Values)
            {
                holder.Dispose();
            }
            sessions.Clear();
            current = null;
            appliedRecreatePlan = null;
            appliedHotPlan = null;
            appliedHostPlan = null;
            appliedPlanEpoch = 0;
        }
    }

    private SessionHolder RequireCurrent()
        => current ?? throw new InvalidOperationException(
            "The Host resource scheduler session has not been created.");

    private SessionHolder RequireHolder(ResourceSchedulerReservationToken token)
        => RequireHolder(token.StateGeneration);

    private SessionHolder RequireHolder(ulong stateGeneration)
        => sessions.TryGetValue(stateGeneration, out var holder)
            ? holder
            : throw new InvalidOperationException(
                "The Host resource scheduler reservation belongs to a retired session.");

    private void Release(SessionHolder holder, ResourceSchedulerReservationToken token)
    {
        holder.Remove(token);
        TryCleanupRetired(holder);
    }

    private ulong PrepareNextStateGeneration()
    {
        if (stateGenerationSequence == ulong.MaxValue)
        {
            throw new InvalidOperationException("The Host resource scheduler state generation was exhausted.");
        }

        return stateGenerationSequence + 1;
    }

    private void TryCleanupRetired(SessionHolder? holder)
    {
        if (holder is not { IsDisposable: true })
        {
            return;
        }

        try
        {
            holder.Dispose();
            sessions.Remove(holder.StateGeneration);
        }
        catch
        {
            // Cleanup is outside the deployment commit barrier. A failed native
            // dispose remains registered for later owner disposal and cannot
            // invalidate an already-applied replacement.
        }
    }

    private void RequireReservationsRepresented(
        IReadOnlyList<ResourceSchedulerPendingAction> journalPending)
    {
        foreach (var holder in sessions.Values)
        {
            if (holder.HasReservations
                && !holder.AllReservationsRepresented(journalPending))
            {
                throw new InvalidOperationException(
                    "Every Host resource scheduler reservation must remain bijectively represented by the same indivisible journal-pending fact batch.");
            }
        }
    }

    private void RequireGlobalReservationCapacity(
        CompiledHostManagerResourceSchedulerHotPublishPlan dispatch,
        ulong targetKey)
    {
        var total = 0;
        var target = 0;
        foreach (var holder in sessions.Values)
        {
            total = checked(total + holder.ReservationCount);
            target = checked(target + holder.CountReservationsForTarget(targetKey));
        }
        if (total >= dispatch.MaximumInFlightActions
            || target >= dispatch.MaximumInFlightActionsPerTarget)
        {
            throw new InvalidOperationException(
                "The Host-wide resource scheduler in-flight limit is exhausted across active and retired sessions.");
        }
    }

    private sealed class SessionHolder(
        NativeResourceSchedulerSession session,
        ulong stateGeneration,
        int reservationCapacity,
        ulong planEpoch,
        CompiledHostManagerResourceSchedulerHotPublishPlan dispatch) : IDisposable
    {
        private readonly ReservationEntry[] reservations = new ReservationEntry[
            reservationCapacity > 0
                ? reservationCapacity
                : throw new ArgumentOutOfRangeException(nameof(reservationCapacity))];
        private bool retired;
        private int reservationCount;
        private ulong latestPlanLease;

        public NativeResourceSchedulerSession Session { get; } = session;

        public ulong StateGeneration { get; } = stateGeneration;

        public bool HasReservations => reservationCount != 0;

        public int ReservationCount => reservationCount;

        public bool IsDisposable => retired && reservationCount == 0;

        public ulong PrepareNextPlanLease()
        {
            if (latestPlanLease == ulong.MaxValue)
            {
                throw new InvalidOperationException(
                    "The Host resource scheduler plan lease generation was exhausted.");
            }
            return latestPlanLease + 1;
        }

        public void CommitPreparedPlanLease(ulong preparedPlanLease)
        {
            // The owner lock prevents another plan from interleaving between
            // PrepareNextPlanLease and this assignment. All validation happens
            // before the native call so this commit cannot strand native state.
            latestPlanLease = preparedPlanLease;
        }

        public void RequireReservable(
            HostResourceSchedulerPlanResult plan,
            ResourceSchedulerSelection selection)
        {
            if (retired
                || plan.PlanEpoch != planEpoch
                || plan.StateGeneration != StateGeneration
                || plan.PlanLease == 0
                || plan.PlanLease != latestPlanLease
                || plan.Dispatch != dispatch)
            {
                throw new InvalidOperationException(
                    "The resource scheduler plan does not belong to the current reservable session.");
            }
            var exactMatches = plan.NativePlan.Selections.Count(candidate => candidate == selection);
            if (exactMatches != 1
                || selection.RequestId != plan.NativePlan.Summary.RequestId)
            {
                throw new InvalidOperationException(
                    "The resource scheduler selection is not an exact member of the current native plan lease.");
            }
        }

        public ResourceSchedulerSelection RequireReservableCandidate(
            HostResourceSchedulerPlanResult plan,
            ResourceSchedulerCandidate candidate)
        {
            if (retired
                || plan.PlanEpoch != planEpoch
                || plan.StateGeneration != StateGeneration
                || plan.PlanLease == 0
                || plan.PlanLease != latestPlanLease
                || plan.Dispatch != dispatch)
            {
                throw new InvalidOperationException(
                    "The resource scheduler candidate plan does not belong to the current reservable session.");
            }
            var candidateIndex = -1;
            for (var index = 0; index < plan.NativePlan.Candidates.Count; index++)
            {
                if (plan.NativePlan.Candidates[index] != candidate)
                {
                    continue;
                }
                if (candidateIndex >= 0)
                {
                    throw new InvalidOperationException(
                        "The ledger-capacity candidate is duplicated in the current native plan lease.");
                }
                candidateIndex = index;
            }
            if (candidateIndex < 0
                || candidate.Authority.Action != AdapterResourceActionMask.Discard
                || candidate.TargetKey == 0
                || candidate.SizeBytes == 0)
            {
                throw new InvalidOperationException(
                    "The ledger-capacity candidate is not one exact discard member of the current native plan lease.");
            }
            return new ResourceSchedulerSelection(
                candidate.TargetKey,
                plan.NativePlan.Summary.RequestId,
                candidate.Authority,
                candidate.SizeBytes,
                candidate.EstimatedReleaseBytes,
                plan.NativePlan.Summary.ConfigurationGeneration,
                candidate.FinalImportance,
                checked((uint)candidateIndex),
                candidate.TargetInputIndex,
                candidate.Tier,
                candidate.ActivityScore);
        }

        public int PrepareReservationSlot()
        {
            for (var index = 0; index < reservations.Length; index++)
            {
                if (!reservations[index].Occupied)
                {
                    return index;
                }
            }
            throw new InvalidOperationException(
                "The explicit Host resource scheduler reservation tracking capacity was exhausted.");
        }

        public void CommitReservation(
            int slot,
            ResourceSchedulerReservationToken token,
            ResourceSchedulerSelection selection,
            ulong deadlineTimestamp)
        {
            reservations[slot] = new ReservationEntry(
                true,
                token,
                selection,
                deadlineTimestamp,
                0,
                0,
                ReservationLifecycle.Reserved);
            reservationCount++;
        }

        public void RequireJournalBinding(
            ResourceSchedulerReservationToken token,
            HostManagerResourceJournalPrepareProof prepareProof)
        {
            var index = Find(token);
            if ((reservations[index].JournalTransactionIdLow |
                reservations[index].JournalTransactionIdHigh) != 0)
            {
                throw new InvalidOperationException(
                    "The resource scheduler reservation already has a durable journal binding.");
            }
            if (!prepareProof.Matches(
                    token,
                    reservations[index].Selection))
            {
                throw new InvalidDataException(
                    "The resource scheduler reservation does not match its exact durable prepare proof.");
            }
        }

        public void CommitJournalBinding(
            ResourceSchedulerReservationToken token,
            ulong transactionIdLow,
            ulong transactionIdHigh)
        {
            var index = Find(token);
            reservations[index] = reservations[index] with
            {
                JournalTransactionIdLow = transactionIdLow,
                JournalTransactionIdHigh = transactionIdHigh
            };
        }

        public void Remove(ResourceSchedulerReservationToken token)
        {
            var index = Find(token);
            reservations[index] = default;
            reservationCount--;
        }

        public void RequireRecoveredReservation(
            HostManagerResourceTransactionPayload payload)
        {
            var index = Find(payload.Reservation);
            var reservation = reservations[index];
            if (reservation.Selection != payload.Selection
                || reservation.DeadlineTimestamp !=
                    payload.DeadlineTimestamp
                || reservation.JournalTransactionIdLow !=
                    payload.JournalTransactionIdLow
                || reservation.JournalTransactionIdHigh !=
                    payload.JournalTransactionIdHigh)
            {
                throw new InvalidDataException(
                    "The recovered resource payload does not match its exact native reservation authority.");
            }
        }

        public void RequireJournalBound(
            ResourceSchedulerReservationToken token)
        {
            var reservation = reservations[Find(token)];
            if ((reservation.JournalTransactionIdLow |
                reservation.JournalTransactionIdHigh) == 0)
            {
                throw new InvalidOperationException(
                    "A terminal resource reservation requires an exact durable journal binding.");
            }
        }

        public bool IsSettledPendingJournalAck(
            ResourceSchedulerReservationToken token)
            => reservations[Find(token)].Lifecycle ==
                ReservationLifecycle.SettledPendingJournalAck;

        public void MarkSettledPendingJournalAck(
            ResourceSchedulerReservationToken token)
        {
            var index = Find(token);
            if (reservations[index].Lifecycle is not (
                    ReservationLifecycle.Reserved
                    or ReservationLifecycle.Active
                    or ReservationLifecycle.EffectUncertain))
            {
                throw new InvalidOperationException(
                    "Only an unsettled resource reservation may await journal acknowledgement.");
            }
            RequireJournalBound(token);
            reservations[index] = reservations[index] with
            {
                Lifecycle = ReservationLifecycle.SettledPendingJournalAck
            };
        }

        public void RequireRecoveryFeedback(
            ResourceSchedulerReservationToken token)
        {
            var lifecycle = reservations[Find(token)].Lifecycle;
            if (lifecycle is not (
                    ReservationLifecycle.Active
                    or ReservationLifecycle.EffectUncertain))
            {
                throw new InvalidOperationException(
                    "Recovered terminal feedback requires an active or effect-uncertain native reservation.");
            }
        }

        public void RequireRecoveryCancellation(
            ResourceSchedulerReservationToken token)
        {
            if (reservations[Find(token)].Lifecycle !=
                ReservationLifecycle.Reserved)
            {
                throw new InvalidOperationException(
                    "Recovered no-effect cancellation requires an unstarted native reservation.");
            }
        }

        public void MarkActive(ResourceSchedulerReservationToken token)
        {
            var index = Find(token);
            if (reservations[index].Lifecycle !=
                ReservationLifecycle.Reserved)
            {
                throw new InvalidOperationException(
                    "Only a reserved resource action may become active.");
            }
            reservations[index] = reservations[index] with
            {
                Lifecycle = ReservationLifecycle.Active
            };
        }

        public void MarkEffectUncertain(
            ResourceSchedulerReservationToken token)
        {
            var index = Find(token);
            if (reservations[index].Lifecycle !=
                ReservationLifecycle.Active)
            {
                throw new InvalidOperationException(
                    "Only an active resource action may become effect-uncertain.");
            }
            reservations[index] = reservations[index] with
            {
                Lifecycle = ReservationLifecycle.EffectUncertain
            };
        }

        public int CountReservationsForTarget(ulong targetKey)
        {
            var count = 0;
            foreach (var reservation in reservations)
            {
                if (reservation is { Occupied: true, Selection.TargetKey: var owned }
                    && owned == targetKey)
                {
                    count++;
                }
            }
            return count;
        }

        public bool AllReservationsRepresented(
            IReadOnlyList<ResourceSchedulerPendingAction> journalPending)
        {
            if (reservationCount == 0)
            {
                return true;
            }

            var represented = 0;
            foreach (var reservation in reservations)
            {
                if (!reservation.Occupied)
                {
                    continue;
                }
                if ((reservation.JournalTransactionIdLow |
                    reservation.JournalTransactionIdHigh) == 0)
                {
                    return false;
                }
                var matches = 0;
                foreach (var pending in journalPending)
                {
                    if (pending.PendingGeneration == reservation.Token.PendingGeneration
                        && pending.TargetKey == reservation.Selection.TargetKey
                        && pending.Authority == reservation.Selection.Authority
                        && pending.SizeBytes == reservation.Selection.SizeBytes
                        && pending.DeadlineTimestamp == reservation.DeadlineTimestamp
                        && pending.Tier == reservation.Selection.Tier
                        && pending.JournalTransactionIdLow ==
                            reservation.JournalTransactionIdLow
                        && pending.JournalTransactionIdHigh ==
                            reservation.JournalTransactionIdHigh
                        && pending.State is ResourceSchedulerPendingState.JournalPending
                            or ResourceSchedulerPendingState.EffectUncertain)
                    {
                        matches++;
                    }
                }
                if (matches != 1)
                {
                    return false;
                }
                represented++;
            }
            return represented == reservationCount;
        }

        public void Retire() => retired = true;

        public void Dispose() => Session.Dispose();

        private int Find(ResourceSchedulerReservationToken token)
        {
            for (var index = 0; index < reservations.Length; index++)
            {
                if (reservations[index] is { Occupied: true, Token: var owned }
                    && owned == token)
                {
                    return index;
                }
            }
            throw new InvalidOperationException(
                "The resource scheduler reservation was not owned by this session.");
        }

        private readonly record struct ReservationEntry(
            bool Occupied,
            ResourceSchedulerReservationToken Token,
            ResourceSchedulerSelection Selection,
            ulong DeadlineTimestamp,
            ulong JournalTransactionIdLow,
            ulong JournalTransactionIdHigh,
            ReservationLifecycle Lifecycle);

        private enum ReservationLifecycle : byte
        {
            Reserved = 1,
            Active = 2,
            EffectUncertain = 3,
            SettledPendingJournalAck = 4
        }
    }
}
