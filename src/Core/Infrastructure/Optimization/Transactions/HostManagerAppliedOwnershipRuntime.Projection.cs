using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.Optimization.Transactions;

internal static class HostManagerAppliedOwnershipProjection
{
    public static NativeAppliedOwnershipPromoteInput CreatePromote(
        in NativeTransactionJournalSnapshotHeader journalSnapshot,
        in NativeTransactionJournalRecord journalRecord,
        in NativeSmartCoordinatorAction action,
        in NativeTransactionJournalPayloadReference payloadReference,
        in NativeTransactionJournalPayloadBinding payloadBinding,
        ulong expectedLedgerRevision,
        ulong promotedAtUtcMilliseconds)
    {
        var projectedBinding = ValidateJournalAction(
            in journalSnapshot,
            in journalRecord,
            in action,
            in payloadReference,
            NativeTransactionJournalDisposition.Apply,
            NativeTransactionJournalPhase.EffectObserved);
        if (payloadBinding != projectedBinding ||
            expectedLedgerRevision == 0 ||
            promotedAtUtcMilliseconds == 0 ||
            promotedAtUtcMilliseconds < journalRecord.UpdatedAtUtcMilliseconds)
        {
            throw new ArgumentException(
                "The applied ownership promotion does not match its durable journal and payload evidence.");
        }

        return new NativeAppliedOwnershipPromoteInput
        {
            AbiVersion = NativeAppliedOwnershipAbi.Version,
            StructSize = NativeAppliedOwnershipAbi.PromoteInputSize,
            ExpectedLedgerRevision = expectedLedgerRevision,
            Primary = CreatePrimary(in projectedBinding),
            OriginalBinding = CreateOriginalBinding(in payloadBinding),
            Payload = CreatePayloadReference(in payloadReference),
            CurrentGrades = CreateCurrentGrades(in journalRecord),
            PromotedAtUtcMilliseconds = promotedAtUtcMilliseconds
        };
    }

    public static NativeAppliedOwnershipTransitionEvidence CreateTransitionEvidence(
        in NativeTransactionJournalSnapshotHeader journalSnapshot,
        in NativeTransactionJournalRecord journalRecord,
        in NativeSmartCoordinatorAction action,
        in NativeTransactionJournalPayloadReference payloadReference)
    {
        var expectedDisposition = action.Disposition switch
        {
            NativeSmartCoordinatorActionDisposition.Apply =>
                NativeTransactionJournalDisposition.Apply,
            NativeSmartCoordinatorActionDisposition.Restore =>
                NativeTransactionJournalDisposition.Restore,
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        };
        var projectedBinding = ValidateJournalAction(
            in journalSnapshot,
            in journalRecord,
            in action,
            in payloadReference,
            expectedDisposition,
            NativeTransactionJournalPhase.Prepared);
        return new NativeAppliedOwnershipTransitionEvidence(
            CreateOriginalBinding(in projectedBinding),
            CreatePayloadReference(in payloadReference));
    }

    public static NativeAppliedOwnershipTransitionInput CompleteTransition(
        in NativeAppliedOwnershipTransitionInput plannedTransition,
        in NativeTransactionJournalRecord journalRecord,
        ulong updatedAtUtcMilliseconds)
    {
        if (plannedTransition.AbiVersion != NativeAppliedOwnershipAbi.Version ||
            plannedTransition.StructSize != NativeAppliedOwnershipAbi.TransitionInputSize ||
            plannedTransition.ExpectedLedgerRevision == 0 ||
            plannedTransition.Cas.ExpectedRecordRevision == 0 ||
            plannedTransition.UpdatedAtUtcMilliseconds != 0 ||
            updatedAtUtcMilliseconds == 0 ||
            updatedAtUtcMilliseconds < journalRecord.UpdatedAtUtcMilliseconds)
        {
            throw new ArgumentException(
                "The applied ownership transition plan is not canonical or has an invalid durable time.");
        }
        var completed = plannedTransition;
        completed.UpdatedAtUtcMilliseconds = updatedAtUtcMilliseconds;
        return completed;
    }

    public static NativeTransactionJournalPayloadReference CreatePayloadReference(
        in NativeAppliedOwnershipRecord currentOwnership)
    {
        ValidateCurrentRecord(in currentOwnership);
        return new NativeTransactionJournalPayloadReference(
            currentOwnership.Payload.Slot,
            currentOwnership.Payload.Generation,
            currentOwnership.Payload.Length,
            currentOwnership.Payload.DigestLow,
            currentOwnership.Payload.DigestHigh);
    }

    public static NativeTransactionJournalPayloadBinding CreatePayloadBinding(
        in NativeAppliedOwnershipRecord currentOwnership)
    {
        ValidateCurrentRecord(in currentOwnership);
        var original = currentOwnership.OriginalBinding;
        var identity = original.ActionIdentity;
        var binding = new NativeTransactionJournalPayloadBinding(
            original.JournalInstanceLow,
            original.JournalInstanceHigh,
            new NativeTransactionJournalIdentity
            {
                ConfigurationGeneration = identity.ConfigurationGeneration,
                PlanEpoch = identity.PlanEpoch,
                ActionId = identity.ActionId,
                HostSessionIncarnation = identity.HostSessionIncarnation,
                TargetId = identity.TargetId,
                SoftwareId = identity.SoftwareId,
                ProcessStartKey = identity.ProcessStartKey,
                ProcessId = identity.ProcessId,
                Reserved = identity.Reserved
            },
            (NativeTransactionJournalScope)original.Scope,
            (NativeTransactionJournalDisposition)original.Disposition,
            (NativeTransactionJournalDomain)original.DomainMask,
            (NativeTransactionJournalGradeValidity)original.GradeValidMask,
            original.ProcessFromGrade,
            original.ProcessToGrade,
            original.CpuFromGrade,
            original.CpuToGrade,
            original.GpuFromGrade,
            original.GpuToGrade,
            original.StableSystemStatus,
            original.StableSystemError,
            original.MaximumRecoveryAttempts,
            original.RecoveryDeadlineUtcMilliseconds,
            original.AtomicGroupId,
            original.GroupMemberIndex,
            original.GroupMemberCount);
        if (!binding.IsValid)
        {
            throw new InvalidDataException(
                "The applied ownership record does not contain a valid payload binding.");
        }
        return binding;
    }

    public static NativeAppliedOwnershipRemoveInput CreateRecoveryRemove(
        in NativeAppliedOwnershipRecord currentOwnership,
        ulong expectedLedgerRevision,
        ulong removedAtUtcMilliseconds)
    {
        ValidateCurrentRecord(in currentOwnership);
        if (expectedLedgerRevision == 0 ||
            removedAtUtcMilliseconds == 0 ||
            removedAtUtcMilliseconds < currentOwnership.UpdatedAtUtcMilliseconds)
        {
            throw new ArgumentException(
                "The recovery ownership removal revision or time is invalid.");
        }

        return new NativeAppliedOwnershipRemoveInput
        {
            AbiVersion = NativeAppliedOwnershipAbi.Version,
            StructSize = NativeAppliedOwnershipAbi.RemoveInputSize,
            ExpectedLedgerRevision = expectedLedgerRevision,
            Cas = CreateCas(in currentOwnership),
            RemovedAtUtcMilliseconds = removedAtUtcMilliseconds
        };
    }

    private static NativeTransactionJournalPayloadBinding ValidateJournalAction(
        in NativeTransactionJournalSnapshotHeader journalSnapshot,
        in NativeTransactionJournalRecord journalRecord,
        in NativeSmartCoordinatorAction action,
        in NativeTransactionJournalPayloadReference payloadReference,
        NativeTransactionJournalDisposition expectedDisposition,
        NativeTransactionJournalPhase expectedPhase)
    {
        if (journalSnapshot.JournalRevision == 0 ||
            (journalSnapshot.JournalInstanceLow == 0 &&
                journalSnapshot.JournalInstanceHigh == 0) ||
            journalRecord.Phase != (uint)expectedPhase ||
            journalRecord.PayloadKind != (uint)NativeTransactionJournalPayloadKind.Durable ||
            journalRecord.PayloadReserved != 0 ||
            journalRecord.GradeReserved != 0 ||
            journalRecord.PayloadSlot != payloadReference.Slot ||
            journalRecord.PayloadGeneration != payloadReference.Generation ||
            journalRecord.PayloadLength != payloadReference.Length ||
            journalRecord.PayloadDigestLow != payloadReference.DigestLow ||
            journalRecord.PayloadDigestHigh != payloadReference.DigestHigh ||
            !payloadReference.IsValid)
        {
            throw new ArgumentException(
                "The transaction journal record does not contain exact durable payload evidence.");
        }

        var projected = HostManagerTransactionJournalProjection.CreatePayloadBinding(
            in action,
            journalSnapshot.JournalInstanceLow,
            journalSnapshot.JournalInstanceHigh,
            journalRecord.Identity.HostSessionIncarnation,
            journalRecord.PreparedAtUtcMilliseconds,
            journalRecord.MaximumRecoveryAttempts,
            journalRecord.RecoveryDeadlineUtcMilliseconds);
        if (projected.Disposition != expectedDisposition ||
            !RecordMatchesProjectedBinding(in journalRecord, in projected))
        {
            throw new ArgumentException(
                "The transaction journal record and smart-coordinator action do not describe the same effect.");
        }
        return projected;
    }

    private static bool RecordMatchesProjectedBinding(
        in NativeTransactionJournalRecord record,
        in NativeTransactionJournalPayloadBinding binding)
        => record.Identity.ConfigurationGeneration ==
                binding.ActionIdentity.ConfigurationGeneration &&
            record.Identity.PlanEpoch == binding.ActionIdentity.PlanEpoch &&
            record.Identity.ActionId == binding.ActionIdentity.ActionId &&
            record.Identity.HostSessionIncarnation ==
                binding.ActionIdentity.HostSessionIncarnation &&
            record.Identity.TargetId == binding.ActionIdentity.TargetId &&
            record.Identity.SoftwareId == binding.ActionIdentity.SoftwareId &&
            record.Identity.ProcessStartKey == binding.ActionIdentity.ProcessStartKey &&
            record.Identity.ProcessId == binding.ActionIdentity.ProcessId &&
            record.Identity.Reserved == binding.ActionIdentity.Reserved &&
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
            record.MaximumRecoveryAttempts == binding.MaximumRecoveryAttempts &&
            record.RecoveryDeadlineUtcMilliseconds == binding.RecoveryDeadlineUtcMilliseconds &&
            record.AtomicGroupId == binding.AtomicGroupId &&
            record.GroupMemberIndex == binding.GroupMemberIndex &&
            record.GroupMemberCount == binding.GroupMemberCount;

    private static NativeAppliedOwnershipPrimaryIdentity CreatePrimary(
        in NativeTransactionJournalPayloadBinding binding)
        => binding.Scope switch
        {
            NativeTransactionJournalScope.Process => new NativeAppliedOwnershipPrimaryIdentity
            {
                Scope = (uint)NativeAppliedOwnershipScope.Process,
                TargetId = binding.ActionIdentity.TargetId,
                SoftwareId = binding.ActionIdentity.SoftwareId,
                ProcessStartKey = binding.ActionIdentity.ProcessStartKey,
                ProcessId = binding.ActionIdentity.ProcessId
            },
            NativeTransactionJournalScope.Software => new NativeAppliedOwnershipPrimaryIdentity
            {
                Scope = (uint)NativeAppliedOwnershipScope.Adapter,
                SoftwareId = binding.ActionIdentity.SoftwareId
            },
            _ => throw new ArgumentOutOfRangeException(nameof(binding))
        };

    private static NativeAppliedOwnershipOriginalBinding CreateOriginalBinding(
        in NativeTransactionJournalPayloadBinding binding)
    {
        var actionIdentity = binding.ActionIdentity;
        return new NativeAppliedOwnershipOriginalBinding
        {
            JournalInstanceLow = binding.JournalInstanceLow,
            JournalInstanceHigh = binding.JournalInstanceHigh,
            ActionIdentity = CreateActionIdentity(in actionIdentity),
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
            RecoveryDeadlineUtcMilliseconds = binding.RecoveryDeadlineUtcMilliseconds,
            AtomicGroupId = binding.AtomicGroupId,
            GroupMemberIndex = binding.GroupMemberIndex,
            GroupMemberCount = binding.GroupMemberCount
        };
    }

    private static NativeAppliedOwnershipActionIdentity CreateActionIdentity(
        in NativeTransactionJournalIdentity identity)
        => new()
        {
            ConfigurationGeneration = identity.ConfigurationGeneration,
            PlanEpoch = identity.PlanEpoch,
            ActionId = identity.ActionId,
            HostSessionIncarnation = identity.HostSessionIncarnation,
            TargetId = identity.TargetId,
            SoftwareId = identity.SoftwareId,
            ProcessStartKey = identity.ProcessStartKey,
            ProcessId = identity.ProcessId,
            Reserved = identity.Reserved
        };

    private static NativeAppliedOwnershipDurablePayloadReference CreatePayloadReference(
        in NativeTransactionJournalPayloadReference payload)
        => new()
        {
            Slot = payload.Slot,
            Generation = payload.Generation,
            Length = payload.Length,
            DigestLow = payload.DigestLow,
            DigestHigh = payload.DigestHigh
        };

    private static NativeAppliedOwnershipCurrentGrades CreateCurrentGrades(
        in NativeTransactionJournalRecord record)
        => new()
        {
            ValidMask = record.GradeValidMask,
            ProcessGrade = record.ProcessToGrade,
            CpuGrade = record.CpuToGrade,
            GpuGrade = record.GpuToGrade
        };

    private static NativeAppliedOwnershipCas CreateCas(
        in NativeAppliedOwnershipRecord record)
        => new()
        {
            Primary = record.Primary,
            JournalInstanceLow = record.OriginalBinding.JournalInstanceLow,
            JournalInstanceHigh = record.OriginalBinding.JournalInstanceHigh,
            OriginalActionIdentity = record.OriginalBinding.ActionIdentity,
            Payload = record.Payload,
            ExpectedRecordRevision = record.RecordRevision,
            ExpectedCurrentGrades = record.CurrentGrades
        };

    private static void ValidateCurrentRecord(in NativeAppliedOwnershipRecord current)
    {
        if (current.RecordRevision == 0 ||
            current.PromotedAtUtcMilliseconds == 0 ||
            current.UpdatedAtUtcMilliseconds < current.PromotedAtUtcMilliseconds ||
            current.Flags != 0 ||
            current.ReservedUInt32 != 0 ||
            current.Primary.ReservedUInt32 != 0 ||
            current.Primary.ProcessReserved != 0 ||
            current.OriginalBinding.ActionIdentity.Reserved != 0 ||
            current.OriginalBinding.RecoveryReserved != 0 ||
            current.CurrentGrades.ReservedUInt32 != 0 ||
            current.CurrentGrades.ReservedInt32 != 0)
        {
            throw new InvalidDataException("The applied ownership record is not canonical.");
        }
    }
}

internal readonly record struct NativeAppliedOwnershipTransitionEvidence(
    NativeAppliedOwnershipOriginalBinding Binding,
    NativeAppliedOwnershipDurablePayloadReference Payload);
