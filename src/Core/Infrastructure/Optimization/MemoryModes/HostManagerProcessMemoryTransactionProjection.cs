using System.Buffers.Binary;
using System.Security.Cryptography;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization.Transactions;

namespace ResourceManager.App.Infrastructure.Optimization;

internal readonly record struct HostManagerProcessMemoryTransactionAction(
    ulong ConfigurationGeneration,
    ulong SchedulingGeneration,
    ulong ActionId,
    ulong HostSessionIncarnation,
    ulong MemoryPolicyTargetKey,
    ulong SoftwareKey,
    string? SoftwareId,
    int ProcessId,
    ulong ProcessStartKey,
    NativeMemoryMode Mode,
    HostManagerNonAdaptedMemoryProcessAction DirectiveAction,
    NativeTransactionJournalDisposition Disposition,
    uint FromMemoryPriority,
    uint ToMemoryPriority);

internal static class HostManagerProcessMemoryTransactionProjection
{
    internal static HostManagerProcessMemoryTransactionAction CreateApply(
        HostManagerNonAdaptedMemoryProcessDirective directive,
        ulong configurationGeneration,
        ulong actionId,
        ulong hostSessionIncarnation)
    {
        ArgumentNullException.ThrowIfNull(directive);
        var target = directive.TargetMemoryPriority
            ?? throw new InvalidDataException(
                "An applying memory directive must carry an explicit memory-priority target.");
        var action = CreateAction(
            directive,
            configurationGeneration,
            actionId,
            hostSessionIncarnation,
            NativeTransactionJournalDisposition.Apply,
            fromMemoryPriority: 0,
            toMemoryPriority: target);
        ValidateAction(in action);
        return action;
    }

    internal static HostManagerProcessMemoryTransactionAction CreateRestore(
        HostManagerNonAdaptedMemoryProcessDirective directive,
        ulong configurationGeneration,
        ulong actionId,
        ulong hostSessionIncarnation,
        uint currentOwnedMemoryPriority)
    {
        ArgumentNullException.ThrowIfNull(directive);
        if (directive.TargetMemoryPriority is not null)
        {
            throw new InvalidDataException(
                "A restoring memory directive cannot carry a new memory-priority target.");
        }
        var action = CreateAction(
            directive,
            configurationGeneration,
            actionId,
            hostSessionIncarnation,
            NativeTransactionJournalDisposition.Restore,
            currentOwnedMemoryPriority,
            toMemoryPriority: 0);
        ValidateAction(in action);
        return action;
    }

    internal static HostManagerProcessMemoryTransactionAction
        CreateAttributionDriftRestore(
            HostManagerNonAdaptedMemoryProcessDirective directive,
            in NativeAppliedOwnershipRecord ownership,
            ulong configurationGeneration,
            ulong actionId,
            ulong hostSessionIncarnation,
            uint currentOwnedMemoryPriority)
    {
        ArgumentNullException.ThrowIfNull(directive);
        var primary = ownership.Primary;
        if (directive.Mode != NativeMemoryMode.Normal
            || directive.Action !=
                HostManagerNonAdaptedMemoryProcessAction.RestoreOwnedConstraints
            || directive.TargetMemoryPriority is not null
            || primary.Scope != (uint)NativeAppliedOwnershipScope.Process
            || primary.TargetId != directive.MemoryPolicyTargetKey
            || primary.ProcessId != checked((uint)directive.ProcessId)
            || primary.ProcessStartKey != directive.ProcessStartKey
            || primary.SoftwareId == directive.SoftwareKey)
        {
            throw new InvalidDataException(
                "A process-memory attribution-drift restore is not bound to one exact owned process incarnation.");
        }

        var action = new HostManagerProcessMemoryTransactionAction(
            configurationGeneration,
            directive.SchedulingGeneration,
            actionId,
            hostSessionIncarnation,
            directive.MemoryPolicyTargetKey,
            primary.SoftwareId,
            SoftwareId: null,
            directive.ProcessId,
            directive.ProcessStartKey,
            directive.Mode,
            directive.Action,
            NativeTransactionJournalDisposition.Restore,
            currentOwnedMemoryPriority,
            ToMemoryPriority: 0);
        ValidateAction(in action);
        return action;
    }

    internal static NativeAppliedOwnershipPrimaryIdentity CreatePrimary(
        in HostManagerProcessMemoryTransactionAction action)
    {
        ValidateAction(in action);
        return new NativeAppliedOwnershipPrimaryIdentity
        {
            Scope = (uint)NativeAppliedOwnershipScope.Process,
            TargetId = action.MemoryPolicyTargetKey,
            SoftwareId = action.SoftwareKey,
            ProcessStartKey = action.ProcessStartKey,
            ProcessId = checked((uint)action.ProcessId)
        };
    }

    internal static NativeTransactionJournalPayloadBinding CreatePayloadBinding(
        in HostManagerProcessMemoryTransactionAction action,
        ReadOnlySpan<byte> rollbackPayload,
        ulong journalInstanceLow,
        ulong journalInstanceHigh,
        ulong preparedAtUtcMilliseconds,
        uint maximumRecoveryAttempts,
        ulong recoveryDeadlineUtcMilliseconds)
    {
        ValidateAction(in action);
        RequireRollbackPayload(in action, rollbackPayload);
        if ((journalInstanceLow | journalInstanceHigh) == 0 ||
            preparedAtUtcMilliseconds == 0 ||
            maximumRecoveryAttempts == 0 ||
            recoveryDeadlineUtcMilliseconds <= preparedAtUtcMilliseconds)
        {
            throw new InvalidDataException(
                "The process-memory payload binding identity or recovery budget is invalid.");
        }

        var binding = new NativeTransactionJournalPayloadBinding(
            journalInstanceLow,
            journalInstanceHigh,
            CreateJournalIdentity(in action),
            NativeTransactionJournalScope.Process,
            action.Disposition,
            NativeTransactionJournalDomain.PhysicalMemory,
            NativeTransactionJournalGradeValidity.Memory,
            checked((int)action.FromMemoryPriority),
            checked((int)action.ToMemoryPriority),
            CpuFromGrade: 0,
            CpuToGrade: 0,
            GpuFromGrade: 0,
            GpuToGrade: 0,
            StableSystemStatus: 0,
            StableSystemError: 0,
            maximumRecoveryAttempts,
            recoveryDeadlineUtcMilliseconds,
            AtomicGroupId: 0,
            GroupMemberIndex: 0,
            GroupMemberCount: 0);
        if (!binding.IsValid)
        {
            throw new InvalidDataException(
                "The process-memory payload binding is not canonical.");
        }
        return binding;
    }

    internal static NativeTransactionJournalPrepareInput CreatePrepare(
        in HostManagerProcessMemoryTransactionAction action,
        ReadOnlySpan<byte> rollbackPayload,
        in NativeTransactionJournalPayloadBinding actionBinding,
        in NativeTransactionJournalPayloadBinding payloadSourceBinding,
        in NativeTransactionJournalPayloadReference payloadReference,
        in NativeTransactionJournalPayloadProvenance payloadProvenance,
        ulong expectedJournalRevision,
        ulong preparedAtUtcMilliseconds)
    {
        ValidateAction(in action);
        RequireRollbackPayload(in action, rollbackPayload);
        RequirePayloadReferenceMatches(payloadReference, rollbackPayload);
        var expectedActionBinding = CreatePayloadBinding(
            in action,
            rollbackPayload,
            actionBinding.JournalInstanceLow,
            actionBinding.JournalInstanceHigh,
            preparedAtUtcMilliseconds,
            actionBinding.MaximumRecoveryAttempts,
            actionBinding.RecoveryDeadlineUtcMilliseconds);
        if (actionBinding != expectedActionBinding ||
            expectedJournalRevision == 0 ||
            !payloadSourceBinding.IsValid ||
            payloadSourceBinding.JournalInstanceLow != actionBinding.JournalInstanceLow ||
            payloadSourceBinding.JournalInstanceHigh != actionBinding.JournalInstanceHigh)
        {
            throw new InvalidDataException(
                "The process-memory prepare action is not exactly bound to the current journal.");
        }
        RequirePayloadSourceBinding(in action, in actionBinding, in payloadSourceBinding);
        var expectedProvenance = NativeTransactionJournalPayloadProvenance.Create(
            payloadSourceBinding);
        if (!payloadProvenance.IsValid || payloadProvenance != expectedProvenance)
        {
            throw new InvalidDataException(
                "The process-memory prepare payload provenance is not exact.");
        }

        return new NativeTransactionJournalPrepareInput
        {
            AbiVersion = NativeTransactionJournalAbi.Version,
            StructSize = NativeTransactionJournalSession
                .SizeOf<NativeTransactionJournalPrepareInput>(),
            ExpectedJournalRevision = expectedJournalRevision,
            Identity = actionBinding.ActionIdentity,
            Scope = (uint)actionBinding.Scope,
            Disposition = (uint)actionBinding.Disposition,
            DomainMask = (uint)actionBinding.Domain,
            GradeValidMask = (uint)actionBinding.GradeValidMask,
            ProcessFromGrade = actionBinding.ProcessFromGrade,
            ProcessToGrade = actionBinding.ProcessToGrade,
            CpuFromGrade = 0,
            CpuToGrade = 0,
            GpuFromGrade = 0,
            GpuToGrade = 0,
            StableSystemStatus = 0,
            StableSystemError = 0,
            PayloadKind = (uint)NativeTransactionJournalPayloadKind.Durable,
            PayloadSlot = payloadReference.Slot,
            PayloadGeneration = payloadReference.Generation,
            PayloadReserved = 0,
            PayloadLength = payloadReference.Length,
            PayloadDigestLow = payloadReference.DigestLow,
            PayloadDigestHigh = payloadReference.DigestHigh,
            NowUtcMilliseconds = preparedAtUtcMilliseconds,
            MaximumRecoveryAttempts = actionBinding.MaximumRecoveryAttempts,
            RetryPolicyReserved = 0,
            RecoveryDeadlineUtcMilliseconds = actionBinding.RecoveryDeadlineUtcMilliseconds,
            AtomicGroupId = 0,
            GroupMemberIndex = 0,
            GroupMemberCount = 0,
            PayloadProvenanceDigestLow = payloadProvenance.DigestLow,
            PayloadProvenanceDigestHigh = payloadProvenance.DigestHigh
        };
    }

    internal static NativeTransactionJournalStageFeedbackInput CreateFeedback(
        in HostManagerProcessMemoryTransactionAction action,
        in NativeTransactionJournalRecord record,
        ulong expectedJournalRevision,
        ulong completedAtUtcMilliseconds,
        NativeTransactionJournalFeedbackStatus status,
        uint? actualMemoryPriority,
        uint systemStatus = 0,
        uint systemError = 0)
    {
        ValidateAction(in action);
        RequireRecordMatchesAction(in record, in action);
        if (expectedJournalRevision == 0 ||
            record.EntryRevision == 0 ||
            record.Phase != (uint)NativeTransactionJournalPhase.EffectObserved ||
            completedAtUtcMilliseconds == 0 ||
            completedAtUtcMilliseconds < record.UpdatedAtUtcMilliseconds)
        {
            throw new InvalidDataException(
                "The process-memory feedback does not match an observed durable effect.");
        }

        var validMask = NativeTransactionJournalFeedbackValidity.CompletedAt;
        var flags = NativeTransactionJournalFeedbackFlags.None;
        var actual = 0;
        switch (status)
        {
            case NativeTransactionJournalFeedbackStatus.Succeeded:
                if (actualMemoryPriority != action.ToMemoryPriority)
                {
                    throw new InvalidDataException(
                        "Successful process-memory feedback must report the exact target priority.");
                }
                validMask |= NativeTransactionJournalFeedbackValidity.ActualMemoryPriority;
                actual = checked((int)actualMemoryPriority.Value);
                if (action.ToMemoryPriority != 0)
                {
                    flags = NativeTransactionJournalFeedbackFlags.MemoryOwned |
                        NativeTransactionJournalFeedbackFlags.RollbackPayloadPersisted;
                }
                break;
            case NativeTransactionJournalFeedbackStatus.FailedUnchanged:
            case NativeTransactionJournalFeedbackStatus.Rejected:
                if (actualMemoryPriority is uint unchanged)
                {
                    if (unchanged != action.FromMemoryPriority)
                    {
                        throw new InvalidDataException(
                            "Unchanged process-memory feedback must report the exact source priority.");
                    }
                    validMask |= NativeTransactionJournalFeedbackValidity.ActualMemoryPriority;
                    actual = checked((int)unchanged);
                }
                break;
            case NativeTransactionJournalFeedbackStatus.Skipped:
            case NativeTransactionJournalFeedbackStatus.OwnershipLost:
            case NativeTransactionJournalFeedbackStatus.StateUncertain:
                if (actualMemoryPriority is not null)
                {
                    throw new InvalidDataException(
                        "Unobserved process-memory feedback cannot claim an actual priority.");
                }
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(status));
        }

        return new NativeTransactionJournalStageFeedbackInput
        {
            AbiVersion = NativeTransactionJournalAbi.Version,
            StructSize = NativeTransactionJournalSession
                .SizeOf<NativeTransactionJournalStageFeedbackInput>(),
            Identity = record.Identity,
            ExpectedJournalRevision = expectedJournalRevision,
            ExpectedEntryRevision = record.EntryRevision,
            CompletedAtUtcMilliseconds = completedAtUtcMilliseconds,
            ExpectedPhase = record.Phase,
            FeedbackValidMask = (uint)validMask,
            FeedbackFlags = (uint)flags,
            FeedbackStatus = (uint)status,
            FeedbackSystemStatus = systemStatus,
            FeedbackSystemError = systemError,
            FeedbackReserved = 0,
            ActualProcessGrade = actual,
            ActualCpuGrade = 0,
            ActualGpuGrade = 0,
            ActualReserved = 0
        };
    }

    internal static NativeAppliedOwnershipPromoteInput CreatePromote(
        in HostManagerProcessMemoryTransactionAction action,
        ReadOnlySpan<byte> rollbackPayload,
        in NativeTransactionJournalSnapshotHeader journalSnapshot,
        in NativeTransactionJournalRecord journalRecord,
        in NativeTransactionJournalPayloadReference payloadReference,
        in NativeTransactionJournalPayloadBinding payloadBinding,
        ulong expectedLedgerRevision,
        ulong promotedAtUtcMilliseconds)
    {
        ValidateAction(in action);
        if (action.Disposition != NativeTransactionJournalDisposition.Apply)
        {
            throw new InvalidDataException(
                "Only a fresh process-memory apply can promote ownership.");
        }
        RequireRollbackPayload(in action, rollbackPayload);
        RequirePayloadReferenceMatches(payloadReference, rollbackPayload);
        RequireJournalEvidence(
            in action,
            in journalSnapshot,
            in journalRecord,
            in payloadReference,
            in payloadBinding,
            in payloadBinding,
            NativeTransactionJournalPhase.EffectObserved);
        if (expectedLedgerRevision == 0 ||
            promotedAtUtcMilliseconds == 0 ||
            promotedAtUtcMilliseconds < journalRecord.UpdatedAtUtcMilliseconds)
        {
            throw new InvalidDataException(
                "The process-memory ownership promotion revision or time is invalid.");
        }

        return new NativeAppliedOwnershipPromoteInput
        {
            AbiVersion = NativeAppliedOwnershipAbi.Version,
            StructSize = NativeAppliedOwnershipAbi.PromoteInputSize,
            ExpectedLedgerRevision = expectedLedgerRevision,
            Primary = CreatePrimary(in action),
            OriginalBinding = CreateOriginalBinding(in payloadBinding),
            Payload = CreateAppliedPayloadReference(in payloadReference),
            CurrentGrades = new NativeAppliedOwnershipCurrentGrades
            {
                ValidMask = (uint)NativeAppliedOwnershipGradeValidity.Memory,
                ProcessGrade = checked((int)action.ToMemoryPriority)
            },
            PromotedAtUtcMilliseconds = promotedAtUtcMilliseconds
        };
    }

    internal static NativeAppliedOwnershipTransitionEvidence
        CreateRestoreTransitionEvidence(
            in HostManagerProcessMemoryTransactionAction action,
            ReadOnlySpan<byte> rollbackPayload,
            in NativeTransactionJournalSnapshotHeader journalSnapshot,
            in NativeTransactionJournalRecord journalRecord,
            in NativeTransactionJournalPayloadReference payloadReference,
            in NativeTransactionJournalPayloadBinding transitionBinding,
            in NativeAppliedOwnershipRecord currentOwnership)
    {
        ValidateAction(in action);
        if (action.Disposition != NativeTransactionJournalDisposition.Restore)
        {
            throw new InvalidDataException(
                "Only a process-memory restore can remove an existing memory owner.");
        }
        RequireRollbackPayload(in action, rollbackPayload);
        RequirePayloadReferenceMatches(payloadReference, rollbackPayload);
        var originalBinding = HostManagerAppliedOwnershipProjection.CreatePayloadBinding(
            in currentOwnership);
        var originalPayload = HostManagerAppliedOwnershipProjection.CreatePayloadReference(
            in currentOwnership);
        RequirePayloadSourceBinding(in action, in transitionBinding, in originalBinding);
        if (originalPayload != payloadReference ||
            currentOwnership.Primary.Scope != (uint)NativeAppliedOwnershipScope.Process ||
            currentOwnership.Primary.TargetId != action.MemoryPolicyTargetKey ||
            currentOwnership.Primary.SoftwareId != action.SoftwareKey ||
            currentOwnership.Primary.ProcessStartKey != action.ProcessStartKey ||
            currentOwnership.Primary.ProcessId != checked((uint)action.ProcessId) ||
            currentOwnership.CurrentGrades.ValidMask !=
                (uint)NativeAppliedOwnershipGradeValidity.Memory ||
            currentOwnership.CurrentGrades.ProcessGrade !=
                checked((int)action.FromMemoryPriority) ||
            currentOwnership.CurrentGrades.CpuGrade != 0 ||
            currentOwnership.CurrentGrades.GpuGrade != 0)
        {
            throw new InvalidDataException(
                "The process-memory restore does not match the current memory owner.");
        }
        RequireJournalEvidence(
            in action,
            in journalSnapshot,
            in journalRecord,
            in payloadReference,
            in transitionBinding,
            in originalBinding,
            NativeTransactionJournalPhase.Prepared);
        return new NativeAppliedOwnershipTransitionEvidence(
            CreateOriginalBinding(in transitionBinding),
            CreateAppliedPayloadReference(in payloadReference));
    }

    private static HostManagerProcessMemoryTransactionAction CreateAction(
        HostManagerNonAdaptedMemoryProcessDirective directive,
        ulong configurationGeneration,
        ulong actionId,
        ulong hostSessionIncarnation,
        NativeTransactionJournalDisposition disposition,
        uint fromMemoryPriority,
        uint toMemoryPriority)
        => new(
            configurationGeneration,
            directive.SchedulingGeneration,
            actionId,
            hostSessionIncarnation,
            directive.MemoryPolicyTargetKey,
            directive.SoftwareKey,
            directive.SoftwareId,
            directive.ProcessId,
            directive.ProcessStartKey,
            directive.Mode,
            directive.Action,
            disposition,
            fromMemoryPriority,
            toMemoryPriority);

    private static void ValidateAction(
        in HostManagerProcessMemoryTransactionAction action)
    {
        if (action.ConfigurationGeneration == 0 ||
            action.SchedulingGeneration == 0 ||
            action.ActionId == 0 ||
            action.HostSessionIncarnation == 0 ||
            action.MemoryPolicyTargetKey == 0 ||
            action.SoftwareKey == 0 ||
            action.ProcessId <= 0 ||
            action.ProcessStartKey == 0 ||
            action.ProcessStartKey > long.MaxValue ||
            (action.SoftwareId is not null
                && (string.IsNullOrWhiteSpace(action.SoftwareId)
                    || NativeStableIdentity.CreateCaseInsensitiveKey(action.SoftwareId)
                        != action.SoftwareKey)) ||
            action.Disposition == NativeTransactionJournalDisposition.Apply
                && action.SoftwareId is null)
        {
            throw new InvalidDataException(
                "The process-memory transaction identity is not canonical.");
        }

        var expectedMemoryTarget = NativeStableIdentity.CreateCaseInsensitiveKey(
            HostManagerTargetIdentity.CreateProcessMemoryPolicyTargetId(
                action.ProcessId,
                action.ProcessStartKey));
        var cpuTarget = NativeStableIdentity.CreateCaseInsensitiveKey(
            HostManagerTargetIdentity.CreateProcessTargetId(
                action.ProcessId,
                action.ProcessStartKey));
        if (action.MemoryPolicyTargetKey != expectedMemoryTarget ||
            action.MemoryPolicyTargetKey == cpuTarget)
        {
            throw new InvalidDataException(
                "The process-memory transaction target is not its exact independent target.");
        }

        var valid = action.Disposition switch
        {
            NativeTransactionJournalDisposition.Apply =>
                action.FromMemoryPriority == 0 &&
                action.ToMemoryPriority is >= 1 and <= 5 &&
                (action.Mode, action.DirectiveAction) is
                    (NativeMemoryMode.Optimize,
                        HostManagerNonAdaptedMemoryProcessAction.Optimize) or
                    (NativeMemoryMode.PagedFrozen,
                        HostManagerNonAdaptedMemoryProcessAction.PagedFrozen),
            NativeTransactionJournalDisposition.Restore =>
                action.FromMemoryPriority is >= 1 and <= 5 &&
                action.ToMemoryPriority == 0 &&
                action.Mode is NativeMemoryMode.Unrestricted or NativeMemoryMode.Normal &&
                action.DirectiveAction ==
                    HostManagerNonAdaptedMemoryProcessAction.RestoreOwnedConstraints,
            _ => false
        };
        if (!valid)
        {
            throw new InvalidDataException(
                "A process-memory transaction must be a fresh apply or a complete restore; direct owned-target changes are not allowed.");
        }
    }

    private static void RequireRollbackPayload(
        in HostManagerProcessMemoryTransactionAction action,
        ReadOnlySpan<byte> rollbackPayload)
    {
        if (!HostManagerProcessPolicyRollbackPayloadCodec.TryDecode(
                rollbackPayload,
                out var payload) ||
            payload.Fields != HostManagerProcessPolicyTransactionFields.MemoryPriority ||
            payload.ProcessId != action.ProcessId ||
            payload.ProcessStartKey != checked((long)action.ProcessStartKey) ||
            payload.TargetMemoryPriority != ExpectedPayloadTarget(in action) ||
            payload.BaselineMemoryPriority == payload.TargetMemoryPriority)
        {
            throw new InvalidDataException(
                "The process-memory rollback payload does not prove the exact owned effect.");
        }
    }

    private static uint ExpectedPayloadTarget(
        in HostManagerProcessMemoryTransactionAction action)
        => action.Disposition == NativeTransactionJournalDisposition.Apply
            ? action.ToMemoryPriority
            : action.FromMemoryPriority;

    private static void RequirePayloadReferenceMatches(
        in NativeTransactionJournalPayloadReference payloadReference,
        ReadOnlySpan<byte> rollbackPayload)
    {
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(rollbackPayload, hash);
        if (!payloadReference.IsValid ||
            payloadReference.Length != checked((ulong)rollbackPayload.Length) ||
            payloadReference.DigestLow !=
                BinaryPrimitives.ReadUInt64LittleEndian(hash[..8]) ||
            payloadReference.DigestHigh !=
                BinaryPrimitives.ReadUInt64LittleEndian(hash.Slice(8, 8)))
        {
            throw new InvalidDataException(
                "The durable process-memory payload reference does not match the rollback bytes.");
        }
    }

    private static void RequirePayloadSourceBinding(
        in HostManagerProcessMemoryTransactionAction action,
        in NativeTransactionJournalPayloadBinding actionBinding,
        in NativeTransactionJournalPayloadBinding sourceBinding)
    {
        if (action.Disposition == NativeTransactionJournalDisposition.Apply)
        {
            if (sourceBinding != actionBinding)
            {
                throw new InvalidDataException(
                    "A fresh process-memory apply must persist payload bytes under its own exact binding.");
            }
            return;
        }

        var source = sourceBinding.ActionIdentity;
        if (sourceBinding.Scope != NativeTransactionJournalScope.Process ||
            sourceBinding.Disposition != NativeTransactionJournalDisposition.Apply ||
            sourceBinding.Domain != NativeTransactionJournalDomain.PhysicalMemory ||
            sourceBinding.GradeValidMask != NativeTransactionJournalGradeValidity.Memory ||
            sourceBinding.ProcessFromGrade != 0 ||
            sourceBinding.ProcessToGrade != checked((int)action.FromMemoryPriority) ||
            sourceBinding.CpuFromGrade != 0 ||
            sourceBinding.CpuToGrade != 0 ||
            sourceBinding.GpuFromGrade != 0 ||
            sourceBinding.GpuToGrade != 0 ||
            sourceBinding.StableSystemStatus != 0 ||
            sourceBinding.StableSystemError != 0 ||
            sourceBinding.AtomicGroupId != 0 ||
            sourceBinding.GroupMemberIndex != 0 ||
            sourceBinding.GroupMemberCount != 0 ||
            source.TargetId != action.MemoryPolicyTargetKey ||
            source.SoftwareId != action.SoftwareKey ||
            source.ProcessStartKey != action.ProcessStartKey ||
            source.ProcessId != checked((uint)action.ProcessId) ||
            source.Reserved != 0)
        {
            throw new InvalidDataException(
                "A process-memory restore must reuse the exact original fresh-apply payload.");
        }
    }

    private static void RequireJournalEvidence(
        in HostManagerProcessMemoryTransactionAction action,
        in NativeTransactionJournalSnapshotHeader snapshot,
        in NativeTransactionJournalRecord record,
        in NativeTransactionJournalPayloadReference payloadReference,
        in NativeTransactionJournalPayloadBinding actionBinding,
        in NativeTransactionJournalPayloadBinding payloadSourceBinding,
        NativeTransactionJournalPhase expectedPhase)
    {
        RequireRecordMatchesAction(in record, in action);
        RequirePayloadReferenceMatchesRecord(in payloadReference, in record);
        var expectedProvenance = NativeTransactionJournalPayloadProvenance.Create(
            payloadSourceBinding);
        if (snapshot.AbiVersion != NativeTransactionJournalAbi.Version ||
            snapshot.StructSize != NativeTransactionJournalSession
                .SizeOf<NativeTransactionJournalSnapshotHeader>() ||
            snapshot.JournalRevision == 0 ||
            snapshot.JournalInstanceLow != actionBinding.JournalInstanceLow ||
            snapshot.JournalInstanceHigh != actionBinding.JournalInstanceHigh ||
            payloadSourceBinding.JournalInstanceLow != actionBinding.JournalInstanceLow ||
            payloadSourceBinding.JournalInstanceHigh != actionBinding.JournalInstanceHigh ||
            record.Phase != (uint)expectedPhase ||
            record.PayloadProvenanceDigestLow != expectedProvenance.DigestLow ||
            record.PayloadProvenanceDigestHigh != expectedProvenance.DigestHigh ||
            !RecordMatchesBinding(in record, in actionBinding))
        {
            throw new InvalidDataException(
                "The process-memory journal record lacks exact payload and action evidence.");
        }
    }

    private static void RequireRecordMatchesAction(
        in NativeTransactionJournalRecord record,
        in HostManagerProcessMemoryTransactionAction action)
    {
        var identity = CreateJournalIdentity(in action);
        if (!SameIdentity(record.Identity, identity) ||
            record.Scope != (uint)NativeTransactionJournalScope.Process ||
            record.Disposition != (uint)action.Disposition ||
            record.DomainMask != (uint)NativeTransactionJournalDomain.PhysicalMemory ||
            record.GradeValidMask != (uint)NativeTransactionJournalGradeValidity.Memory ||
            record.ProcessFromGrade != checked((int)action.FromMemoryPriority) ||
            record.ProcessToGrade != checked((int)action.ToMemoryPriority) ||
            record.CpuFromGrade != 0 ||
            record.CpuToGrade != 0 ||
            record.GpuFromGrade != 0 ||
            record.GpuToGrade != 0 ||
            record.GradeReserved != 0)
        {
            throw new InvalidDataException(
                "The process-memory journal record does not match its transaction action.");
        }
    }

    private static void RequirePayloadReferenceMatchesRecord(
        in NativeTransactionJournalPayloadReference payloadReference,
        in NativeTransactionJournalRecord record)
    {
        if (!payloadReference.IsValid ||
            record.PayloadKind != (uint)NativeTransactionJournalPayloadKind.Durable ||
            record.PayloadSlot != payloadReference.Slot ||
            record.PayloadGeneration != payloadReference.Generation ||
            record.PayloadLength != payloadReference.Length ||
            record.PayloadDigestLow != payloadReference.DigestLow ||
            record.PayloadDigestHigh != payloadReference.DigestHigh ||
            record.PayloadReserved != 0)
        {
            throw new InvalidDataException(
                "The process-memory journal record lost its durable payload reference.");
        }
    }

    private static bool RecordMatchesBinding(
        in NativeTransactionJournalRecord record,
        in NativeTransactionJournalPayloadBinding binding)
        => SameIdentity(record.Identity, binding.ActionIdentity) &&
            record.Scope == (uint)binding.Scope &&
            record.Disposition == (uint)binding.Disposition &&
            record.DomainMask == (uint)binding.Domain &&
            record.GradeValidMask == (uint)binding.GradeValidMask &&
            record.ProcessFromGrade == binding.ProcessFromGrade &&
            record.ProcessToGrade == binding.ProcessToGrade &&
            record.CpuFromGrade == binding.CpuFromGrade &&
            record.CpuToGrade == binding.CpuToGrade &&
            record.GpuFromGrade == binding.GpuFromGrade &&
            record.GpuToGrade == binding.GpuToGrade &&
            record.StableSystemStatus == binding.StableSystemStatus &&
            record.StableSystemError == binding.StableSystemError &&
            record.MaximumRecoveryAttempts == binding.MaximumRecoveryAttempts &&
            record.RecoveryDeadlineUtcMilliseconds ==
                binding.RecoveryDeadlineUtcMilliseconds &&
            record.AtomicGroupId == binding.AtomicGroupId &&
            record.GroupMemberIndex == binding.GroupMemberIndex &&
            record.GroupMemberCount == binding.GroupMemberCount;

    private static NativeTransactionJournalIdentity CreateJournalIdentity(
        in HostManagerProcessMemoryTransactionAction action)
        => new()
        {
            ConfigurationGeneration = action.ConfigurationGeneration,
            PlanEpoch = action.SchedulingGeneration,
            ActionId = action.ActionId,
            HostSessionIncarnation = action.HostSessionIncarnation,
            TargetId = action.MemoryPolicyTargetKey,
            SoftwareId = action.SoftwareKey,
            ProcessStartKey = action.ProcessStartKey,
            ProcessId = checked((uint)action.ProcessId),
            Reserved = 0
        };

    private static bool SameIdentity(
        NativeTransactionJournalIdentity left,
        NativeTransactionJournalIdentity right)
        => left.ConfigurationGeneration == right.ConfigurationGeneration &&
            left.PlanEpoch == right.PlanEpoch &&
            left.ActionId == right.ActionId &&
            left.HostSessionIncarnation == right.HostSessionIncarnation &&
            left.TargetId == right.TargetId &&
            left.SoftwareId == right.SoftwareId &&
            left.ProcessStartKey == right.ProcessStartKey &&
            left.ProcessId == right.ProcessId &&
            left.Reserved == right.Reserved;

    private static NativeAppliedOwnershipOriginalBinding CreateOriginalBinding(
        in NativeTransactionJournalPayloadBinding binding)
        => new()
        {
            JournalInstanceLow = binding.JournalInstanceLow,
            JournalInstanceHigh = binding.JournalInstanceHigh,
            ActionIdentity = new NativeAppliedOwnershipActionIdentity
            {
                ConfigurationGeneration = binding.ActionIdentity.ConfigurationGeneration,
                PlanEpoch = binding.ActionIdentity.PlanEpoch,
                ActionId = binding.ActionIdentity.ActionId,
                HostSessionIncarnation = binding.ActionIdentity.HostSessionIncarnation,
                TargetId = binding.ActionIdentity.TargetId,
                SoftwareId = binding.ActionIdentity.SoftwareId,
                ProcessStartKey = binding.ActionIdentity.ProcessStartKey,
                ProcessId = binding.ActionIdentity.ProcessId,
                Reserved = 0
            },
            Scope = (uint)binding.Scope,
            Disposition = (uint)binding.Disposition,
            DomainMask = (uint)binding.Domain,
            GradeValidMask = (uint)binding.GradeValidMask,
            ProcessFromGrade = binding.ProcessFromGrade,
            ProcessToGrade = binding.ProcessToGrade,
            CpuFromGrade = binding.CpuFromGrade,
            CpuToGrade = binding.CpuToGrade,
            GpuFromGrade = binding.GpuFromGrade,
            GpuToGrade = binding.GpuToGrade,
            StableSystemStatus = binding.StableSystemStatus,
            StableSystemError = binding.StableSystemError,
            MaximumRecoveryAttempts = binding.MaximumRecoveryAttempts,
            RecoveryReserved = 0,
            RecoveryDeadlineUtcMilliseconds = binding.RecoveryDeadlineUtcMilliseconds,
            AtomicGroupId = binding.AtomicGroupId,
            GroupMemberIndex = binding.GroupMemberIndex,
            GroupMemberCount = binding.GroupMemberCount
        };

    private static NativeAppliedOwnershipDurablePayloadReference
        CreateAppliedPayloadReference(
            in NativeTransactionJournalPayloadReference payloadReference)
        => new()
        {
            Slot = payloadReference.Slot,
            Generation = payloadReference.Generation,
            Length = payloadReference.Length,
            DigestLow = payloadReference.DigestLow,
            DigestHigh = payloadReference.DigestHigh
        };
}
