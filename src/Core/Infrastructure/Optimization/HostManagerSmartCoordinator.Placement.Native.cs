using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.GpuPlacement;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace ResourceManager.App.Infrastructure.Optimization;

public sealed partial class HostManagerSmartCoordinator
{
    private int placementRecoveryRecordOffset;

    private void EnsurePlacementCoordinatorWorkspace()
    {
        var desired = placementCoordinatorRuntime.CaptureDesired();
        var configuration = CreatePlacementCoordinatorConfiguration(desired);
        if (placementCoordinatorSession is null || placementCoordinatorWorkspace is null)
        {
            var attempt = placementCoordinatorRuntime.BeginInitialCreate(desired.HostPlan);
            NativePlacementCoordinatorSession? createdSession = null;
            try
            {
                createdSession = new NativePlacementCoordinatorSession(in configuration);
                var createdWorkspace = new NativePlacementCoordinatorWorkspace(
                    in configuration,
                    createdSession.Capacity);
                RequirePlacementSettlement(
                    placementCoordinatorRuntime.CompleteSucceeded(attempt),
                    HostManagerDeploymentAttemptSettlement.Applied,
                    "initial create");
                placementCoordinatorSession = createdSession;
                placementCoordinatorWorkspace = createdWorkspace;
                appliedPlacementCoordinatorPlan = desired;
                placementCoordinatorCycleEpoch = 0;
                createdSession = null;
                return;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                CompletePlacementFailure(attempt, exception, "initial-create");
                throw;
            }
            finally
            {
                createdSession?.Dispose();
            }
        }

        var applied = appliedPlacementCoordinatorPlan
            ?? throw new InvalidOperationException("The applied placement coordinator runtime identity is missing.");
        if (applied.HotPublish.ConfigurationGeneration == desired.HotPublish.ConfigurationGeneration)
        {
            if (applied.AbiVersion != desired.AbiVersion
                || applied.Recreate != desired.Recreate
                || applied.HotPublish != desired.HotPublish
                || applied.HostPlan.BuildSha256 != desired.HostPlan.BuildSha256
                || applied.HostPlan.DeploymentDigests.PlacementCoordinator
                    != desired.HostPlan.DeploymentDigests.PlacementCoordinator)
            {
                throw new InvalidOperationException(
                    "The Host Manager placement coordinator drifted without a profile revision change.");
            }
            return;
        }

        if (desired.HotPublish.ConfigurationGeneration < applied.HotPublish.ConfigurationGeneration)
        {
            throw new InvalidOperationException(
                $"The Host Manager placement coordinator generation must increase: applied {applied.HotPublish.ConfigurationGeneration}, desired {desired.HotPublish.ConfigurationGeneration}.");
        }

        if (applied.Recreate == desired.Recreate)
        {
            var attempt = placementCoordinatorRuntime.BeginHotPublish(desired.HostPlan);
            try
            {
                RequirePlacementStatus(
                    placementCoordinatorSession.Reconfigure(in configuration),
                    NativePlacementCoordinatorStatus.Ok,
                    "reconfigure");
                RequirePlacementSettlement(
                    placementCoordinatorRuntime.CompleteSucceeded(attempt),
                    HostManagerDeploymentAttemptSettlement.Applied,
                    "hot publish");
                appliedPlacementCoordinatorPlan = desired;
                return;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                try
                {
                    CompletePlacementFailure(attempt, exception, "hot-publish");
                }
                finally
                {
                    placementCoordinatorSession.Dispose();
                    placementCoordinatorSession = null;
                    placementCoordinatorWorkspace = null;
                    appliedPlacementCoordinatorPlan = null;
                }
                throw;
            }
        }

        var recreateAttempt = placementCoordinatorRuntime.BeginHostRecreateAndHotPublish(desired.HostPlan);
        NativePlacementCoordinatorSession? replacementSession = null;
        try
        {
            replacementSession = new NativePlacementCoordinatorSession(in configuration);
            var replacementWorkspace = new NativePlacementCoordinatorWorkspace(
                in configuration,
                replacementSession.Capacity);
            RequirePlacementSettlement(
                placementCoordinatorRuntime.CompleteSucceeded(recreateAttempt),
                HostManagerDeploymentAttemptSettlement.Applied,
                "host recreate");

            var previous = placementCoordinatorSession;
            placementCoordinatorSession = replacementSession;
            placementCoordinatorWorkspace = replacementWorkspace;
            appliedPlacementCoordinatorPlan = desired;
            placementCoordinatorCycleEpoch = 0;
            replacementSession = null;
            previous.Dispose();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            CompletePlacementFailure(recreateAttempt, exception, "host-recreate");
            throw;
        }
        finally
        {
            replacementSession?.Dispose();
        }
    }

    private IReadOnlyList<HostManagerAppliedPlacementReceipt> RestoreAppliedPlacementsThroughNative(
        HostManagerCycleEffectPermit permit,
        IReadOnlyList<HostManagerAppliedPlacementReceipt> placements,
        CancellationToken cancellationToken)
    {
        permit.Require(HostManagerCycleEffectKind.LegacyPlacementRestore);
        if (placements.Count == 0)
        {
            placementRecoveryRecordOffset = 0;
            return [];
        }
        var recoveryCapacity = permit.RecoveryRemaining;
        if (recoveryCapacity == 0)
        {
            return placements;
        }

        EnsurePlacementCoordinatorWorkspace();
        var session = placementCoordinatorSession
            ?? throw new InvalidOperationException("The Host Manager placement coordinator session is unavailable.");
        var workspace = placementCoordinatorWorkspace
            ?? throw new InvalidOperationException("The Host Manager placement coordinator workspace is unavailable.");
        var applied = appliedPlacementCoordinatorPlan
            ?? throw new InvalidOperationException("The Host Manager placement coordinator plan is unavailable.");
        var selectedPlacements = SelectPlacementRecordsForRecovery(
            placements,
            recoveryCapacity);
        if (selectedPlacements.Count == 0)
        {
            return placements;
        }
        var projection = HostManagerPlacementCoordinatorProjection.ProjectApplied(
            selectedPlacements,
            workspace.Applied);
        var observedAt = MonotonicMilliseconds();
        var actionCapacity = checked((uint)workspace.Actions.Length);
        var cycle = new NativePlacementCoordinatorCycleInput
        {
            AbiVersion = NativePlacementCoordinatorAbi.Version,
            StructSize = NativePlacementCoordinatorSession.SizeOf<NativePlacementCoordinatorCycleInput>(),
            ConfigurationGeneration = applied.HotPublish.ConfigurationGeneration,
            CycleEpoch = checked(++placementCoordinatorCycleEpoch),
            ObservedAtMilliseconds = observedAt,
            ValidMask = (ulong)NativePlacementCycleValidity.Required,
            DesiredCount = 0,
            AppliedCount = projection.RowCount,
            ActionCapacity = actionCapacity
        };
        RequirePlacementStatus(
            session.Plan(
                ref cycle,
                ReadOnlySpan<NativePlacementDesiredInput>.Empty,
                workspace.Applied.AsSpan(0, checked((int)projection.RowCount)),
                workspace.Actions),
            NativePlacementCoordinatorStatus.Ok,
            "plan");
        ValidatePlacementCycle(
            cycle,
            observedAt,
            expectedDesiredCount: 0,
            projection.RowCount,
            actionCapacity,
            applied,
            workspace);

        var settlements = new Dictionary<HostManagerPlacementNativeIdentity, HostManagerPlacementRecordSettlement>(
            checked((int)cycle.ActionCount));
        var actionIds = new HashSet<ulong>();
        for (var index = 0; index < cycle.ActionCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var action = workspace.Actions[index];
            var projected = projection.Require(action);
            ValidatePlacementRestoreAction(action, projected, observedAt, applied, actionIds);
            if (!permit.TryReserveRecovery(1, out var reservation)
                || reservation is null)
            {
                throw new InvalidOperationException(
                    "The Host Manager placement restore exceeded its admitted recovery budget.");
            }
            using (reservation)
            {
                reservation.EnterPointOfNoReturn();
                var settlement = RestorePlacementRecord(projected.Record);
                if (!settlements.TryAdd(projected.Identity, settlement))
                {
                    throw new InvalidDataException("Native placement coordinator emitted duplicate receipt actions.");
                }
                workspace.Feedback[index] = CreatePlacementFeedback(action, settlement);
            }
        }

        if (cycle.ActionCount != 0)
        {
            RequirePlacementStatus(
                session.ApplyFeedback(workspace.Feedback.AsSpan(0, checked((int)cycle.ActionCount))),
                NativePlacementCoordinatorStatus.Ok,
                "feedback");
        }
        return RebuildRemainingPlacements(placements, settlements);
    }

    private static NativePlacementCoordinatorConfiguration CreatePlacementCoordinatorConfiguration(
        PlacementCoordinatorRuntimePlan plan)
    {
        if (plan.AbiVersion != NativePlacementCoordinatorAbi.Version)
        {
            throw new InvalidDataException(
                $"Placement coordinator ABI mismatch: plan 0x{plan.AbiVersion:X8}, process 0x{NativePlacementCoordinatorAbi.Version:X8}.");
        }

        return new NativePlacementCoordinatorConfiguration
        {
            AbiVersion = plan.AbiVersion,
            StructSize = NativePlacementCoordinatorSession.SizeOf<NativePlacementCoordinatorConfiguration>(),
            Generation = plan.HotPublish.ConfigurationGeneration,
            MaximumDesiredCount = checked((uint)plan.Recreate.MaximumDesiredCount),
            MaximumAppliedCount = checked((uint)plan.Recreate.MaximumAppliedCount),
            MaximumActionCount = checked((uint)plan.Recreate.MaximumActionCount),
            MaximumStateCount = checked((uint)plan.Recreate.MaximumStateCount),
            RetryDelayMilliseconds = checked((ulong)plan.HotPublish.RetryDelayMilliseconds),
            ActionTimeoutMilliseconds = checked((ulong)plan.HotPublish.ActionTimeoutMilliseconds),
            MaximumFutureSkewMilliseconds = checked((ulong)plan.HotPublish.MaximumFutureSkewMilliseconds),
            Flags = 0
        };
    }

    private static void ValidatePlacementCycle(
        NativePlacementCoordinatorCycleInput cycle,
        ulong observedAt,
        uint expectedDesiredCount,
        uint expectedAppliedCount,
        uint expectedActionCapacity,
        PlacementCoordinatorRuntimePlan plan,
        NativePlacementCoordinatorWorkspace workspace)
    {
        if (cycle.AbiVersion != NativePlacementCoordinatorAbi.Version
            || cycle.StructSize != NativePlacementCoordinatorSession.SizeOf<NativePlacementCoordinatorCycleInput>()
            || cycle.ConfigurationGeneration != plan.HotPublish.ConfigurationGeneration
            || cycle.CycleEpoch == 0
            || cycle.ObservedAtMilliseconds != observedAt
            || cycle.ValidMask != (ulong)NativePlacementCycleValidity.Required
            || cycle.DesiredCount != expectedDesiredCount
            || cycle.AppliedCount != expectedAppliedCount
            || expectedActionCapacity == 0
            || expectedActionCapacity > checked((uint)workspace.Actions.Length)
            || cycle.ActionCapacity != expectedActionCapacity
            || cycle.ActionCount > cycle.ActionCapacity
            || cycle.StateRevision == 0)
        {
            throw new InvalidDataException("The native placement coordinator returned an invalid cycle envelope.");
        }
    }

    private IReadOnlyList<HostManagerAppliedPlacementReceipt>
        SelectPlacementRecordsForRecovery(
        IReadOnlyList<HostManagerAppliedPlacementReceipt> placements,
        uint maximumRecordCount)
    {
        placements = GpuActionFacts.AvailablePlacementEffects(placements);
        var recordCount = 0;
        foreach (var placement in placements)
        {
            recordCount = checked(recordCount + placement.Records.Count);
        }
        if (recordCount == 0)
        {
            placementRecoveryRecordOffset = 0;
            return [];
        }
        var selected = new List<HostManagerAppliedPlacementReceipt>();
        var remaining = Math.Min(maximumRecordCount, checked((uint)recordCount));
        var count = remaining;
        var start = placementRecoveryRecordOffset % recordCount;
        var skip = start;
        // Continue through the circular queue so a retained prefix cannot starve later records.
        for (var pass = 0; pass < 2 && remaining != 0; pass++)
        {
            foreach (var placement in placements)
            {
                if (remaining == 0) break;
                if (skip >= placement.Records.Count)
                {
                    skip -= placement.Records.Count;
                    continue;
                }
                var take = checked((int)Math.Min(remaining, checked((uint)(placement.Records.Count - skip))));
                var records = placement.Records.Skip(skip).Take(take).ToArray();
                selected.Add(placement with { Records = records });
                remaining -= checked((uint)take);
                skip = 0;
            }
        }
        placementRecoveryRecordOffset = checked((int)(((long)start + count) % recordCount));
        return selected;
    }

    private static void ValidatePlacementRestoreAction(
        NativePlacementAction action,
        HostManagerPlacementProjectedRecord projected,
        ulong observedAt,
        PlacementCoordinatorRuntimePlan plan,
        ISet<ulong> actionIds)
    {
        var input = projected.Input;
        var flags = (NativePlacementActionFlags)action.Flags;
        var reason = (NativePlacementActionReason)action.ReasonMask;
        var expectedDeadline = checked(observedAt + (ulong)plan.HotPublish.ActionTimeoutMilliseconds);
        // With no desired row, the native session may retain its prior desired digest.
        // Restore ownership is bound by the applied receipt digest and process identity.
        if (action.StructSize != NativePlacementCoordinatorSession.SizeOf<NativePlacementAction>()
            || action.Disposition != (uint)NativePlacementActionDisposition.Restore
            || action.ActionId == 0
            || !actionIds.Add(action.ActionId)
            || action.TargetKey != input.TargetKey
            || action.RecordKey != input.RecordKey
            || action.ResourceKind != input.ResourceKind
            || action.PlacementKind != input.PlacementKind
            || action.PreviousDigest != input.PreviousDigest
            || (flags & ~NativePlacementActionFlags.Known) != 0
            || (reason & ~NativePlacementActionReason.Known) != 0
            || reason == NativePlacementActionReason.None
            || action.DeadlineMilliseconds != expectedDeadline
            || action.Priority != 0
            || action.Reserved != 0
            || (flags.HasFlag(NativePlacementActionFlags.ProcessIdentityValid)
                != ((input.ValidMask & (ulong)NativePlacementAppliedValidity.ProcessIdentity) != 0))
            || action.ProcessId != input.ProcessId
            || action.ProcessStartKey != input.ProcessStartKey)
        {
            throw new InvalidDataException(
                $"Native placement action {action.ActionId} has an invalid restore shape.");
        }
    }

    private static NativePlacementFeedback CreatePlacementFeedback(
        NativePlacementAction action,
        HostManagerPlacementRecordSettlement settlement)
        => new()
        {
            StructSize = NativePlacementCoordinatorSession.SizeOf<NativePlacementFeedback>(),
            Status = settlement.Kind switch
            {
                HostManagerPlacementSettlementKind.Restored => (uint)NativePlacementFeedbackStatus.Restored,
                HostManagerPlacementSettlementKind.AlreadyRestored => (uint)NativePlacementFeedbackStatus.AlreadySatisfied,
                HostManagerPlacementSettlementKind.OwnershipLost => (uint)NativePlacementFeedbackStatus.OwnershipLost,
                HostManagerPlacementSettlementKind.RetryableFailure => (uint)NativePlacementFeedbackStatus.RetryableFailure,
                HostManagerPlacementSettlementKind.InvalidReceipt => (uint)NativePlacementFeedbackStatus.InvalidReceipt,
                _ => throw new ArgumentOutOfRangeException(nameof(settlement), settlement.Kind, null)
            },
            ActionId = action.ActionId,
            TargetKey = action.TargetKey,
            RecordKey = action.RecordKey,
            CompletedAtMilliseconds = MonotonicMilliseconds(),
            SystemErrorCode = unchecked((uint)settlement.NativeErrorCode),
            Flags = 0,
            ObservedDigest = 0,
            ValidMask = 0
        };

    private static IReadOnlyList<HostManagerAppliedPlacementReceipt> RebuildRemainingPlacements(
        IReadOnlyList<HostManagerAppliedPlacementReceipt> placements,
        IReadOnlyDictionary<HostManagerPlacementNativeIdentity, HostManagerPlacementRecordSettlement> settlements)
    {
        var remaining = new List<HostManagerAppliedPlacementReceipt>();
        foreach (var placement in placements)
        {
            var records = placement.Records
                .Where(record =>
                {
                    var identity = HostManagerPlacementCoordinatorProjection.CreateIdentity(placement, record);
                    return !settlements.TryGetValue(identity, out var settlement)
                        || !settlement.CanRemoveReceipt;
                })
                .ToArray();
            if (records.Length != 0)
            {
                remaining.Add(placement with { Records = records });
            }
        }
        return remaining;
    }

    private void CompletePlacementFailure(
        HostManagerDeploymentAttemptToken attempt,
        Exception exception,
        string operation)
    {
        var settlement = placementCoordinatorRuntime.CompleteFailed(
            attempt,
            $"native-placement-coordinator:{operation}:{exception.GetType().Name}");
        if (settlement != HostManagerDeploymentAttemptSettlement.Failed)
        {
            throw new AggregateException(
                exception,
                new InvalidOperationException(
                    $"Host Manager placement coordinator {operation} failure settlement was {settlement}."));
        }
    }

    private static void RequirePlacementSettlement(
        HostManagerDeploymentAttemptSettlement actual,
        HostManagerDeploymentAttemptSettlement expected,
        string operation)
    {
        if (actual != expected)
        {
            throw new InvalidOperationException(
                $"Host Manager placement coordinator {operation} settlement was {actual}, expected {expected}.");
        }
    }

    private static void RequirePlacementStatus(
        NativePlacementCoordinatorStatus actual,
        NativePlacementCoordinatorStatus expected,
        string operation)
    {
        if (actual != expected)
        {
            throw new InvalidOperationException(
                $"Native placement coordinator {operation} returned {actual}, expected {expected}.");
        }
    }

    private static ulong MonotonicMilliseconds()
        => checked((ulong)Environment.TickCount64);
}
