using System.Runtime.CompilerServices;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.Optimization;

internal static partial class HostManagerTransactionJournalProjection
{
    public static NativeTransactionJournalPrepareInput CreatePrepare(
        in NativeSmartCoordinatorAction action,
        ulong hostSessionIncarnation,
        ulong expectedJournalRevision,
        in NativeTransactionJournalPayloadReference payload,
        in NativeTransactionJournalPayloadProvenance payloadProvenance,
        ulong preparedAtUtcMilliseconds,
        uint maximumRecoveryAttempts,
        ulong recoveryDeadlineUtcMilliseconds)
    {
        ValidateExecutableAction(in action);
        if (IsAtomic(in action))
        {
            throw new ArgumentException(
                "Atomic actions must be projected and persisted as one complete batch.",
                nameof(action));
        }

        return CreatePrepareCore(
            in action,
            hostSessionIncarnation,
            expectedJournalRevision,
            in payload,
            in payloadProvenance,
            preparedAtUtcMilliseconds,
            maximumRecoveryAttempts,
            recoveryDeadlineUtcMilliseconds);
    }

    public static NativeTransactionJournalPrepareBatchInput CreatePrepareBatch(
        ReadOnlySpan<NativeSmartCoordinatorAction> actions,
        ulong hostSessionIncarnation,
        ulong expectedJournalRevision,
        ReadOnlySpan<NativeTransactionJournalPayloadReference> payloads,
        ReadOnlySpan<NativeTransactionJournalPayloadProvenance> payloadProvenances,
        ulong preparedAtUtcMilliseconds,
        uint maximumRecoveryAttempts,
        ulong recoveryDeadlineUtcMilliseconds,
        Span<NativeTransactionJournalPrepareInput> preparedInputs)
    {
        if (actions.Length < 2 ||
            actions.Length != payloads.Length ||
            actions.Length != payloadProvenances.Length ||
            preparedInputs.Length < actions.Length)
        {
            throw new ArgumentException("The atomic action, payload, and output counts are inconsistent.");
        }

        var first = actions[0];
        ValidateExecutableAction(in first);
        if (!IsAtomic(in first))
        {
            throw new ArgumentException("The action batch is not an atomic group.", nameof(actions));
        }

        for (var index = 0; index < actions.Length; index++)
        {
            var action = actions[index];
            ValidateExecutableAction(in action);
            if (!IsAtomic(in action) ||
                action.AtomicGroupId != first.AtomicGroupId ||
                action.ConfigurationGeneration != first.ConfigurationGeneration ||
                action.PlanEpoch != first.PlanEpoch ||
                action.SoftwareKey != first.SoftwareKey ||
                action.GroupMemberCount != actions.Length)
            {
                throw new ArgumentException("The action batch does not describe one complete atomic group.");
            }
            for (var previousIndex = 0; previousIndex < index; previousIndex++)
            {
                var previous = actions[previousIndex];
                if (previous.GroupMemberIndex == action.GroupMemberIndex ||
                    SameActionIdentity(in previous, in action))
                {
                    throw new ArgumentException("The atomic action batch contains a duplicate member.");
                }
            }

            preparedInputs[index] = CreatePrepareCore(
                in action,
                hostSessionIncarnation,
                expectedJournalRevision,
                in payloads[index],
                in payloadProvenances[index],
                preparedAtUtcMilliseconds,
                maximumRecoveryAttempts,
                recoveryDeadlineUtcMilliseconds);
        }

        return new NativeTransactionJournalPrepareBatchInput
        {
            AbiVersion = NativeTransactionJournalAbi.Version,
            StructSize = NativeTransactionJournalSession.SizeOf<NativeTransactionJournalPrepareBatchInput>(),
            ExpectedJournalRevision = expectedJournalRevision,
            InputCount = checked((uint)actions.Length),
            Flags = 0
        };
    }

    private static NativeTransactionJournalPrepareInput CreatePrepareCore(
        in NativeSmartCoordinatorAction action,
        ulong hostSessionIncarnation,
        ulong expectedJournalRevision,
        in NativeTransactionJournalPayloadReference payload,
        in NativeTransactionJournalPayloadProvenance payloadProvenance,
        ulong preparedAtUtcMilliseconds,
        uint maximumRecoveryAttempts,
        ulong recoveryDeadlineUtcMilliseconds)
    {
        if (hostSessionIncarnation == 0 ||
            expectedJournalRevision == 0 ||
            preparedAtUtcMilliseconds == 0 ||
            maximumRecoveryAttempts == 0 ||
            recoveryDeadlineUtcMilliseconds <= preparedAtUtcMilliseconds)
        {
            throw new ArgumentException("The transaction journal prepare identity or recovery budget is invalid.");
        }
        if (!payload.IsValid)
        {
            throw new ArgumentException("The rollback payload reference is invalid.", nameof(payload));
        }
        if (!payloadProvenance.IsValid)
        {
            throw new ArgumentException(
                "The rollback payload provenance is invalid.",
                nameof(payloadProvenance));
        }

        var projected = ProjectAction(in action, hostSessionIncarnation);
        return new NativeTransactionJournalPrepareInput
        {
            AbiVersion = NativeTransactionJournalAbi.Version,
            StructSize = NativeTransactionJournalSession.SizeOf<NativeTransactionJournalPrepareInput>(),
            ExpectedJournalRevision = expectedJournalRevision,
            Identity = projected.Identity,
            Scope = (uint)projected.Scope,
            Disposition = (uint)projected.Disposition,
            DomainMask = (uint)projected.Domain,
            GradeValidMask = (uint)projected.GradeValidMask,
            ProcessFromGrade = projected.ProcessFromGrade,
            ProcessToGrade = projected.ProcessToGrade,
            CpuFromGrade = projected.CpuFromGrade,
            CpuToGrade = projected.CpuToGrade,
            GpuFromGrade = projected.GpuFromGrade,
            GpuToGrade = projected.GpuToGrade,
            StableSystemStatus = 0,
            StableSystemError = 0,
            PayloadKind = (uint)NativeTransactionJournalPayloadKind.Durable,
            PayloadSlot = payload.Slot,
            PayloadGeneration = payload.Generation,
            PayloadLength = payload.Length,
            PayloadDigestLow = payload.DigestLow,
            PayloadDigestHigh = payload.DigestHigh,
            NowUtcMilliseconds = preparedAtUtcMilliseconds,
            MaximumRecoveryAttempts = maximumRecoveryAttempts,
            RecoveryDeadlineUtcMilliseconds = recoveryDeadlineUtcMilliseconds,
            AtomicGroupId = projected.AtomicGroupId,
            GroupMemberIndex = projected.GroupMemberIndex,
            GroupMemberCount = projected.GroupMemberCount,
            PayloadProvenanceDigestLow = payloadProvenance.DigestLow,
            PayloadProvenanceDigestHigh = payloadProvenance.DigestHigh
        };
    }

    public static NativeTransactionJournalMutationInput CreateMutation(
        in NativeTransactionJournalRecord record,
        ulong expectedJournalRevision,
        NativeTransactionJournalMutationEvent mutationEvent,
        ulong observedAtUtcMilliseconds,
        uint stableSystemStatus,
        uint stableSystemError)
    {
        if (expectedJournalRevision == 0 ||
            record.EntryRevision == 0 ||
            record.Identity.HostSessionIncarnation == 0 ||
            observedAtUtcMilliseconds == 0 ||
            observedAtUtcMilliseconds < record.UpdatedAtUtcMilliseconds ||
            !MutationMatchesPhase(mutationEvent, record.Phase))
        {
            throw new ArgumentException("The transaction journal mutation identity is invalid.");
        }
        return new NativeTransactionJournalMutationInput
        {
            AbiVersion = NativeTransactionJournalAbi.Version,
            StructSize = NativeTransactionJournalSession.SizeOf<NativeTransactionJournalMutationInput>(),
            Identity = record.Identity,
            ExpectedJournalRevision = expectedJournalRevision,
            ExpectedEntryRevision = record.EntryRevision,
            NowUtcMilliseconds = observedAtUtcMilliseconds,
            Event = (uint)mutationEvent,
            ExpectedPhase = record.Phase,
            StableSystemStatus = stableSystemStatus,
            StableSystemError = stableSystemError
        };
    }

    public static NativeTransactionJournalStageFeedbackInput CreateFeedback(
        in NativeTransactionJournalRecord record,
        ulong expectedJournalRevision,
        ulong expectedHostSessionIncarnation,
        in NativeSmartCoordinatorFeedback feedback)
    {
        ValidateFeedbackIdentity(in record, expectedHostSessionIncarnation, in feedback);
        if (expectedJournalRevision == 0 ||
            record.EntryRevision == 0 ||
            record.Phase != (uint)NativeTransactionJournalPhase.EffectObserved ||
            feedback.CompletedAtMilliseconds <= 0 ||
            checked((ulong)feedback.CompletedAtMilliseconds) < record.UpdatedAtUtcMilliseconds ||
            !feedback.ValidMask.HasFlag(NativeSmartCoordinatorFeedbackValidity.CompletedAt) ||
            (feedback.ValidMask & ~NativeSmartCoordinatorFeedbackValidity.Known) != 0 ||
            (feedback.Flags & ~NativeSmartCoordinatorFeedbackFlags.Known) != 0 ||
            !FeedbackReservedFieldsAreZero(in feedback))
        {
            throw new ArgumentException("The native feedback does not carry a durable completion identity.");
        }

        var validMask = NativeTransactionJournalFeedbackValidity.CompletedAt;
        if (feedback.ValidMask.HasFlag(NativeSmartCoordinatorFeedbackValidity.ActualProcessGrade))
        {
            validMask |= NativeTransactionJournalFeedbackValidity.ActualProcessGrade;
        }
        if (feedback.ValidMask.HasFlag(NativeSmartCoordinatorFeedbackValidity.ActualCpuGrade))
        {
            validMask |= NativeTransactionJournalFeedbackValidity.ActualCpuGrade;
        }
        if (feedback.ValidMask.HasFlag(NativeSmartCoordinatorFeedbackValidity.ActualGpuGrade))
        {
            validMask |= NativeTransactionJournalFeedbackValidity.ActualGpuGrade;
        }

        var flags = NativeTransactionJournalFeedbackFlags.None;
        if (feedback.Flags.HasFlag(NativeSmartCoordinatorFeedbackFlags.ProcessOwned))
        {
            flags |= NativeTransactionJournalFeedbackFlags.ProcessOwned;
        }
        if (feedback.Flags.HasFlag(NativeSmartCoordinatorFeedbackFlags.CpuOwned))
        {
            flags |= NativeTransactionJournalFeedbackFlags.CpuOwned;
        }
        if (feedback.Flags.HasFlag(NativeSmartCoordinatorFeedbackFlags.GpuOwned))
        {
            flags |= NativeTransactionJournalFeedbackFlags.GpuOwned;
        }
        if (feedback.Flags.HasFlag(NativeSmartCoordinatorFeedbackFlags.RollbackPayloadPersisted))
        {
            flags |= NativeTransactionJournalFeedbackFlags.RollbackPayloadPersisted;
        }

        return new NativeTransactionJournalStageFeedbackInput
        {
            AbiVersion = NativeTransactionJournalAbi.Version,
            StructSize = NativeTransactionJournalSession.SizeOf<NativeTransactionJournalStageFeedbackInput>(),
            Identity = record.Identity,
            ExpectedJournalRevision = expectedJournalRevision,
            ExpectedEntryRevision = record.EntryRevision,
            CompletedAtUtcMilliseconds = checked((ulong)feedback.CompletedAtMilliseconds),
            ExpectedPhase = record.Phase,
            FeedbackValidMask = (uint)validMask,
            FeedbackFlags = (uint)flags,
            FeedbackStatus = (uint)MapFeedbackStatus(feedback.Status),
            FeedbackSystemStatus = 0,
            FeedbackSystemError = feedback.SystemErrorCode,
            ActualProcessGrade = validMask.HasFlag(
                NativeTransactionJournalFeedbackValidity.ActualProcessGrade)
                ? (int)MapProcessGrade(feedback.ActualProcessGrade)
                : 0,
            ActualCpuGrade = validMask.HasFlag(
                NativeTransactionJournalFeedbackValidity.ActualCpuGrade)
                ? (int)MapAdapterGrade(feedback.ActualCpuGrade)
                : 0,
            ActualGpuGrade = validMask.HasFlag(
                NativeTransactionJournalFeedbackValidity.ActualGpuGrade)
                ? (int)MapAdapterGrade(feedback.ActualGpuGrade)
                : 0
        };
    }

    private static void ValidateExecutableAction(in NativeSmartCoordinatorAction action)
    {
        if (action.StructSize != Unsafe.SizeOf<NativeSmartCoordinatorAction>() ||
            !action.Flags.HasFlag(NativeSmartCoordinatorActionFlags.RequiresFeedback) ||
            (action.Flags & ~NativeSmartCoordinatorActionFlags.Known) != 0 ||
            (action.ValidMask & ~NativeSmartCoordinatorActionValidity.Known) != 0 ||
            (action.ReasonMask & ~NativeSmartCoordinatorReason.Known) != 0 ||
            !ActionReservedFieldsAreZero(in action) ||
            action.ConfigurationGeneration == 0 ||
            action.PlanEpoch == 0 ||
            action.ActionId == 0 ||
            action.TargetKey == 0 ||
            action.Disposition is not (NativeSmartCoordinatorActionDisposition.Apply or
                NativeSmartCoordinatorActionDisposition.Restore) ||
            (action.ValidMask.HasFlag(NativeSmartCoordinatorActionValidity.CpuScore) &&
                (!double.IsFinite(action.CpuScore) || action.CpuScore < 0)) ||
            (action.ValidMask.HasFlag(NativeSmartCoordinatorActionValidity.GpuScore) &&
                (!double.IsFinite(action.GpuScore) || action.GpuScore < 0)))
        {
            throw new ArgumentException("The smart coordinator action is not executable.");
        }

        var atomic = action.Flags.HasFlag(NativeSmartCoordinatorActionFlags.Atomic);
        if (atomic != action.ValidMask.HasFlag(NativeSmartCoordinatorActionValidity.AtomicGroup) ||
            (!atomic && (action.AtomicGroupId != 0 ||
                action.GroupMemberIndex != 0 ||
                action.GroupMemberCount != 0)) ||
            (atomic && (action.AtomicGroupId == 0 ||
                action.GroupMemberCount <= 1 ||
                action.GroupMemberIndex >= action.GroupMemberCount)))
        {
            throw new ArgumentException("The smart coordinator atomic-group shape is invalid.");
        }

        switch (action.Scope)
        {
            case NativeSmartCoordinatorActionScope.ProcessPolicy:
                const NativeSmartCoordinatorActionValidity processRequired =
                    NativeSmartCoordinatorActionValidity.ProcessIdentity |
                    NativeSmartCoordinatorActionValidity.ProcessGrade;
                const NativeSmartCoordinatorActionValidity processAllowed =
                    processRequired |
                    NativeSmartCoordinatorActionValidity.SoftwareIdentity |
                    NativeSmartCoordinatorActionValidity.CpuScore |
                    NativeSmartCoordinatorActionValidity.AtomicGroup;
                var hasSoftwareIdentity = action.ValidMask.HasFlag(
                    NativeSmartCoordinatorActionValidity.SoftwareIdentity);
                if (action.ProcessId == 0 ||
                    action.ProcessStartKey == 0 ||
                    action.DomainMask != NativeSmartCoordinatorGradeDomains.Process ||
                    (action.ValidMask & processRequired) != processRequired ||
                    (action.ValidMask & ~processAllowed) != 0 ||
                    hasSoftwareIdentity != (action.SoftwareKey != 0) ||
                    action.FromCpuGrade != NativeSmartCoordinatorAdapterGrade.Normal ||
                    action.ToCpuGrade != NativeSmartCoordinatorAdapterGrade.Normal ||
                    action.FromGpuGrade != NativeSmartCoordinatorAdapterGrade.Normal ||
                    action.ToGpuGrade != NativeSmartCoordinatorAdapterGrade.Normal)
                {
                    throw new ArgumentException("The process action identity or grade is incomplete.");
                }
                _ = MapProcessGrade(action.FromProcessGrade);
                _ = MapProcessGrade(action.ToProcessGrade);
                if (atomic && (!hasSoftwareIdentity ||
                    action.Disposition != NativeSmartCoordinatorActionDisposition.Apply ||
                    action.ToProcessGrade != NativeSmartCoordinatorProcessGrade.Level4))
                {
                    throw new ArgumentException("Only a complete software freeze group can be atomic.");
                }
                break;
            case NativeSmartCoordinatorActionScope.AdapterSoftware:
                var expectedGradeValidity = NativeSmartCoordinatorActionValidity.None;
                if (action.DomainMask.HasFlag(NativeSmartCoordinatorGradeDomains.Cpu))
                {
                    expectedGradeValidity |= NativeSmartCoordinatorActionValidity.CpuGrade;
                }
                if (action.DomainMask.HasFlag(NativeSmartCoordinatorGradeDomains.Gpu))
                {
                    expectedGradeValidity |= NativeSmartCoordinatorActionValidity.GpuGrade;
                }
                if (action.ProcessId != 0 ||
                    action.ProcessStartKey != 0 ||
                    action.SoftwareKey == 0 ||
                    action.TargetKey != action.SoftwareKey ||
                    action.DomainMask == NativeSmartCoordinatorGradeDomains.None ||
                    (action.DomainMask & ~(
                        NativeSmartCoordinatorGradeDomains.Cpu |
                        NativeSmartCoordinatorGradeDomains.Gpu)) != 0 ||
                    !action.ValidMask.HasFlag(NativeSmartCoordinatorActionValidity.SoftwareIdentity) ||
                    (action.ValidMask & expectedGradeValidity) != expectedGradeValidity ||
                    (action.ValidMask.HasFlag(NativeSmartCoordinatorActionValidity.CpuScore)
                        && !action.DomainMask.HasFlag(NativeSmartCoordinatorGradeDomains.Cpu)) ||
                    (action.ValidMask.HasFlag(NativeSmartCoordinatorActionValidity.GpuScore)
                        && !action.DomainMask.HasFlag(NativeSmartCoordinatorGradeDomains.Gpu)) ||
                    (action.Disposition == NativeSmartCoordinatorActionDisposition.Apply &&
                        ((action.DomainMask.HasFlag(NativeSmartCoordinatorGradeDomains.Cpu) &&
                            action.ToCpuGrade != NativeSmartCoordinatorAdapterGrade.Normal &&
                            !action.ValidMask.HasFlag(NativeSmartCoordinatorActionValidity.CpuScore)) ||
                         (action.DomainMask.HasFlag(NativeSmartCoordinatorGradeDomains.Gpu) &&
                            action.ToGpuGrade != NativeSmartCoordinatorAdapterGrade.Normal &&
                            !action.ValidMask.HasFlag(NativeSmartCoordinatorActionValidity.GpuScore)))) ||
                    (action.ValidMask & ~(
                        NativeSmartCoordinatorActionValidity.SoftwareIdentity |
                        NativeSmartCoordinatorActionValidity.CpuGrade |
                        NativeSmartCoordinatorActionValidity.GpuGrade |
                        NativeSmartCoordinatorActionValidity.CpuScore |
                        NativeSmartCoordinatorActionValidity.GpuScore)) != 0 ||
                    atomic ||
                    action.FromProcessGrade != NativeSmartCoordinatorProcessGrade.Normal ||
                    action.ToProcessGrade != NativeSmartCoordinatorProcessGrade.Normal)
                {
                    throw new ArgumentException("The adapter action identity or grade is incomplete.");
                }
                if (action.DomainMask.HasFlag(NativeSmartCoordinatorGradeDomains.Cpu))
                {
                    _ = MapAdapterGrade(action.FromCpuGrade);
                    _ = MapAdapterGrade(action.ToCpuGrade);
                }
                if (action.DomainMask.HasFlag(NativeSmartCoordinatorGradeDomains.Gpu))
                {
                    _ = MapAdapterGrade(action.FromGpuGrade);
                    _ = MapAdapterGrade(action.ToGpuGrade);
                }
                break;
            default:
                throw new ArgumentException("The smart coordinator action scope is unknown.");
        }
    }

    private static bool IsAtomic(in NativeSmartCoordinatorAction action)
        => action.Flags.HasFlag(NativeSmartCoordinatorActionFlags.Atomic);

    private static bool SameActionIdentity(
        in NativeSmartCoordinatorAction left,
        in NativeSmartCoordinatorAction right)
        => left.ConfigurationGeneration == right.ConfigurationGeneration &&
            left.PlanEpoch == right.PlanEpoch &&
            left.ActionId == right.ActionId &&
            left.TargetKey == right.TargetKey &&
            left.SoftwareKey == right.SoftwareKey &&
            left.ProcessStartKey == right.ProcessStartKey &&
            left.ProcessId == right.ProcessId;

    private static unsafe bool ActionReservedFieldsAreZero(
        in NativeSmartCoordinatorAction action)
        => action.Reserved0 == 0 &&
            action.Reserved1[0] == 0 &&
            action.Reserved1[1] == 0;

    private static unsafe bool FeedbackReservedFieldsAreZero(
        in NativeSmartCoordinatorFeedback feedback)
        => feedback.Reserved0[0] == 0 &&
            feedback.Reserved0[1] == 0 &&
            feedback.Reserved0[2] == 0 &&
            feedback.Reserved1 == 0;

    private static void ValidateFeedbackIdentity(
        in NativeTransactionJournalRecord record,
        ulong expectedHostSessionIncarnation,
        in NativeSmartCoordinatorFeedback feedback)
    {
        if (expectedHostSessionIncarnation == 0 ||
            record.Identity.HostSessionIncarnation != expectedHostSessionIncarnation ||
            feedback.StructSize != Unsafe.SizeOf<NativeSmartCoordinatorFeedback>() ||
            feedback.ConfigurationGeneration != record.Identity.ConfigurationGeneration ||
            feedback.PlanEpoch != record.Identity.PlanEpoch ||
            feedback.ActionId != record.Identity.ActionId ||
            feedback.TargetKey != record.Identity.TargetId ||
            feedback.SoftwareKey != record.Identity.SoftwareId ||
            feedback.ProcessStartKey != record.Identity.ProcessStartKey ||
            feedback.ProcessId != record.Identity.ProcessId ||
            feedback.Scope != MapFeedbackScope(record.Scope))
        {
            throw new ArgumentException("The native feedback does not match the transaction identity.");
        }
    }

}
