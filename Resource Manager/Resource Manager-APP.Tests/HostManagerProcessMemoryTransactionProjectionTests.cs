using System.Buffers.Binary;
using System.Security.Cryptography;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization;
using ResourceManager.App.Infrastructure.Optimization.Transactions;

namespace ResourceManager.App.Tests;

public sealed class HostManagerProcessMemoryTransactionProjectionTests
{
    private const ulong ConfigurationGeneration = 0x101;
    private const ulong SchedulingGeneration = 0x202;
    private const ulong HostSessionIncarnation = 0x303;
    private const ulong JournalInstanceLow = 0x401;
    private const ulong JournalInstanceHigh = 0x402;
    private const ulong PreparedAt = 10_000;
    private const ulong RecoveryDeadline = 20_000;
    private const int ProcessId = 4242;
    private const ulong ProcessStartKey = 132_537_600_000_000_000;
    private const string SoftwareId = "editor.example";

    [Fact]
    public void FreshApply_ProjectsExactTaggedPrepareAndOwnershipPromotion()
    {
        var directive = CreateDirective(
            NativeMemoryMode.Optimize,
            HostManagerNonAdaptedMemoryProcessAction.Optimize,
            targetMemoryPriority: 3);
        var action = HostManagerProcessMemoryTransactionProjection.CreateApply(
            directive,
            ConfigurationGeneration,
            actionId: 0x501,
            HostSessionIncarnation);
        var payload = CreateRollbackPayload(targetMemoryPriority: 3);
        var binding = CreateBinding(action, payload);
        var provenance = NativeTransactionJournalPayloadProvenance.Create(binding);
        var reference = CreateReference(payload);
        var prepare = HostManagerProcessMemoryTransactionProjection.CreatePrepare(
            action,
            payload,
            binding,
            binding,
            reference,
            provenance,
            expectedJournalRevision: 7,
            PreparedAt);

        Assert.Equal(ConfigurationGeneration, prepare.Identity.ConfigurationGeneration);
        Assert.Equal(SchedulingGeneration, prepare.Identity.PlanEpoch);
        Assert.Equal(action.MemoryPolicyTargetKey, prepare.Identity.TargetId);
        Assert.Equal(action.SoftwareKey, prepare.Identity.SoftwareId);
        Assert.Equal(ProcessStartKey, prepare.Identity.ProcessStartKey);
        Assert.Equal((uint)ProcessId, prepare.Identity.ProcessId);
        Assert.Equal(0U, prepare.Identity.Reserved);
        Assert.Equal((uint)NativeTransactionJournalScope.Process, prepare.Scope);
        Assert.Equal((uint)NativeTransactionJournalDisposition.Apply, prepare.Disposition);
        Assert.Equal((uint)NativeTransactionJournalDomain.PhysicalMemory, prepare.DomainMask);
        Assert.Equal((uint)NativeTransactionJournalGradeValidity.Memory, prepare.GradeValidMask);
        Assert.Equal(0, prepare.ProcessFromGrade);
        Assert.Equal(3, prepare.ProcessToGrade);
        Assert.Equal(0, prepare.CpuFromGrade);
        Assert.Equal(0, prepare.CpuToGrade);
        Assert.Equal(0, prepare.GpuFromGrade);
        Assert.Equal(0, prepare.GpuToGrade);
        Assert.Equal(0U, prepare.PayloadReserved);
        Assert.Equal(0U, prepare.RetryPolicyReserved);
        Assert.Equal(0UL, prepare.AtomicGroupId);
        Assert.Equal(0U, prepare.GroupMemberIndex);
        Assert.Equal(0U, prepare.GroupMemberCount);

        var record = CreateRecord(prepare, NativeTransactionJournalPhase.EffectObserved);
        var snapshot = CreateSnapshot();
        var promote = HostManagerProcessMemoryTransactionProjection.CreatePromote(
            action,
            payload,
            snapshot,
            record,
            reference,
            binding,
            expectedLedgerRevision: 11,
            promotedAtUtcMilliseconds: record.UpdatedAtUtcMilliseconds + 1);

        Assert.Equal((uint)NativeAppliedOwnershipScope.Process, promote.Primary.Scope);
        Assert.Equal(action.MemoryPolicyTargetKey, promote.Primary.TargetId);
        Assert.Equal(action.SoftwareKey, promote.Primary.SoftwareId);
        Assert.Equal((uint)NativeAppliedOwnershipGradeValidity.Memory,
            promote.CurrentGrades.ValidMask);
        Assert.Equal(3, promote.CurrentGrades.ProcessGrade);
        Assert.Equal(0, promote.CurrentGrades.CpuGrade);
        Assert.Equal(0, promote.CurrentGrades.GpuGrade);
        Assert.Equal((uint)NativeAppliedOwnershipDomain.PhysicalMemory,
            promote.OriginalBinding.DomainMask);
        Assert.Equal((uint)NativeAppliedOwnershipGradeValidity.Memory,
            promote.OriginalBinding.GradeValidMask);
    }

    [Fact]
    public void Restore_ReusesOriginalPayloadAndProjectsOwnerRemovalEvidence()
    {
        var owned = CreateOwnedApplyState(targetMemoryPriority: 3);
        var restoreDirective = CreateDirective(
            NativeMemoryMode.Normal,
            HostManagerNonAdaptedMemoryProcessAction.RestoreOwnedConstraints,
            targetMemoryPriority: null);
        var restoreAction = HostManagerProcessMemoryTransactionProjection.CreateRestore(
            restoreDirective,
            ConfigurationGeneration,
            actionId: 0x601,
            HostSessionIncarnation,
            currentOwnedMemoryPriority: 3);
        var transitionBinding = HostManagerProcessMemoryTransactionProjection
            .CreatePayloadBinding(
                restoreAction,
                owned.Payload,
                JournalInstanceLow,
                JournalInstanceHigh,
                PreparedAt + 100,
                maximumRecoveryAttempts: 5,
                RecoveryDeadline + 100);
        var originalProvenance = NativeTransactionJournalPayloadProvenance.Create(
            owned.Binding);
        var prepare = HostManagerProcessMemoryTransactionProjection.CreatePrepare(
            restoreAction,
            owned.Payload,
            transitionBinding,
            owned.Binding,
            owned.Reference,
            originalProvenance,
            expectedJournalRevision: 13,
            preparedAtUtcMilliseconds: PreparedAt + 100);
        var preparedRecord = CreateRecord(
            prepare,
            NativeTransactionJournalPhase.Prepared);
        var snapshot = CreateSnapshot(journalRevision: 13);

        var evidence = HostManagerProcessMemoryTransactionProjection
            .CreateRestoreTransitionEvidence(
                restoreAction,
                owned.Payload,
                snapshot,
                preparedRecord,
                owned.Reference,
                transitionBinding,
                owned.Ownership);

        Assert.Equal((uint)NativeAppliedOwnershipDisposition.Restore,
            evidence.Binding.Disposition);
        Assert.Equal((uint)NativeAppliedOwnershipDomain.PhysicalMemory,
            evidence.Binding.DomainMask);
        Assert.Equal((uint)NativeAppliedOwnershipGradeValidity.Memory,
            evidence.Binding.GradeValidMask);
        Assert.Equal(3, evidence.Binding.ProcessFromGrade);
        Assert.Equal(0, evidence.Binding.ProcessToGrade);
        Assert.Equal(owned.Reference.Slot, evidence.Payload.Slot);
        Assert.Equal(owned.Reference.DigestHigh, evidence.Payload.DigestHigh);

        var observedRecord = preparedRecord;
        observedRecord.Phase = (uint)NativeTransactionJournalPhase.EffectObserved;
        var feedback = HostManagerProcessMemoryTransactionProjection.CreateFeedback(
            restoreAction,
            observedRecord,
            expectedJournalRevision: 14,
            completedAtUtcMilliseconds: observedRecord.UpdatedAtUtcMilliseconds + 1,
            NativeTransactionJournalFeedbackStatus.Succeeded,
            actualMemoryPriority: 0);
        Assert.Equal(
            (uint)(NativeTransactionJournalFeedbackValidity.CompletedAt |
                NativeTransactionJournalFeedbackValidity.ActualMemoryPriority),
            feedback.FeedbackValidMask);
        Assert.Equal((uint)NativeTransactionJournalFeedbackFlags.None,
            feedback.FeedbackFlags);
        Assert.Equal(0, feedback.ActualProcessGrade);
    }

    [Fact]
    public void ApplyFeedback_UsesOnlyMemoryTagAndMemoryOwnershipProof()
    {
        var state = CreatePreparedApplyState(targetMemoryPriority: 3);
        var record = CreateRecord(
            state.Prepare,
            NativeTransactionJournalPhase.EffectObserved);

        var succeeded = HostManagerProcessMemoryTransactionProjection.CreateFeedback(
            state.Action,
            record,
            expectedJournalRevision: 8,
            completedAtUtcMilliseconds: record.UpdatedAtUtcMilliseconds + 1,
            NativeTransactionJournalFeedbackStatus.Succeeded,
            actualMemoryPriority: 3);
        Assert.Equal(
            (uint)(NativeTransactionJournalFeedbackValidity.CompletedAt |
                NativeTransactionJournalFeedbackValidity.ActualMemoryPriority),
            succeeded.FeedbackValidMask);
        Assert.Equal(
            (uint)(NativeTransactionJournalFeedbackFlags.MemoryOwned |
                NativeTransactionJournalFeedbackFlags.RollbackPayloadPersisted),
            succeeded.FeedbackFlags);
        Assert.Equal(3, succeeded.ActualProcessGrade);
        Assert.Equal(0, succeeded.ActualCpuGrade);
        Assert.Equal(0, succeeded.ActualGpuGrade);

        var unchanged = HostManagerProcessMemoryTransactionProjection.CreateFeedback(
            state.Action,
            record,
            expectedJournalRevision: 8,
            completedAtUtcMilliseconds: record.UpdatedAtUtcMilliseconds + 1,
            NativeTransactionJournalFeedbackStatus.FailedUnchanged,
            actualMemoryPriority: 0,
            systemError: 5);
        Assert.Equal((uint)NativeTransactionJournalFeedbackFlags.None,
            unchanged.FeedbackFlags);
        Assert.Equal(0, unchanged.ActualProcessGrade);
        Assert.Equal(5U, unchanged.FeedbackSystemError);

        var uncertain = HostManagerProcessMemoryTransactionProjection.CreateFeedback(
            state.Action,
            record,
            expectedJournalRevision: 8,
            completedAtUtcMilliseconds: record.UpdatedAtUtcMilliseconds + 1,
            NativeTransactionJournalFeedbackStatus.StateUncertain,
            actualMemoryPriority: null);
        Assert.Equal((uint)NativeTransactionJournalFeedbackValidity.CompletedAt,
            uncertain.FeedbackValidMask);
        Assert.Equal((uint)NativeTransactionJournalFeedbackFlags.None,
            uncertain.FeedbackFlags);
    }

    [Fact]
    public void DirectOwnedTargetChange_IsRejectedBeforeAnyDurableProjection()
    {
        var state = CreatePreparedApplyState(targetMemoryPriority: 3);
        var directChange = state.Action with
        {
            FromMemoryPriority = 3,
            ToMemoryPriority = 1
        };

        Assert.Throws<InvalidDataException>(() =>
            HostManagerProcessMemoryTransactionProjection.CreatePrimary(
                directChange));
        Assert.Throws<InvalidDataException>(() =>
            HostManagerProcessMemoryTransactionProjection.CreatePayloadBinding(
                directChange,
                state.Payload,
                JournalInstanceLow,
                JournalInstanceHigh,
                PreparedAt,
                maximumRecoveryAttempts: 5,
                RecoveryDeadline));
    }

    [Fact]
    public void IdentityOrPriorityDrift_FailsClosed()
    {
        var state = CreatePreparedApplyState(targetMemoryPriority: 3);
        var targetDrift = state.Action with
        {
            MemoryPolicyTargetKey = state.Action.MemoryPolicyTargetKey + 1
        };
        var softwareDrift = state.Action with { SoftwareId = "other.example" };
        var priorityDrift = state.Action with { ToMemoryPriority = 6 };

        Assert.Throws<InvalidDataException>(() =>
            HostManagerProcessMemoryTransactionProjection.CreatePrimary(targetDrift));
        Assert.Throws<InvalidDataException>(() =>
            HostManagerProcessMemoryTransactionProjection.CreatePrimary(softwareDrift));
        Assert.Throws<InvalidDataException>(() =>
            HostManagerProcessMemoryTransactionProjection.CreatePrimary(priorityDrift));
    }

    [Fact]
    public void RollbackPayloadMustMatchIdentityTargetAndRepresentARealEffect()
    {
        var state = CreatePreparedApplyState(targetMemoryPriority: 3);
        var wrongTarget = CreateRollbackPayload(targetMemoryPriority: 1);
        var wrongProcess = CreateRollbackPayload(
            targetMemoryPriority: 3,
            processId: ProcessId + 1);
        var noEffect = CreateRollbackPayload(
            targetMemoryPriority: 3,
            baselineMemoryPriority: 3);

        Assert.Throws<InvalidDataException>(() =>
            CreateBinding(state.Action, wrongTarget));
        Assert.Throws<InvalidDataException>(() =>
            CreateBinding(state.Action, wrongProcess));
        Assert.Throws<InvalidDataException>(() =>
            CreateBinding(state.Action, noEffect));
    }

    [Fact]
    public void Prepare_RejectsPayloadReferenceProvenanceAndSourceBindingDrift()
    {
        var state = CreatePreparedApplyState(targetMemoryPriority: 3);
        var driftedReference = state.Reference with
        {
            DigestHigh = state.Reference.DigestHigh + 1
        };
        Assert.Throws<InvalidDataException>(() =>
            HostManagerProcessMemoryTransactionProjection.CreatePrepare(
                state.Action,
                state.Payload,
                state.Binding,
                state.Binding,
                driftedReference,
                state.Provenance,
                expectedJournalRevision: 7,
                PreparedAt));

        var driftedProvenance = state.Provenance with
        {
            DigestLow = state.Provenance.DigestLow + 1
        };
        Assert.Throws<InvalidDataException>(() =>
            HostManagerProcessMemoryTransactionProjection.CreatePrepare(
                state.Action,
                state.Payload,
                state.Binding,
                state.Binding,
                state.Reference,
                driftedProvenance,
                expectedJournalRevision: 7,
                PreparedAt));

        var foreignSource = state.Binding with
        {
            ActionIdentity = new NativeTransactionJournalIdentity
            {
                ConfigurationGeneration = state.Binding.ActionIdentity.ConfigurationGeneration,
                PlanEpoch = state.Binding.ActionIdentity.PlanEpoch,
                ActionId = state.Binding.ActionIdentity.ActionId,
                HostSessionIncarnation = state.Binding.ActionIdentity.HostSessionIncarnation,
                TargetId = state.Binding.ActionIdentity.TargetId + 1,
                SoftwareId = state.Binding.ActionIdentity.SoftwareId,
                ProcessStartKey = state.Binding.ActionIdentity.ProcessStartKey,
                ProcessId = state.Binding.ActionIdentity.ProcessId
            }
        };
        var foreignProvenance = NativeTransactionJournalPayloadProvenance.Create(
            foreignSource);
        Assert.Throws<InvalidDataException>(() =>
            HostManagerProcessMemoryTransactionProjection.CreatePrepare(
                state.Action,
                state.Payload,
                state.Binding,
                foreignSource,
                state.Reference,
                foreignProvenance,
                expectedJournalRevision: 7,
                PreparedAt));
    }

    [Fact]
    public void Restore_RequiresAnExactIndependentMemoryOwner()
    {
        var owned = CreateOwnedApplyState(targetMemoryPriority: 3);
        var directive = CreateDirective(
            NativeMemoryMode.Normal,
            HostManagerNonAdaptedMemoryProcessAction.RestoreOwnedConstraints,
            targetMemoryPriority: null);
        var action = HostManagerProcessMemoryTransactionProjection.CreateRestore(
            directive,
            ConfigurationGeneration,
            actionId: 0x701,
            HostSessionIncarnation,
            currentOwnedMemoryPriority: 3);
        var binding = HostManagerProcessMemoryTransactionProjection.CreatePayloadBinding(
            action,
            owned.Payload,
            JournalInstanceLow,
            JournalInstanceHigh,
            PreparedAt + 100,
            maximumRecoveryAttempts: 5,
            RecoveryDeadline + 100);
        var provenance = NativeTransactionJournalPayloadProvenance.Create(owned.Binding);
        var prepare = HostManagerProcessMemoryTransactionProjection.CreatePrepare(
            action,
            owned.Payload,
            binding,
            owned.Binding,
            owned.Reference,
            provenance,
            expectedJournalRevision: 9,
            preparedAtUtcMilliseconds: PreparedAt + 100);
        var record = CreateRecord(prepare, NativeTransactionJournalPhase.Prepared);
        var snapshot = CreateSnapshot(journalRevision: 9);
        var cpuTaggedOwner = owned.Ownership;
        cpuTaggedOwner.CurrentGrades.ValidMask =
            (uint)NativeAppliedOwnershipGradeValidity.Process;

        Assert.Throws<InvalidDataException>(() =>
            HostManagerProcessMemoryTransactionProjection
                .CreateRestoreTransitionEvidence(
                    action,
                    owned.Payload,
                    snapshot,
                    record,
                    owned.Reference,
                    binding,
                    cpuTaggedOwner));
    }

    private static PreparedState CreatePreparedApplyState(uint targetMemoryPriority)
    {
        var directive = CreateDirective(
            NativeMemoryMode.Optimize,
            HostManagerNonAdaptedMemoryProcessAction.Optimize,
            targetMemoryPriority);
        var action = HostManagerProcessMemoryTransactionProjection.CreateApply(
            directive,
            ConfigurationGeneration,
            actionId: 0x501,
            HostSessionIncarnation);
        var payload = CreateRollbackPayload(targetMemoryPriority);
        var binding = CreateBinding(action, payload);
        var reference = CreateReference(payload);
        var provenance = NativeTransactionJournalPayloadProvenance.Create(binding);
        var prepare = HostManagerProcessMemoryTransactionProjection.CreatePrepare(
            action,
            payload,
            binding,
            binding,
            reference,
            provenance,
            expectedJournalRevision: 7,
            PreparedAt);
        return new(action, payload, binding, reference, provenance, prepare);
    }

    private static OwnedState CreateOwnedApplyState(uint targetMemoryPriority)
    {
        var prepared = CreatePreparedApplyState(targetMemoryPriority);
        var record = CreateRecord(
            prepared.Prepare,
            NativeTransactionJournalPhase.EffectObserved);
        var snapshot = CreateSnapshot();
        var promote = HostManagerProcessMemoryTransactionProjection.CreatePromote(
            prepared.Action,
            prepared.Payload,
            snapshot,
            record,
            prepared.Reference,
            prepared.Binding,
            expectedLedgerRevision: 11,
            promotedAtUtcMilliseconds: record.UpdatedAtUtcMilliseconds + 1);
        var ownership = new NativeAppliedOwnershipRecord
        {
            Primary = promote.Primary,
            OriginalBinding = promote.OriginalBinding,
            Payload = promote.Payload,
            CurrentGrades = promote.CurrentGrades,
            RecordRevision = 19,
            PromotedAtUtcMilliseconds = promote.PromotedAtUtcMilliseconds,
            UpdatedAtUtcMilliseconds = promote.PromotedAtUtcMilliseconds
        };
        return new(
            prepared.Action,
            prepared.Payload,
            prepared.Binding,
            prepared.Reference,
            ownership);
    }

    private static HostManagerNonAdaptedMemoryProcessDirective CreateDirective(
        NativeMemoryMode mode,
        HostManagerNonAdaptedMemoryProcessAction action,
        uint? targetMemoryPriority)
    {
        var softwareKey = NativeStableIdentity.CreateCaseInsensitiveKey(SoftwareId);
        var targetKey = NativeStableIdentity.CreateCaseInsensitiveKey(
            HostManagerTargetIdentity.CreateProcessMemoryPolicyTargetId(
                ProcessId,
                ProcessStartKey));
        return new(
            SchedulingGeneration,
            softwareKey,
            SoftwareId,
            ProcessId,
            ProcessStartKey,
            targetKey,
            SoftwareRank: 1,
            mode,
            action,
            targetMemoryPriority);
    }

    private static byte[] CreateRollbackPayload(
        uint targetMemoryPriority,
        uint baselineMemoryPriority = 5,
        int processId = ProcessId)
        => HostManagerProcessPolicyRollbackPayloadCodec.Encode(
            new HostManagerProcessPolicyRollbackPayload(
                HostManagerProcessPolicyTransactionFields.MemoryPriority,
                processId,
                checked((long)ProcessStartKey),
                BaselinePriorityClass: 0,
                baselineMemoryPriority,
                BaselinePowerControlMask: 0,
                BaselinePowerStateMask: 0,
                targetMemoryPriority));

    private static NativeTransactionJournalPayloadBinding CreateBinding(
        HostManagerProcessMemoryTransactionAction action,
        byte[] payload)
        => HostManagerProcessMemoryTransactionProjection.CreatePayloadBinding(
            action,
            payload,
            JournalInstanceLow,
            JournalInstanceHigh,
            PreparedAt,
            maximumRecoveryAttempts: 5,
            RecoveryDeadline);

    private static NativeTransactionJournalPayloadReference CreateReference(
        byte[] payload)
    {
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(payload, hash);
        return new(
            Slot: 3,
            Generation: 4,
            Length: checked((ulong)payload.Length),
            DigestLow: BinaryPrimitives.ReadUInt64LittleEndian(hash[..8]),
            DigestHigh: BinaryPrimitives.ReadUInt64LittleEndian(hash.Slice(8, 8)));
    }

    private static NativeTransactionJournalSnapshotHeader CreateSnapshot(
        ulong journalRevision = 7)
        => new()
        {
            AbiVersion = NativeTransactionJournalAbi.Version,
            StructSize = NativeTransactionJournalSession
                .SizeOf<NativeTransactionJournalSnapshotHeader>(),
            JournalRevision = journalRevision,
            JournalInstanceLow = JournalInstanceLow,
            JournalInstanceHigh = JournalInstanceHigh
        };

    private static NativeTransactionJournalRecord CreateRecord(
        NativeTransactionJournalPrepareInput prepare,
        NativeTransactionJournalPhase phase)
        => new()
        {
            Identity = prepare.Identity,
            Scope = prepare.Scope,
            Disposition = prepare.Disposition,
            DomainMask = prepare.DomainMask,
            Phase = (uint)phase,
            GradeValidMask = prepare.GradeValidMask,
            GradeReserved = 0,
            StableSystemStatus = prepare.StableSystemStatus,
            StableSystemError = prepare.StableSystemError,
            ProcessFromGrade = prepare.ProcessFromGrade,
            ProcessToGrade = prepare.ProcessToGrade,
            CpuFromGrade = prepare.CpuFromGrade,
            CpuToGrade = prepare.CpuToGrade,
            GpuFromGrade = prepare.GpuFromGrade,
            GpuToGrade = prepare.GpuToGrade,
            PayloadKind = prepare.PayloadKind,
            PayloadSlot = prepare.PayloadSlot,
            PayloadGeneration = prepare.PayloadGeneration,
            PayloadReserved = prepare.PayloadReserved,
            PayloadLength = prepare.PayloadLength,
            PayloadDigestLow = prepare.PayloadDigestLow,
            PayloadDigestHigh = prepare.PayloadDigestHigh,
            PreparedAtUtcMilliseconds = prepare.NowUtcMilliseconds,
            UpdatedAtUtcMilliseconds = prepare.NowUtcMilliseconds + 1,
            EntryRevision = 17,
            MaximumRecoveryAttempts = prepare.MaximumRecoveryAttempts,
            RecoveryDeadlineUtcMilliseconds = prepare.RecoveryDeadlineUtcMilliseconds,
            AtomicGroupId = prepare.AtomicGroupId,
            GroupMemberIndex = prepare.GroupMemberIndex,
            GroupMemberCount = prepare.GroupMemberCount,
            PayloadProvenanceDigestLow = prepare.PayloadProvenanceDigestLow,
            PayloadProvenanceDigestHigh = prepare.PayloadProvenanceDigestHigh
        };

    private readonly record struct PreparedState(
        HostManagerProcessMemoryTransactionAction Action,
        byte[] Payload,
        NativeTransactionJournalPayloadBinding Binding,
        NativeTransactionJournalPayloadReference Reference,
        NativeTransactionJournalPayloadProvenance Provenance,
        NativeTransactionJournalPrepareInput Prepare);

    private readonly record struct OwnedState(
        HostManagerProcessMemoryTransactionAction Action,
        byte[] Payload,
        NativeTransactionJournalPayloadBinding Binding,
        NativeTransactionJournalPayloadReference Reference,
        NativeAppliedOwnershipRecord Ownership);
}
