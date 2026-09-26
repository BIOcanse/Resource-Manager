using ResourceManager.Adapter;
using ResourceManager.Adapter.NativeScheduling;
namespace ResourceManager.Adapter.SharedMemory.Tests;

public sealed class NativeResourceSchedulerSessionTests
{
    private const ulong Mib = 1024 * 1024;
    private const ulong Gib = 1024 * Mib;
    private const ulong ProjectionEpoch = 41;
    private const ulong LayoutFingerprint = 0x4605066A76753B1D;

    [Fact]
    public void AbiV11_UsesPublishedFixedLayoutsOffsetsAndFingerprint()
    {
        Assert.Equal(11U, ResourceSchedulerProtocol.Version);
        Assert.Equal(LayoutFingerprint, NativeResourceSchedulerSession.PublishedLayoutFingerprint);
        NativeResourceSchedulerSession.ValidateConfiguration(CreateConfiguration(1));
    }

    [Fact]
    public void Plan_UsesZigConfigurationAndReturnsExactPrivateAuthority()
    {
        var configuration = CreateConfiguration(41);
        NativeResourceSchedulerSession.ValidateConfiguration(configuration);
        using var scheduler = CreateSession(configuration, targetCapacity: 4, resourceCapacity: 16);

        var result = PlanPrivate(scheduler, configuration, requestId: 77, now: 100);

        Assert.Equal(1U, result.Summary.TargetCount);
        Assert.Equal(1U, result.Summary.ResourceCount);
        var selection = Assert.Single(result.Selections);
        Assert.Equal(77UL, selection.RequestId);
        Assert.Equal(101UL, selection.Authority.ResourceKey);
        Assert.Equal(AdapterResourceActionMask.Discard, selection.Authority.Action);
        Assert.Equal(ResourceSchedulerSelectionKind.Action, result.GlobalSelection.Kind);
    }

    [Fact]
    public void Plan_RejectsRequestsBeyondEveryExplicitSessionCapacity()
    {
        var configuration = CreateConfiguration(9);
        using var scheduler = CreateSession(
            configuration,
            targetCapacity: 1,
            resourceCapacity: 1,
            pendingCapacity: 1,
            journalPendingCapacity: 0);

        Assert.Throws<InvalidOperationException>(() => scheduler.Plan(
            CreateRequest(configuration, 1, 1),
            new ResourceSchedulerTarget[2],
            [],
            new ResourceSchedulerFactBatch(ProjectionEpoch, 1, [])));
    }

    [Fact]
    public void FactBatch_CountsInputRowsWhileActivePendingUsesExactDeduplicatedAuthority()
    {
        var configuration = CreateConfiguration(17);
        using var scheduler = CreateSession(configuration, journalPendingCapacity: 2);
        var selection = Assert.Single(
            PlanPrivate(scheduler, configuration, requestId: 1, now: 100).Selections);
        _ = scheduler.ReserveSelection(selection, 100, 200, 31, 1, 1);
        var journal = new ResourceSchedulerPendingAction(
            selection.Authority,
            91,
            92,
            selection.TargetKey,
            selection.SizeBytes,
            300,
            32,
            ResourceSchedulerPendingState.JournalPending,
            selection.Tier);

        var batch = new ResourceSchedulerFactBatch(ProjectionEpoch, 1, [journal, journal]);
        Assert.Equal(2, batch.JournalPending.Count);
        var result = scheduler.Plan(
            CreateRequest(configuration, 2, 101),
            [CreateTarget(privateResourceCount: 1)],
            [CreatePrivateResource()],
            batch);

        Assert.Equal(1U, result.Summary.ActivePendingCount);
        Assert.Equal(0U, result.Summary.PendingDuplicateCount);
        Assert.Empty(result.Selections);
    }

    [Fact]
    public void Reservation_RemainsVisibleAfterDeadlineUntilExplicitCompletion()
    {
        var configuration = CreateConfiguration(19);
        using var scheduler = CreateSession(configuration);
        var first = PlanPrivate(scheduler, configuration, requestId: 1, now: 100);
        var selection = Assert.Single(first.Selections);
        var token = scheduler.ReserveSelection(selection, 100, 110, 31, 1, 1);
        scheduler.BindReservationToJournal(token, 401, 402);
        Assert.True(scheduler.RevalidateAndBeginReservation(
            token,
            selection,
            CreatePrivateRevalidation(selection)).Allowed);

        var blocked = PlanPrivate(scheduler, configuration, requestId: 2, now: 999);
        Assert.Equal(1U, blocked.Summary.ActivePendingCount);
        Assert.Empty(blocked.Selections);

        scheduler.CompleteReservation(token);
        Assert.Single(PlanPrivate(scheduler, configuration, requestId: 3, now: 1000).Selections);
    }

    [Fact]
    public void Feedback_ExactCompletedActionProjectsCapacityAndCompletesReservation()
    {
        var configuration = CreateConfiguration(21);
        using var scheduler = CreateSession(configuration);
        var capacity = CreateCapacity();
        var selection = Assert.Single(
            PlanPrivate(scheduler, configuration, requestId: 71, now: 100).Selections);
        var token = scheduler.ReserveSelection(selection, 100, 200, 72, 1, 1);
        scheduler.BindReservationToJournal(token, 403, 404);
        Assert.True(scheduler.RevalidateAndBeginReservation(
            token,
            selection,
            CreatePrivateRevalidation(selection)).Allowed);

        var feedback = scheduler.ApplyFeedback(
            token,
            new ResourceSchedulerFeedbackRequest(selection.RequestId, 1, 0, true, 1),
            selection,
            capacity,
            CreateFeedback(selection, AdapterResourceActionStatus.Completed));

        Assert.True(feedback.Applied);
        Assert.Equal(ResourceSchedulerReservationDisposition.Completed, feedback.ReservationDisposition);
        Assert.Equal(capacity.FreePhysicalBytes + selection.SizeBytes, feedback.ProjectedCapacity.FreePhysicalBytes);
        Assert.Equal(0U, PlanPrivate(scheduler, configuration, requestId: 73, now: 201).Summary.ActivePendingCount);
    }

    [Fact]
    public void Feedback_TerminalNonAppliedActionCompletesReservation()
    {
        var configuration = CreateConfiguration(22);
        using var scheduler = CreateSession(configuration);
        var selection = Assert.Single(
            PlanPrivate(scheduler, configuration, requestId: 74, now: 100).Selections);
        var token = scheduler.ReserveSelection(selection, 100, 200, 75, 1, 1);
        scheduler.BindReservationToJournal(token, 411, 412);
        Assert.True(scheduler.RevalidateAndBeginReservation(
            token,
            selection,
            CreatePrivateRevalidation(selection)).Allowed);

        var feedback = scheduler.ApplyFeedback(
            token,
            new ResourceSchedulerFeedbackRequest(selection.RequestId, 1, 0, true, 1),
            selection,
            CreateCapacity(),
            CreateFeedback(selection, AdapterResourceActionStatus.ResourceNotFound));

        Assert.False(feedback.Applied);
        Assert.Equal(ResourceSchedulerReservationDisposition.Completed, feedback.ReservationDisposition);
        Assert.Single(PlanPrivate(scheduler, configuration, requestId: 76, now: 201).Selections);
    }

    [Fact]
    public void Feedback_IdentityMismatchOnFailedOrRejectedStatusRetainsExactReservation()
    {
        var configuration = CreateConfiguration(23);
        using var scheduler = CreateSession(configuration);
        var selection = Assert.Single(
            PlanPrivate(scheduler, configuration, requestId: 81, now: 100).Selections);
        var token = scheduler.ReserveSelection(selection, 100, 200, 82, 1, 1);
        scheduler.BindReservationToJournal(token, 405, 406);
        Assert.True(scheduler.RevalidateAndBeginReservation(
            token,
            selection,
            CreatePrivateRevalidation(selection)).Allowed);
        var capacity = CreateCapacity();

        var wrongRequest = CreateFeedback(selection, AdapterResourceActionStatus.Failed) with
        {
            RequestId = selection.RequestId + 1
        };
        Assert.Equal(12, Assert.Throws<NativeResourceSchedulerException>(() =>
            scheduler.ApplyFeedback(
                token,
                new ResourceSchedulerFeedbackRequest(selection.RequestId + 1, 1, 0, true, 1),
                selection,
                capacity,
                wrongRequest)).ResultCode);

        var wrongAttempt = CreateFeedback(selection, AdapterResourceActionStatus.Failed) with
        {
            Authority = selection.Authority with
            {
                ActionAttemptIdLow = selection.Authority.ActionAttemptIdLow + 1
            }
        };
        Assert.Equal(12, Assert.Throws<NativeResourceSchedulerException>(() =>
            scheduler.ApplyFeedback(
                token,
                new ResourceSchedulerFeedbackRequest(selection.RequestId, 1, 0, true, 1),
                selection,
                capacity,
                wrongAttempt)).ResultCode);

        var retained = scheduler.ApplyFeedback(
            token,
            new ResourceSchedulerFeedbackRequest(selection.RequestId, 1, 0, true, 1),
            selection,
            capacity,
            CreateFeedback(selection, AdapterResourceActionStatus.Failed));
        Assert.False(retained.Applied);
        Assert.Equal(ResourceSchedulerReservationDisposition.Retained, retained.ReservationDisposition);

        var completed = scheduler.ApplyFeedback(
            token,
            new ResourceSchedulerFeedbackRequest(selection.RequestId, 1, 0, true, 1),
            selection,
            capacity,
            CreateFeedback(selection, AdapterResourceActionStatus.Completed));
        Assert.True(completed.Applied);
        Assert.Equal(ResourceSchedulerReservationDisposition.Completed, completed.ReservationDisposition);
    }

    [Fact]
    public void Revalidation_NewerSnapshotIsAllowedButLocalAuthorityDriftIsRejected()
    {
        var configuration = CreateConfiguration(25);
        using var scheduler = CreateSession(configuration);
        var selection = Assert.Single(
            PlanPrivate(scheduler, configuration, requestId: 91, now: 100).Selections);
        var token = scheduler.ReserveSelection(selection, 100, 200, 92, 1, 1);
        scheduler.BindReservationToJournal(token, 407, 408);
        var newer = selection.Authority with
        {
            SourceSnapshotGeneration = selection.Authority.SourceSnapshotGeneration + 1
        };

        foreach (var drifted in new[]
        {
            newer with { SchedulingRevision = newer.SchedulingRevision + 1 },
            newer with { OwnerInstanceIdHigh = newer.OwnerInstanceIdHigh + 1 },
            newer with { LeaseGeneration = newer.LeaseGeneration + 1 }
        })
        {
            var blocked = scheduler.RevalidateAndBeginReservation(
                token,
                selection,
                CreatePrivateRevalidation(selection) with { Authority = drifted });
            Assert.False(blocked.Allowed);
            Assert.Equal(ResourceSchedulerRevalidationReason.IdentityChanged, blocked.Reason);
        }

        var allowed = scheduler.RevalidateAndBeginReservation(
            token,
            selection,
            CreatePrivateRevalidation(selection) with { Authority = newer });
        Assert.True(allowed.Allowed);
        scheduler.AbandonReservation(token);
    }


    [Fact]
    public void Plan_RejectsPrivateManagerRouteWithoutHostSelfProof()
    {
        var configuration = CreateConfiguration(29);
        using var scheduler = CreateSession(configuration);
        var invalidPrivate = CreatePrivateResource() with
        {
            DiscardAuthority = CreateAuthority(
                AdapterResourceActionMask.Discard) with
            {
                ActionRoute = AdapterResourceActionRoute.ManagerDirect
            }
        };
        var privateResult = scheduler.Plan(
            CreateRequest(configuration, 301, 100),
            [CreateTarget(privateResourceCount: 1)],
            [invalidPrivate],
            CreateFactBatch());
        Assert.Equal(1U, privateResult.Summary.InvalidResourceCount);
        Assert.Empty(privateResult.Selections);
    }

    [Fact]
    public void Plan_RejectsPrivateCrossActionResourceIdDrift()
    {
        var configuration = CreateConfiguration(31);
        using var scheduler = CreateSession(configuration);
        var privateResource = CreatePrivateResource(
            AdapterResourceActionMask.Discard | AdapterResourceActionMask.Trim) with
        {
            TrimAuthority = CreateAuthority(
                AdapterResourceActionMask.Trim) with
            {
                ResourceId = 2
            }
        };
        var privateResult = scheduler.Plan(
            CreateRequest(configuration, 311, 100),
            [CreateTarget(privateResourceCount: 1)],
            [privateResource],
            CreateFactBatch());
        Assert.Equal(1U, privateResult.Summary.InvalidResourceCount);
    }


    private static NativeResourceSchedulerSession CreateSession(
        ResourceSchedulerConfig configuration,
        int targetCapacity = 2,
        int resourceCapacity = 4,
        int pendingCapacity = 4,
        int journalPendingCapacity = 4,
        ulong stateGeneration = 7)
        => new(
            targetCapacity,
            resourceCapacity,
            pendingCapacity,
            journalPendingCapacity,
            configuration,
            stateGeneration);

    private static ResourceSchedulerPlanResult PlanPrivate(
        NativeResourceSchedulerSession scheduler,
        ResourceSchedulerConfig configuration,
        ulong requestId,
        ulong now)
        => scheduler.Plan(
            CreateRequest(configuration, requestId, now),
            [CreateTarget(privateResourceCount: 1)],
            [CreatePrivateResource()],
            CreateFactBatch());

    private static ResourceSchedulerPlanRequest CreateRequest(
        ResourceSchedulerConfig configuration,
        ulong requestId,
        ulong now)
        => new(
            requestId,
            configuration.Generation,
            now,
            AdapterResourceActionMask.Discard |
                AdapterResourceActionMask.Trim |
                AdapterResourceActionMask.MoveDown,
            1 << (byte)AdapterResourceTier.PhysicalMemory,
            true);

    private static ResourceSchedulerFactBatch CreateFactBatch()
        => new(ProjectionEpoch, 8, []);

    private static ResourceSchedulerTarget CreateTarget(uint privateResourceCount)
        => new(
            11,
            12,
            13,
            14,
            15,
            16,
            17,
            30,
            CreateCapacity(),
            0,
            privateResourceCount,
            -3,
            ResourceSchedulerSchedulingGrade.Optimize,
            ResourceSchedulerSchedulingGrade.Normal,
            AdapterSoftwareSurfaceState.PureBackground,
            false,
            true);

    private static ResourceSchedulerExecutionAuthority CreateAuthority(
        AdapterResourceActionMask action,
        ulong resourceKey = 101,
        ulong attemptLow = 131,
        ulong attemptHigh = 132)
        => new(
            ResourceSchedulerSource.AdaptedPrivate,
            action,
            AdapterResourceActionRoute.AdapterHandler,
            ResourceSchedulerExecutionAuthority.TypedExecutorProof,
            1,
            1,
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
            24,
            attemptLow,
            attemptHigh,
            ProjectionEpoch);

    private static ResourceSchedulerPrivateResource CreatePrivateResource(
        AdapterResourceActionMask actions = AdapterResourceActionMask.Discard)
    {
        var empty = default(ResourceSchedulerExecutionAuthority);
        return new ResourceSchedulerPrivateResource(
            11,
            512 * Mib,
            (actions & AdapterResourceActionMask.Discard) != 0
                ? CreateAuthority(AdapterResourceActionMask.Discard)
                : empty,
            (actions & AdapterResourceActionMask.Trim) != 0
                ? CreateAuthority(AdapterResourceActionMask.Trim)
                : empty,
            (actions & AdapterResourceActionMask.MoveDown) != 0
                ? CreateAuthority(AdapterResourceActionMask.MoveDown)
                : empty,
            AdapterResourceTier.PhysicalMemory,
            AdapterResourceKind.Cache,
            AdapterResourceRecoveryKind.BuiltData,
            AdapterResourceGranularity.PartialUsable,
            AdapterResourceProtocol.AllActions & ~actions,
            AdapterResourceActionRoute.AdapterHandler,
            AdapterResourceDemandMask.None,
            0);
    }

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

    internal static ResourceSchedulerConfig CreateConfiguration(ulong generation)
        => new(
            generation,
            Mib,
            1.0,
            2.25,
            16.0,
            [1.80, 0.48, 0.88, 1.35, 0.88, 2.20, 0.48, 0.48, 2.20, 1.80, 0.88, 1.35, 0.88],
            [1.45, 1.20, 0.95, 0.72, 0.60],
            [0.55, 0.80, 0.95, 1.0, 1.10],
            [4, 3, 2, 1, 0],
            [0.50, 0.75, 1.0, 1.35],
            1.0,
            [3.30, 0.32, 0.90, 2.60, 0.90, 4.00, 0.32, 0.32, 4.00, 3.30, 0.90, 2.80, 1.25],
            [2.50, 0.42, 2.50, 1.80, 2.50, 2.50, 0.42, 0.42, 2.50, 1.50, 0.65, 0.65, 1.80],
            [1.80, 1.25, 1.15, 0.95, 1.15, 3.00, 3.00, 1.25, 3.00, 2.20, 0.95, 1.15, 0.95],
            [2.40, 1.30, 2.40, 0.90, 2.40, 2.40, 1.30, 2.40, 2.40, 0.90, 0.90, 0.90, 0.90],
            0.70,
            3.30,
            255.0,
            [1.70, 1.22, 1.08],
            512 * Mib,
            192,
            3,
            0.25,
            0.18,
            0.15,
            [0.06, 0.12, 0.20],
            [0.30, 0.30, 0.30],
            [0.24, 0.18, 0.12],
            [64 * Mib, 128 * Mib, 256 * Mib, 512 * Mib],
            [0.15, 0.30, 0.50, 0.75],
            [81.0, 61.0, 21.0],
            [0.15, 0.35, 0.65, 1.0],
            0,
            100,
            1,
            4,
            1,
            0.20,
            0.10,
            0.12,
            [2, 4, 1, 8, 16]);
}
