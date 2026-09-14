using System.Reflection;
using System.Text.Json.Nodes;
using ResourceManager.Adapter;
using ResourceManager.Adapter.NativeScheduling;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization.NativeScheduling;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace Resource_Manager_APP.Tests;

public sealed class HostResourceSchedulerLifecycleTests
{
    private const ulong Mib = 1024 * 1024;
    private const ulong Gib = 1024 * Mib;
    private const ulong ProjectionEpoch = 41;

    [Fact]
    public void FailedFeedbackRetainsRoutingUntilExactCompletedFeedback()
    {
        using var fixture = CreateFixture();
        var plan = PlanPrivate(fixture.Scheduler, 1, 100, CreateFactBatch());
        var selection = Assert.Single(plan.NativePlan.Selections);
        var token = fixture.Scheduler.ReserveSelection(plan, selection, 100, 200, 31);
        BindPrepared(fixture.Scheduler, plan, token, selection);
        Assert.True(fixture.Scheduler.RevalidateAndBeginReservation(
            token,
            selection,
            CreatePrivateRevalidation(selection)).Allowed);

        var retained = fixture.Scheduler.ApplyFeedback(
            token,
            new ResourceSchedulerFeedbackRequest(selection.RequestId, 1, 0, true, 1),
            selection,
            CreateCapacity(),
            CreateFeedback(selection, AdapterResourceActionStatus.Failed));
        Assert.False(retained.Applied);
        Assert.Equal(
            ResourceSchedulerReservationDisposition.Retained,
            retained.ReservationDisposition);

        var completed = fixture.Scheduler.ApplyFeedback(
            token,
            new ResourceSchedulerFeedbackRequest(selection.RequestId, 1, 0, true, 1),
            selection,
            CreateCapacity(),
            CreateFeedback(selection, AdapterResourceActionStatus.Completed));
        Assert.True(completed.Applied);
        Assert.Equal(
            ResourceSchedulerReservationDisposition.Completed,
            completed.ReservationDisposition);
    }

    [Fact]
    public void PlanFromRetiredGenerationCannotReserveInReplacementSession()
    {
        using var fixture = CreateFixture();
        var first = PlanPrivate(
            fixture.Scheduler,
            1,
            100,
            CreateFactBatch(),
            targetKey: 13);
        var firstSelection = Assert.Single(first.NativePlan.Selections);

        Publish(fixture, CreateHostPlan(38, 2), 2);
        var second = PlanPrivate(fixture.Scheduler, 2, 101, CreateFactBatch());
        Assert.NotEqual(first.StateGeneration, second.StateGeneration);

        Assert.Throws<InvalidOperationException>(() =>
            fixture.Scheduler.ReserveSelection(first, firstSelection, 101, 201, 32));
    }

    [Fact]
    public void NewHostPublicationReplacesSessionEvenWhenSchedulerSubplanIsEqual()
    {
        using var fixture = CreateFixture();
        var first = PlanPrivate(
            fixture.Scheduler,
            1,
            100,
            CreateFactBatch(),
            targetKey: 13);
        var nextPublication = first.HostPlan with
        {
            PlanEpoch = checked(first.HostPlan.PlanEpoch + 1)
        };
        Publish(fixture, nextPublication, 2);

        var second = PlanPrivate(
            fixture.Scheduler,
            2,
            101,
            CreateFactBatch(),
            targetKey: 13);

        Assert.NotSame(first.HostPlan, second.HostPlan);
        Assert.Equal(nextPublication.PlanEpoch, second.PlanEpoch);
        Assert.NotEqual(first.StateGeneration, second.StateGeneration);
    }

    [Fact]
    public void RecoveredJournalAuthoritySettlesOnlyItsExactNativeReservation()
    {
        using var fixture = CreateFixture();
        var plan = PlanPrivate(
            fixture.Scheduler,
            1,
            100,
            CreateFactBatch(),
            targetKey: 13);
        var selection = Assert.Single(plan.NativePlan.Selections);
        var token = fixture.Scheduler.ReserveSelection(
            plan,
            selection,
            100,
            200,
            pendingGeneration: 0x7211);
        BindPrepared(fixture.Scheduler, plan, token, selection);
        Assert.Equal(plan.StateGeneration, token.StateGeneration);
        Assert.Equal(
            plan.Dispatch.Configuration?.Generation,
            selection.ConfigurationGeneration);
        Assert.Single(plan.NativePlan.Selections, candidate =>
            candidate == selection);
        Assert.True(
            HostManagerResourceTransactionProjection.IsStrictResourceAuthority(
                selection.Authority));
        Assert.True(
            HostManagerResourceTransactionProjection.TargetIdentityMatches(
                selection.Authority,
                selection.TargetKey));
        var payload = HostManagerResourceTransactionProjection.DecodePayload(
            HostManagerResourceTransactionProjection.EncodePayload(
                plan,
                token,
                selection,
                CreateCapacity(),
                token.PendingGeneration,
                deadlineTimestamp: 200,
                journalConfigurationGeneration:
                    plan.HostPlan.HotPublish.TransactionJournal
                        .ConfigurationGeneration,
                hostSessionIncarnation: 0x9001));
        var forgedSelection = selection with
        {
            Authority = selection.Authority with
            {
                ActionAttemptIdHigh = checked(
                    selection.Authority.ActionAttemptIdHigh + 1)
            }
        };
        var forged = payload with
        {
            JournalTransactionIdHigh =
                forgedSelection.Authority.ActionAttemptIdHigh,
            Selection = forgedSelection
        };

        Assert.Throws<InvalidDataException>(() =>
            fixture.Scheduler.CancelRecoveredReservationNoEffect(
                forged));
        fixture.Scheduler.CancelRecoveredReservationNoEffect(payload);
        fixture.Scheduler.CancelRecoveredReservationNoEffect(payload);
        var pendingAck = CreatePending(
            selection,
            token,
            selection.Authority.ActionAttemptIdLow,
            selection.Authority.ActionAttemptIdHigh,
            ResourceSchedulerPendingState.JournalPending,
            200);
        Assert.Empty(PlanPrivate(
            fixture.Scheduler,
            2,
            101,
            CreateFactBatch(pendingAck),
            targetKey: 13).NativePlan.Selections);
        fixture.Scheduler.CompleteRecoveredReservationAfterJournalAck(
            payload);

        var replacement = PlanPrivate(
            fixture.Scheduler,
            3,
            102,
            CreateFactBatch(),
            targetKey: 13);
        Assert.Equal(plan.StateGeneration, replacement.StateGeneration);
    }

    [Fact]
    public void FixedReservationTrackingRejectsBeforeNativeMutationAndRemainsReusable()
    {
        using var fixture = CreateFixture(pendingCapacity: 1);
        var plan = PlanPrivate(fixture.Scheduler, 1, 100, CreateFactBatch());
        var selection = Assert.Single(plan.NativePlan.Selections);
        var first = fixture.Scheduler.ReserveSelection(plan, selection, 100, 200, 41);

        Assert.Throws<InvalidOperationException>(() =>
            fixture.Scheduler.ReserveSelection(plan, selection, 101, 201, 42));

        fixture.Scheduler.CancelReservation(first);
        var second = fixture.Scheduler.ReserveSelection(plan, selection, 102, 202, 43);
        fixture.Scheduler.CancelReservation(second);
    }

    [Fact]
    public void RetainedReservationMustRemainInEveryLaterIndivisibleJournalBatch()
    {
        using var fixture = CreateFixture();
        var first = PlanPrivate(fixture.Scheduler, 1, 100, CreateFactBatch());
        var selection = Assert.Single(first.NativePlan.Selections);
        var token = fixture.Scheduler.ReserveSelection(first, selection, 100, 200, 51);
        BindPrepared(fixture.Scheduler, first, token, selection);
        Assert.True(fixture.Scheduler.RevalidateAndBeginReservation(
            token,
            selection,
            CreatePrivateRevalidation(selection)).Allowed);
        Assert.False(fixture.Scheduler.ApplyFeedback(
            token,
            new ResourceSchedulerFeedbackRequest(selection.RequestId, 1, 0, true, 1),
            selection,
            CreateCapacity(),
            CreateFeedback(selection, AdapterResourceActionStatus.Failed)).Applied);

        var pending = new ResourceSchedulerPendingAction(
            selection.Authority,
            selection.Authority.ActionAttemptIdLow,
            selection.Authority.ActionAttemptIdHigh,
            selection.TargetKey,
            selection.SizeBytes,
            200,
            token.PendingGeneration,
            ResourceSchedulerPendingState.JournalPending,
            selection.Tier);
        var retainedBatch = CreateFactBatch(pending);
        Publish(fixture, CreateHostPlan(38, 2), 2);
        var second = PlanPrivate(fixture.Scheduler, 2, 101, retainedBatch);
        Assert.Empty(second.NativePlan.Selections);

        Assert.Throws<InvalidOperationException>(() =>
            PlanPrivate(fixture.Scheduler, 3, 102, CreateFactBatch()));

        Publish(fixture, CreateHostPlan(39, 3), 3);
        Assert.Throws<InvalidOperationException>(() =>
            PlanPrivate(fixture.Scheduler, 4, 103, CreateFactBatch()));

        var drifted = pending with
        {
            JournalTransactionIdLow =
                selection.Authority.ActionAttemptIdLow + 1
        };
        Assert.Throws<InvalidOperationException>(() =>
            PlanPrivate(fixture.Scheduler, 5, 104, CreateFactBatch(drifted)));
    }

    [Fact]
    public void TerminalNonAppliedFeedbackReleasesHostAndNativeReservation()
    {
        using var fixture = CreateFixture(pendingCapacity: 1);
        var plan = PlanPrivate(fixture.Scheduler, 1, 100, CreateFactBatch());
        var selection = Assert.Single(plan.NativePlan.Selections);
        var token = fixture.Scheduler.ReserveSelection(plan, selection, 100, 200, 61);
        BindPrepared(fixture.Scheduler, plan, token, selection);
        Assert.True(fixture.Scheduler.RevalidateAndBeginReservation(
            token,
            selection,
            CreatePrivateRevalidation(selection)).Allowed);

        var completed = fixture.Scheduler.ApplyFeedback(
            token,
            new ResourceSchedulerFeedbackRequest(selection.RequestId, 3, 0, false, 0),
            selection,
            CreateCapacity(),
            CreateFeedback(selection, AdapterResourceActionStatus.InvalidRequest));
        Assert.False(completed.Applied);
        Assert.Equal(
            ResourceSchedulerReservationDisposition.Completed,
            completed.ReservationDisposition);

        var reused = fixture.Scheduler.ReserveSelection(plan, selection, 101, 201, 62);
        fixture.Scheduler.CancelReservation(reused);
    }

    [Fact]
    public void ExpiredReservedSlotRequiresExplicitCancellationAndCannotDiverge()
    {
        using var fixture = CreateFixture(pendingCapacity: 1);
        var first = PlanPrivate(fixture.Scheduler, 1, 100, CreateFactBatch());
        var selection = Assert.Single(first.NativePlan.Selections);
        var token = fixture.Scheduler.ReserveSelection(first, selection, 100, 110, 71);
        BindPrepared(fixture.Scheduler, first, token, selection);
        var pending = CreatePending(
            selection,
            token,
            selection.Authority.ActionAttemptIdLow,
            selection.Authority.ActionAttemptIdHigh,
            ResourceSchedulerPendingState.JournalPending,
            110);

        var afterDeadline = PlanPrivate(
            fixture.Scheduler,
            2,
            999,
            CreateFactBatch(pending));
        Assert.Equal(1U, afterDeadline.NativePlan.Summary.ActivePendingCount);
        Assert.Empty(afterDeadline.NativePlan.Selections);

        fixture.Scheduler.CancelReservation(token);
        Assert.Single(PlanPrivate(
            fixture.Scheduler,
            3,
            1000,
            CreateFactBatch()).NativePlan.Selections);
    }

    [Fact]
    public void AbandonedEffectUncertainReservationNeedsExactRecoveryCompletion()
    {
        using var fixture = CreateFixture(pendingCapacity: 1);
        var first = PlanPrivate(
            fixture.Scheduler,
            1,
            100,
            CreateFactBatch(),
            targetKey: 13);
        var selection = Assert.Single(first.NativePlan.Selections);
        var token = fixture.Scheduler.ReserveSelection(first, selection, 100, 200, 81);
        BindPrepared(fixture.Scheduler, first, token, selection);
        Assert.True(fixture.Scheduler.RevalidateAndBeginReservation(
            token,
            selection,
            CreatePrivateRevalidation(selection)).Allowed);
        fixture.Scheduler.AbandonReservation(token);

        var uncertain = CreatePending(
            selection,
            token,
            selection.Authority.ActionAttemptIdLow,
            selection.Authority.ActionAttemptIdHigh,
            ResourceSchedulerPendingState.EffectUncertain,
            200);
        Assert.Empty(PlanPrivate(
            fixture.Scheduler,
            2,
            300,
            CreateFactBatch(uncertain)).NativePlan.Selections);

        var payload = new HostManagerResourceTransactionPayload(
            selection.Authority.ActionAttemptIdLow,
            selection.Authority.ActionAttemptIdHigh,
            token.PendingGeneration,
            token,
            selection,
            CreateCapacity(),
            DeadlineTimestamp: 200,
            first.PlanLease,
            first.PlanEpoch,
            first.HostPlan.HotPublish.TransactionJournal
                .ConfigurationGeneration,
            HostSessionIncarnation: 0x9001);
        var recoveredFeedback = new ResourceSchedulerActionFeedback(
            selection.Authority,
            selection.RequestId,
            ReleasedBytes: 0,
            ResidentBytes: selection.SizeBytes,
            ActionGateEpoch: 0,
            DetailCode: 0,
            AdapterResourceActionStatus.InvalidRequest,
            selection.Tier,
            selection.Tier);
        Assert.True(payload.IsValid);
        Assert.Equal(payload.Selection.Authority, recoveredFeedback.Authority);
        Assert.Equal(payload.Selection.RequestId, recoveredFeedback.RequestId);
        fixture.Scheduler.CompleteRecoveredReservationFeedback(
            payload,
            recoveredFeedback);
        fixture.Scheduler.CompleteRecoveredReservationFeedback(
            payload,
            recoveredFeedback);
        var pendingAck = CreatePending(
            selection,
            token,
            selection.Authority.ActionAttemptIdLow,
            selection.Authority.ActionAttemptIdHigh,
            ResourceSchedulerPendingState.JournalPending,
            200);
        Assert.Empty(PlanPrivate(
            fixture.Scheduler,
            3,
            301,
            CreateFactBatch(pendingAck)).NativePlan.Selections);
        fixture.Scheduler.CompleteRecoveredReservationAfterJournalAck(
            payload);
        Assert.Single(PlanPrivate(
            fixture.Scheduler,
            4,
            302,
            CreateFactBatch()).NativePlan.Selections);
    }

    [Fact]
    public void NewerPlanLeaseInvalidatesOlderSelectionInSameSession()
    {
        using var fixture = CreateFixture();
        var first = PlanPrivate(fixture.Scheduler, 1, 100, CreateFactBatch());
        var selection = Assert.Single(first.NativePlan.Selections);
        var durable = new ResourceSchedulerPendingAction(
            selection.Authority,
            0xF1,
            0xF2,
            selection.TargetKey,
            selection.SizeBytes,
            200,
            91,
            ResourceSchedulerPendingState.JournalPending,
            selection.Tier);
        var second = PlanPrivate(
            fixture.Scheduler,
            2,
            101,
            CreateFactBatch(durable));
        Assert.Empty(second.NativePlan.Selections);

        Assert.Throws<InvalidOperationException>(() =>
            fixture.Scheduler.ReserveSelection(first, selection, 101, 201, 92));
    }

    [Fact]
    public void ReplacementStagesFirstFactBatchBeforeDeploymentSettlement()
    {
        using var fixture = CreateFixture();
        var first = PlanPrivate(fixture.Scheduler, 1, 100, CreateFactBatch());
        var one = CreatePendingForAuthority(CreateAuthority(), 11, 0x101, 0x102, 101);
        var two = CreatePendingForAuthority(
            CreateAuthority(targetKey: 12, resourceKey: 102, resourceId: 2),
            12,
            0x103,
            0x104,
            102);

        Publish(
            fixture,
            CreateHostPlan(38, 2, journalPendingCapacity: 1),
            2);
        Assert.ThrowsAny<Exception>(() =>
            PlanPrivate(fixture.Scheduler, 2, 101, CreateFactBatch(one, two)));
        Assert.Equal(
            first.PlanEpoch,
            fixture.Deployment.Snapshot.ResourceScheduler.AppliedPlanEpoch);

        Publish(fixture, CreateHostPlan(39, 3), 3);
        var recovered = PlanPrivate(fixture.Scheduler, 3, 102, CreateFactBatch());
        Assert.Equal(first.StateGeneration + 1, recovered.StateGeneration);
    }

    [Fact]
    public void HostWideInFlightLimitIncludesRetiredGenerations()
    {
        using var fixture = CreateFixture(maximumInFlightActions: 1);
        var first = PlanPrivate(fixture.Scheduler, 1, 100, CreateFactBatch());
        var selection = Assert.Single(first.NativePlan.Selections);
        var token = fixture.Scheduler.ReserveSelection(first, selection, 100, 200, 111);
        BindPrepared(fixture.Scheduler, first, token, selection);
        Assert.True(fixture.Scheduler.RevalidateAndBeginReservation(
            token,
            selection,
            CreatePrivateRevalidation(selection)).Allowed);
        Assert.Equal(
            ResourceSchedulerReservationDisposition.Retained,
            fixture.Scheduler.ApplyFeedback(
                token,
                new ResourceSchedulerFeedbackRequest(selection.RequestId, 1, 0, true, 1),
                selection,
                CreateCapacity(),
                CreateFeedback(selection, AdapterResourceActionStatus.Failed))
                .ReservationDisposition);
        var pending = CreatePending(
            selection,
            token,
            selection.Authority.ActionAttemptIdLow,
            selection.Authority.ActionAttemptIdHigh,
            ResourceSchedulerPendingState.JournalPending,
            200);

        Publish(
            fixture,
            CreateHostPlan(38, 2, maximumInFlightActions: 1),
            2);
        var second = PlanPrivate(
            fixture.Scheduler,
            2,
            101,
            CreateFactBatch(pending),
            targetKey: 12,
            resourceKey: 102,
            resourceId: 2);
        var secondSelection = Assert.Single(second.NativePlan.Selections);
        Assert.Throws<InvalidOperationException>(() =>
            fixture.Scheduler.ReserveSelection(
                second,
                secondSelection,
                101,
                201,
                112));
    }

    [Fact]
    public void DeploymentSuccessIsExactCurrentPublicationCas()
    {
        var deployment = new HostManagerDeploymentState();
        var provider = new RuntimePlanProvider(deployment);
        var first = CreateHostPlan(37, 1);
        provider.Publish(CompiledRuntimePlan.Default with
        {
            Version = 1,
            CompiledAt = DateTimeOffset.UtcNow,
            Reason = "deployment-cas-first",
            HostManager = first
        });
        var attempt = deployment.BeginAttempt(
            HostManagerModuleKind.ResourceScheduler,
            first,
            HostManagerDeploymentOperation.InitialCreate);
        provider.Publish(CompiledRuntimePlan.Default with
        {
            Version = 2,
            CompiledAt = DateTimeOffset.UtcNow,
            Reason = "deployment-cas-second",
            HostManager = CreateHostPlan(38, 2)
        });

        Assert.Equal(
            HostManagerDeploymentAttemptSettlement.Stale,
            deployment.CompleteAttemptSucceeded(attempt));
        Assert.Equal(0UL, deployment.Snapshot.ResourceScheduler.AppliedPlanEpoch);
        Assert.Equal(
            HostManagerDeploymentAttemptSettlement.Stale,
            deployment.CompleteAttemptFailed(attempt, "superseded"));
        Assert.Null(deployment.Snapshot.ResourceScheduler.ActiveAttempt);
        Assert.Equal(
            2U,
            deployment.Snapshot.ResourceScheduler.StaleCompletionCount);
    }

    [Fact]
    public void StateGenerationExhaustionPreservesCurrentSessionAndDoesNotOpenAttempt()
    {
        using var fixture = CreateFixture();
        var first = PlanPrivate(fixture.Scheduler, 1, 100, CreateFactBatch());
        var field = typeof(HostResourceScheduler).GetField(
            "stateGenerationSequence",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("State generation field was not found.");
        field.SetValue(fixture.Scheduler, ulong.MaxValue);

        Publish(fixture, CreateHostPlan(38, 2), 2);
        Assert.Throws<InvalidOperationException>(() =>
            PlanPrivate(fixture.Scheduler, 2, 101, CreateFactBatch()));
        var blocked = fixture.Deployment.Snapshot.ResourceScheduler;
        Assert.Null(blocked.ActiveAttempt);
        Assert.Null(blocked.LastFailure);

        field.SetValue(fixture.Scheduler, first.StateGeneration);
        var recovered = PlanPrivate(fixture.Scheduler, 3, 102, CreateFactBatch());
        Assert.NotEqual(first.StateGeneration, recovered.StateGeneration);
    }

    [Fact]
    public void PlanLeaseLastValueSucceedsAndExhaustionRejectsBeforeNativePlanning()
    {
        using var fixture = CreateFixture();
        using var control = CreateFixture();
        _ = PlanPrivate(fixture.Scheduler, 1, 100, CreateFactBatch());
        _ = PlanPrivate(control.Scheduler, 1, 100, CreateFactBatch());

        SetCurrentPlanLease(fixture.Scheduler, ulong.MaxValue - 1);
        SetCurrentPlanLease(control.Scheduler, ulong.MaxValue - 1);
        var last = PlanPrivate(fixture.Scheduler, 2, 101, CreateFactBatch());
        var controlLast = PlanPrivate(control.Scheduler, 2, 101, CreateFactBatch());
        Assert.Equal(ulong.MaxValue, last.PlanLease);
        Assert.Equal(ulong.MaxValue, CurrentPlanLease(fixture.Scheduler));
        Assert.Equal(controlLast.NativePlan.Summary, last.NativePlan.Summary);
        Assert.Equal(controlLast.NativePlan.Selections, last.NativePlan.Selections);

        var exhausted = Assert.Throws<InvalidOperationException>(() =>
            PlanPrivate(fixture.Scheduler, 3, 102, CreateFactBatch()));
        Assert.Contains("plan lease generation was exhausted", exhausted.Message);
        Assert.Equal(ulong.MaxValue, CurrentPlanLease(fixture.Scheduler));

        SetCurrentPlanLease(fixture.Scheduler, ulong.MaxValue - 1);
        SetCurrentPlanLease(control.Scheduler, ulong.MaxValue - 1);
        var afterRejectedCall = PlanPrivate(
            fixture.Scheduler,
            3,
            102,
            CreateFactBatch());
        var withoutRejectedCall = PlanPrivate(
            control.Scheduler,
            3,
            102,
            CreateFactBatch());
        Assert.Equal(withoutRejectedCall.NativePlan.Summary, afterRejectedCall.NativePlan.Summary);
        Assert.Equal(withoutRejectedCall.NativePlan.GlobalSelection, afterRejectedCall.NativePlan.GlobalSelection);
        Assert.Equal(withoutRejectedCall.NativePlan.Candidates, afterRejectedCall.NativePlan.Candidates);
        Assert.Equal(withoutRejectedCall.NativePlan.Selections, afterRejectedCall.NativePlan.Selections);
        var expectedTargetPlan = Assert.Single(withoutRejectedCall.NativePlan.TargetPlans);
        var actualTargetPlan = Assert.Single(afterRejectedCall.NativePlan.TargetPlans);
        Assert.Equal(expectedTargetPlan.TargetKey, actualTargetPlan.TargetKey);
        Assert.Equal(expectedTargetPlan.OwnerApplicationKey, actualTargetPlan.OwnerApplicationKey);
        Assert.Equal(expectedTargetPlan.ReleaseGoalBytes, actualTargetPlan.ReleaseGoalBytes);
        Assert.Equal(expectedTargetPlan.PressureLevel, actualTargetPlan.PressureLevel);
        Assert.Equal(expectedTargetPlan.CandidateCount, actualTargetPlan.CandidateCount);
        Assert.Equal(expectedTargetPlan.SelectedActionCount, actualTargetPlan.SelectedActionCount);
        Assert.Equal(expectedTargetPlan.FirstSelectionIndex, actualTargetPlan.FirstSelectionIndex);
        Assert.Equal(expectedTargetPlan.ActionLimit, actualTargetPlan.ActionLimit);
        Assert.Equal(expectedTargetPlan.ActivePhaseOrder, actualTargetPlan.ActivePhaseOrder);
        Assert.Equal(expectedTargetPlan.ResultCode, actualTargetPlan.ResultCode);
        Assert.Equal(expectedTargetPlan.DangerFlags, actualTargetPlan.DangerFlags);
        Assert.Equal(expectedTargetPlan.TargetFlags, actualTargetPlan.TargetFlags);
    }

    private static ulong CurrentPlanLease(HostResourceScheduler scheduler)
        => (ulong)(CurrentPlanLeaseField(scheduler).GetValue(CurrentHolder(scheduler))
            ?? throw new InvalidOperationException("Current plan lease was not found."));

    private static void SetCurrentPlanLease(
        HostResourceScheduler scheduler,
        ulong value)
        => CurrentPlanLeaseField(scheduler).SetValue(CurrentHolder(scheduler), value);

    private static object CurrentHolder(HostResourceScheduler scheduler)
    {
        var field = typeof(HostResourceScheduler).GetField(
            "current",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Current session holder field was not found.");
        return field.GetValue(scheduler)
            ?? throw new InvalidOperationException("Current session holder was not created.");
    }

    private static FieldInfo CurrentPlanLeaseField(HostResourceScheduler scheduler)
        => CurrentHolder(scheduler).GetType().GetField(
            "latestPlanLease",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Current plan lease field was not found.");

    private static Fixture CreateFixture(
        int? pendingCapacity = null,
        int? maximumInFlightActions = null)
    {
        var deployment = new HostManagerDeploymentState();
        var provider = new RuntimePlanProvider(deployment);
        var plan = CreateHostPlan(
            37,
            1,
            pendingCapacity,
            maximumInFlightActions: maximumInFlightActions);
        provider.Publish(CompiledRuntimePlan.Default with
        {
            Version = 1,
            CompiledAt = DateTimeOffset.UtcNow,
            Reason = "host-resource-scheduler-lifecycle",
            HostManager = plan
        });
        return new Fixture(
            deployment,
            provider,
            new HostResourceScheduler(
                provider,
                deployment,
                new HostManagerRuntimeIdentity()));
    }

    private static void BindPrepared(
        HostResourceScheduler scheduler,
        HostResourceSchedulerPlanResult plan,
        ResourceSchedulerReservationToken token,
        ResourceSchedulerSelection selection)
    {
        var payload = new HostManagerResourceTransactionPayload(
            selection.Authority.ActionAttemptIdLow,
            selection.Authority.ActionAttemptIdHigh,
            token.PendingGeneration,
            token,
            selection,
            CreateCapacity(),
            200,
            plan.PlanLease,
            plan.PlanEpoch,
            plan.HostPlan.HotPublish.TransactionJournal
                .ConfigurationGeneration,
            0x9001);
        var constructor = typeof(HostManagerResourceJournalPrepareProof)
            .GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                [
                    typeof(HostManagerResourceTransactionPayload),
                    typeof(NativeTransactionJournalPayloadReference),
                    typeof(NativeTransactionJournalPayloadProvenance),
                    typeof(ulong),
                    typeof(ulong),
                    typeof(ulong)
                ],
                modifiers: null)
            ?? throw new InvalidOperationException(
                "The exact resource prepare-proof constructor was not found.");
        var proof =
            (HostManagerResourceJournalPrepareProof)constructor.Invoke(
                [
                    payload,
                    new NativeTransactionJournalPayloadReference(
                        1,
                        1,
                        HostManagerResourceTransactionProjection
                            .EncodedPayloadSize,
                        0xA001,
                        0xA002),
                    new NativeTransactionJournalPayloadProvenance(
                        0xB001,
                        0xB002),
                    0xC001UL,
                    0xC002UL,
                    1UL
                ]);
        scheduler.BindReservationToJournal(token, proof);
    }

    private static CompiledHostManagerPlan CreateHostPlan(
        int profileRevision,
        ulong planEpoch,
        int? pendingCapacity = null,
        int? journalPendingCapacity = null,
        int? maximumInFlightActions = null)
    {
        var plan = HostManagerTestPlanFactory.CreatePlan(root =>
        {
            root["profile_revision"] = profileRevision;
            if (pendingCapacity is not null)
            {
                var recreate = root["host_recreate"]?.AsObject()
                    ?? throw new InvalidOperationException("host_recreate was not found.");
                var scheduler = recreate["resource_scheduler"]?.AsObject()
                    ?? throw new InvalidOperationException("resource_scheduler was not found.");
                scheduler["pending_capacity"] = pendingCapacity.Value;
            }
            if (journalPendingCapacity is not null)
            {
                var recreate = root["host_recreate"]?.AsObject()
                    ?? throw new InvalidOperationException("host_recreate was not found.");
                var scheduler = recreate["resource_scheduler"]?.AsObject()
                    ?? throw new InvalidOperationException("resource_scheduler was not found.");
                scheduler["journal_pending_capacity"] = journalPendingCapacity.Value;
            }
            if (maximumInFlightActions is not null)
            {
                var hotPublish = root["hot_publish"]?.AsObject()
                    ?? throw new InvalidOperationException("hot_publish was not found.");
                var scheduler = hotPublish["resource_scheduler"]?.AsObject()
                    ?? throw new InvalidOperationException("resource_scheduler was not found.");
                var dispatch = scheduler["dispatch"]?.AsObject()
                    ?? throw new InvalidOperationException("resource scheduler dispatch was not found.");
                dispatch["maximum_in_flight_actions"] = maximumInFlightActions.Value;
            }
        });
        return plan with { PlanEpoch = planEpoch };
    }

    private static void Publish(
        Fixture fixture,
        CompiledHostManagerPlan hostPlan,
        long version)
    {
        fixture.Provider.Publish(CompiledRuntimePlan.Default with
        {
            Version = version,
            CompiledAt = DateTimeOffset.UtcNow,
            Reason = "host-resource-scheduler-replacement",
            HostManager = hostPlan
        });
    }

    private static HostResourceSchedulerPlanResult PlanPrivate(
        HostResourceScheduler scheduler,
        ulong requestId,
        ulong now,
        ResourceSchedulerFactBatch factBatch,
        ulong targetKey = 11,
        ulong resourceKey = 101,
        uint resourceId = 1)
        => scheduler.Plan(
            scheduler.CurrentHostPlan,
            requestId,
            now,
            AdapterResourceActionMask.Discard
                | AdapterResourceActionMask.Trim
                | AdapterResourceActionMask.MoveDown,
            1 << (byte)AdapterResourceTier.PhysicalMemory,
            true,
            [CreateTarget(targetKey)],
            [CreatePrivateResource(targetKey, resourceKey, resourceId)],
            factBatch);

    private static ResourceSchedulerFactBatch CreateFactBatch(
        params ResourceSchedulerPendingAction[] pending)
        => new(ProjectionEpoch, 8, pending);

    private static ResourceSchedulerTarget CreateTarget(ulong targetKey = 11)
        => new(
            targetKey,
            12,
            13,
            14,
            15,
            16,
            17,
            30,
            CreateCapacity(),
            0,
            1,
            -3,
            ResourceSchedulerSchedulingGrade.Optimize,
            ResourceSchedulerSchedulingGrade.Normal,
            AdapterSoftwareSurfaceState.PureBackground,
            false,
            true);

    private static ResourceSchedulerPrivateResource CreatePrivateResource(
        ulong targetKey = 11,
        ulong resourceKey = 101,
        uint resourceId = 1)
        => new(
            targetKey,
            512 * Mib,
            default,
            CreateAuthority(targetKey, resourceKey, resourceId),
            default,
            AdapterResourceTier.PhysicalMemory,
            AdapterResourceKind.Cache,
            AdapterResourceRecoveryKind.BuiltData,
            AdapterResourceGranularity.PartialUsable,
            AdapterResourceProtocol.AllActions & ~AdapterResourceActionMask.Trim,
            AdapterResourceActionRoute.ManagerDirect,
            AdapterResourceDemandMask.None,
            0);

    private static ResourceSchedulerExecutionAuthority CreateAuthority(
        ulong targetKey = 11,
        ulong resourceKey = 101,
        uint resourceId = 1)
        => new(
            ResourceSchedulerSource.AdaptedPrivate,
            AdapterResourceActionMask.Trim,
            AdapterResourceActionRoute.ManagerDirect,
            ResourceSchedulerExecutionAuthority.HostSelfExecutorProof,
            1,
            resourceId,
            20,
            19,
            21,
            resourceKey,
            12,
            13,
            14,
            15,
            16,
            18,
            17,
            22,
            23,
            0,
            131 + targetKey,
            132 + targetKey,
            ProjectionEpoch);

    private static ResourceSchedulerPendingAction CreatePending(
        ResourceSchedulerSelection selection,
        ResourceSchedulerReservationToken token,
        ulong transactionIdLow,
        ulong transactionIdHigh,
        ResourceSchedulerPendingState state,
        ulong deadline)
        => new(
            selection.Authority,
            transactionIdLow,
            transactionIdHigh,
            selection.TargetKey,
            selection.SizeBytes,
            deadline,
            token.PendingGeneration,
            state,
            selection.Tier);

    private static ResourceSchedulerPendingAction CreatePendingForAuthority(
        ResourceSchedulerExecutionAuthority authority,
        ulong targetKey,
        ulong transactionIdLow,
        ulong transactionIdHigh,
        ulong pendingGeneration)
        => new(
            authority,
            transactionIdLow,
            transactionIdHigh,
            targetKey,
            512 * Mib,
            200,
            pendingGeneration,
            ResourceSchedulerPendingState.JournalPending,
            AdapterResourceTier.PhysicalMemory);

    private static ResourceSchedulerRevalidationResource CreatePrivateRevalidation(
        ResourceSchedulerSelection selection)
        => new(
            selection.Authority,
            true,
            selection.TargetKey,
            selection.SizeBytes,
            -3,
            selection.Tier,
            AdapterResourceKind.Cache,
            AdapterResourceRecoveryKind.BuiltData,
            AdapterResourceGranularity.PartialUsable,
            AdapterResourceProtocol.AllActions & ~selection.Authority.Action,
            AdapterResourceDemandMask.None,
            0);

    private static ResourceSchedulerActionFeedback CreateFeedback(
        ResourceSchedulerSelection selection,
        AdapterResourceActionStatus status)
        => new(
            selection.Authority,
            selection.RequestId,
            selection.SizeBytes,
            0,
            0,
            0,
            status,
            selection.Tier,
            selection.Tier);

    private static ResourceSchedulerCapacity CreateCapacity()
        => new(
            8 * Gib,
            4 * Gib,
            16 * Gib,
            1 * Gib,
            32 * Gib,
            16 * Gib,
            0.50,
            0.0625,
            0.50,
            0b111);

    private sealed record Fixture(
        HostManagerDeploymentState Deployment,
        RuntimePlanProvider Provider,
        HostResourceScheduler Scheduler) : IDisposable
    {
        public void Dispose() => Scheduler.Dispose();
    }
}
