using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization.Transactions;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace ResourceManager.App.Infrastructure.Optimization;

public sealed partial class HostManagerSmartCoordinator
{
    private async Task<bool> RunNativePlanningAndActionsAsync(
        HostManagerCycleEffectAdmission effectAdmission,
        HostManagerProcessEffectValidationCycleSnapshot processEffectValidationScope,
        HostManagerSmartCoordinatorRuntimePlan desired,
        HostManagerSample sample,
        HostManagerComputeScoringCycleResult? computeScoring,
        NativeAppliedOwnershipFactIndex ownership,
        IReadOnlyDictionary<string, int> protectionLevels,
        bool policyExecutionEnabled,
        bool hardwareSchedulingEnabled,
        HostManagerTransactionJournalAdmission? executionAdmission,
        ulong sequence,
        string trigger,
        HostManagerSmartCoordinatorCycleDiagnostics? cycleDiagnostics,
        CancellationToken cancellationToken,
        List<HostManagerAutomaticGpuPreferencePlacement>? firstUse = null)
    {
        var workspace = nativeWorkspace
            ?? throw new InvalidOperationException("The native workspace is unavailable.");
        var modePlan = desired.RuntimePlan.OptimizationMode;
        var scoreOnlyEnabled = effectAdmission.IsScoreOnly;
        var now = durableTimeSource.NextUtc();
        var projection = ProjectNativeFacts(
            workspace,
            sample,
            ownership,
            protectionLevels,
            modePlan,
            policyExecutionEnabled,
            hardwareSchedulingEnabled,
            computeScoring);
        cycleDiagnostics?.CaptureProjection(projection.InputCount);
        cycleDiagnostics?.Mark("fact-projection");

        var nativeMaximumActionsThisCycle = scoreOnlyEnabled
            ? 1U
            : effectAdmission.NewPointOfNoReturnRemaining;
        var validMask = NativeSmartCoordinatorCycleValidity.ScoreOnlyMode |
            NativeSmartCoordinatorCycleValidity.MaximumActionsThisCycle;
        if (projection.ScoreSchedulingGeneration != 0)
        {
            validMask |= NativeSmartCoordinatorCycleValidity.ScoreSchedulingGeneration;
        }
        if (projection.CpuScoreSourceFingerprint != 0)
        {
            validMask |= NativeSmartCoordinatorCycleValidity.CpuScoreSourceFingerprint;
        }
        if (projection.GpuScoreSourceFingerprint != 0)
        {
            validMask |= NativeSmartCoordinatorCycleValidity.GpuScoreSourceFingerprint;
        }
        var flags = NativeSmartCoordinatorCycleFlags.None;
        if (scoreOnlyEnabled)
        {
            flags |= NativeSmartCoordinatorCycleFlags.ScoreOnly;
        }
        if (projection.FullProcessSnapshot)
        {
            flags |= NativeSmartCoordinatorCycleFlags.FullProcessSnapshot;
        }
        var canPublishAuthoritativeFacts = projection.FullProcessSnapshot
            && projection.AllAppliedOwnershipRecordsRepresented
            && nativeActionTransactions.JournalState ==
                HostManagerTransactionJournalRuntimeState.ReadyForExecution;
        if (authoritativeAppliedFactsRequired
            && !canPublishAuthoritativeFacts)
        {
            cycleDiagnostics?.Defer("authoritative-facts-incomplete");
            return false;
        }
        var sentAuthoritativeAppliedFacts = authoritativeAppliedFactsRequired
            && canPublishAuthoritativeFacts;
        if (sentAuthoritativeAppliedFacts)
        {
            flags |= NativeSmartCoordinatorCycleFlags.AuthoritativeAppliedFacts;
        }

        if (!trigger.Equals("scheduled", StringComparison.Ordinal))
        {
            flags |= NativeSmartCoordinatorCycleFlags.ExternalEvent;
        }

        var cycle = new NativeSmartCoordinatorCycleInput
        {
            AbiVersion = NativeSmartCoordinatorAbi.Version,
            StructSize = checked((uint)NativeSmartCoordinatorSession.SizeOf<NativeSmartCoordinatorCycleInput>()),
            InputRowStructSize = checked((uint)NativeSmartCoordinatorSession.SizeOf<NativeSmartCoordinatorInputRow>()),
            ActionStructSize = checked((uint)NativeSmartCoordinatorSession.SizeOf<NativeSmartCoordinatorAction>()),
            ConfigurationGeneration = desired.Configuration.Generation,
            CycleSequence = sequence,
            ObservedAtMilliseconds = now.ToUnixTimeMilliseconds(),
            ValidMask = validMask,
            Flags = flags,
            InputCount = projection.InputCount,
            ActionCapacity = workspace.Capacity.ActionCapacity,
            ScoreSchedulingGeneration = projection.ScoreSchedulingGeneration,
            CpuScoreSourceFingerprint = projection.CpuScoreSourceFingerprint,
            GpuScoreSourceFingerprint = projection.GpuScoreSourceFingerprint,
            MaximumActionsThisCycle = nativeMaximumActionsThisCycle
        };

        ReachTransitionPoint(HostManagerSmartCoordinatorTransitionPoint.BeforeNativeCall);
        var previousNativeSnapshot = workspace.Snapshot;
        RequireNativePlanStatus(
            workspace.Plan(in cycle),
            workspace,
            projection.InputCount,
            in previousNativeSnapshot);
        ReachTransitionPoint(HostManagerSmartCoordinatorTransitionPoint.AfterNativeCall);
        ValidateNativeSnapshot(
            workspace.Snapshot,
            workspace.Capacity,
            cycle.ConfigurationGeneration,
            cycle.CycleSequence,
            cycle.ObservedAtMilliseconds,
            cycle.ObservedAtMilliseconds,
            0,
            nativeMaximumActionsThisCycle,
            false,
            scoreOnlyEnabled);
        var plannedPolicyChangeCount = ValidateNativeActionBatch(workspace);
        cycleDiagnostics?.CaptureNativePlanSnapshot(
            workspace.Snapshot,
            plannedPolicyChangeCount);
        cycleDiagnostics?.Mark("native-plan");
        var planEpoch = workspace.Snapshot.PlanEpoch;
        var scheduleBaseAtMilliseconds = cycle.ObservedAtMilliseconds;
        var expectedReadbackRevision = workspace.Snapshot.StateRevision;
        var expectedReadbackInflightCount = workspace.Snapshot.InflightCount;
        var expectedReadbackWakeAfter = workspace.Snapshot.WakeAfterMilliseconds;
        var expectedReadbackNextWake = workspace.Snapshot.NextWakeAtMilliseconds;
        var execution = !scoreOnlyEnabled
            && effectAdmission.TryAcquire(
                HostManagerCycleEffectKind.NativeActionTransaction,
                out var nativeActionPermit)
            ? await ExecuteNativeTransactionsAsync(
                nativeActionPermit,
                processEffectValidationScope,
                workspace,
                plannedPolicyChangeCount,
                sample,
                executionAdmission
                    ?? throw new InvalidOperationException(
                        "The native transaction effect has no journal execution admission."),
                now,
                cancellationToken)
            : new NativeTransactionExecutionBatch(0, 0, []);
        var feedbackCount = execution.FeedbackCount;
        cycleDiagnostics?.CaptureExecution(
            feedbackCount,
            execution.SuccessfulFeedbackCount);
        if (feedbackCount > 0)
        {
            for (uint index = 0; index < feedbackCount; index++)
            {
                scheduleBaseAtMilliseconds = Math.Max(
                    scheduleBaseAtMilliseconds,
                    workspace.FeedbackRows[checked((int)index)].CompletedAtMilliseconds);
            }
            RequireNativeStatus(workspace.ApplyFeedback(feedbackCount), "feedback");
            ValidateNativeSnapshot(
                workspace.Snapshot,
                workspace.Capacity,
                cycle.ConfigurationGeneration,
                cycle.CycleSequence,
                cycle.ObservedAtMilliseconds,
                scheduleBaseAtMilliseconds,
                planEpoch,
                nativeMaximumActionsThisCycle,
                true,
                scoreOnlyEnabled);
            var expectedFeedbackInflightCount = checked(expectedReadbackInflightCount - feedbackCount);
            var expectedFeedbackRevision = NextNativeStateRevision(expectedReadbackRevision);
            if (workspace.Snapshot.InflightCount != expectedFeedbackInflightCount
                || workspace.Snapshot.StateRevision != expectedFeedbackRevision)
            {
                throw new InvalidDataException(
                    "The Host Manager smart coordinator feedback snapshot did not advance its reservations exactly once.");
            }
            expectedReadbackRevision = workspace.Snapshot.StateRevision;
            expectedReadbackInflightCount = workspace.Snapshot.InflightCount;
            expectedReadbackWakeAfter = workspace.Snapshot.WakeAfterMilliseconds;
            expectedReadbackNextWake = workspace.Snapshot.NextWakeAtMilliseconds;
            await CommitAcceptedNativeTransactionsAsync(
                executionAdmission
                    ?? throw new InvalidOperationException(
                        "The native transaction settlement has no journal execution admission."),
                execution.Settlements,
                CancellationToken.None);
        }
        cycleDiagnostics?.Mark("native-actions");

        RequireNativeStatus(workspace.RefreshSnapshot(), "snapshot");
        ValidateNativeSnapshot(
            workspace.Snapshot,
            workspace.Capacity,
            cycle.ConfigurationGeneration,
            cycle.CycleSequence,
            cycle.ObservedAtMilliseconds,
            scheduleBaseAtMilliseconds,
            planEpoch,
            nativeMaximumActionsThisCycle,
            true,
            scoreOnlyEnabled);
        ValidateNativeSnapshotRows(workspace.CurrentSnapshotRows, workspace.Snapshot);
        if (workspace.Snapshot.StateRevision != expectedReadbackRevision
            || workspace.Snapshot.InflightCount != expectedReadbackInflightCount
            || workspace.Snapshot.WakeAfterMilliseconds != expectedReadbackWakeAfter
            || workspace.Snapshot.NextWakeAtMilliseconds != expectedReadbackNextWake)
        {
            throw new InvalidDataException(
                "The Host Manager smart coordinator readback changed state without an operation.");
        }
        cycleDiagnostics?.CaptureNativeTerminalSnapshot(workspace.Snapshot);
        cycleDiagnostics?.Mark("snapshot-readback");
        authoritativeAppliedFactsRequired = (authoritativeAppliedFactsRequired && !sentAuthoritativeAppliedFacts)
            || workspace.Snapshot.Flags.HasFlag(
                NativeSmartCoordinatorSnapshotFlags.RequiresAuthoritativeResync);
        if (authoritativeAppliedFactsRequired)
        {
            cycleDiagnostics?.Defer("native-transaction-recovery-required");
            return false;
        }
        return await RunAutomaticPlacementCycleAsync(
            effectAdmission,
            desired,
            sample,
            computeScoring,
            hardwareSchedulingEnabled,
            cancellationToken,
            firstUse);
    }
}
