using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization;
using ResourceManager.App.Infrastructure.Optimization.Transactions;

namespace Resource_Manager_APP.Tests;

public sealed class HostManagerAppliedOwnershipRuntimeProjectionTests
{
    private const ulong HostSessionIncarnation = 0x401;
    private const ulong JournalInstanceLow = 0x501;
    private const ulong JournalInstanceHigh = 0x502;
    private const ulong PreparedAt = 10_000;
    private const ulong RecoveryDeadline = 20_000;

    [Fact]
    public void PromotePlanTransitionAndRecoveryRemove_ProjectExactDurableEvidence()
    {
        var payload = CreatePayload();
        var snapshot = CreateJournalSnapshot();
        var promoteAction = CreateProcessAction(
            1,
            NativeSmartCoordinatorActionDisposition.Apply,
            NativeSmartCoordinatorProcessGrade.Normal,
            NativeSmartCoordinatorProcessGrade.Level2);
        var promoteRecord = CreateRecord(promoteAction, payload);
        var promoteBinding = CreateBinding(promoteAction);

        var promote = HostManagerAppliedOwnershipProjection.CreatePromote(
            in snapshot,
            in promoteRecord,
            in promoteAction,
            in payload,
            in promoteBinding,
            expectedLedgerRevision: 31,
            promotedAtUtcMilliseconds: promoteRecord.UpdatedAtUtcMilliseconds + 1);

        Assert.Equal(31UL, promote.ExpectedLedgerRevision);
        Assert.Equal(promoteAction.TargetKey, promote.Primary.TargetId);
        Assert.Equal(promoteAction.ProcessStartKey, promote.Primary.ProcessStartKey);
        Assert.Equal(payload.Slot, promote.Payload.Slot);
        Assert.Equal((int)NativeSmartCoordinatorProcessGrade.Level2,
            promote.CurrentGrades.ProcessGrade);
        Assert.Equal(promoteBinding.ActionIdentity.ActionId,
            promote.OriginalBinding.ActionIdentity.ActionId);

        var current = CreateOwnershipRecord(promote, recordRevision: 41);
        var transitionAction = CreateProcessAction(
            2,
            NativeSmartCoordinatorActionDisposition.Apply,
            NativeSmartCoordinatorProcessGrade.Level2,
            NativeSmartCoordinatorProcessGrade.Level3);
        var transitionRecord = CreateRecord(transitionAction, payload);
        transitionRecord.Phase = (uint)NativeTransactionJournalPhase.Prepared;
        var transitionEvidence = HostManagerAppliedOwnershipProjection.CreateTransitionEvidence(
            in snapshot,
            in transitionRecord,
            in transitionAction,
            in payload);
        Assert.Equal(
            transitionAction.ActionId,
            transitionEvidence.Binding.ActionIdentity.ActionId);
        Assert.Equal(payload.DigestHigh, transitionEvidence.Payload.DigestHigh);

        var planned = new NativeAppliedOwnershipTransitionInput
        {
            AbiVersion = NativeAppliedOwnershipAbi.Version,
            StructSize = NativeAppliedOwnershipAbi.TransitionInputSize,
            ExpectedLedgerRevision = 42,
            Cas = new NativeAppliedOwnershipCas
            {
                ExpectedRecordRevision = current.RecordRevision
            },
            NewCurrentGrades = new NativeAppliedOwnershipCurrentGrades
            {
                ValidMask = (uint)NativeAppliedOwnershipGradeValidity.Process,
                ProcessGrade = (int)NativeSmartCoordinatorProcessGrade.Level3
            }
        };
        var completed = HostManagerAppliedOwnershipProjection.CompleteTransition(
            in planned,
            in transitionRecord,
            transitionRecord.UpdatedAtUtcMilliseconds + 1);
        Assert.Equal(
            transitionRecord.UpdatedAtUtcMilliseconds + 1,
            completed.UpdatedAtUtcMilliseconds);

        var remove = HostManagerAppliedOwnershipProjection.CreateRecoveryRemove(
            in current,
            expectedLedgerRevision: 44,
            removedAtUtcMilliseconds: current.UpdatedAtUtcMilliseconds + 1);
        Assert.Equal(44UL, remove.ExpectedLedgerRevision);
        Assert.Equal(current.RecordRevision, remove.Cas.ExpectedRecordRevision);
        Assert.Equal(current.Payload.DigestHigh, remove.Cas.Payload.DigestHigh);
    }

    [Fact]
    public void Promote_RejectsPayloadBindingOrJournalIdentityDrift()
    {
        var payload = CreatePayload();
        var snapshot = CreateJournalSnapshot();
        var action = CreateProcessAction(
            1,
            NativeSmartCoordinatorActionDisposition.Apply,
            NativeSmartCoordinatorProcessGrade.Normal,
            NativeSmartCoordinatorProcessGrade.Level2);
        var record = CreateRecord(action, payload);
        var binding = CreateBinding(action);

        var driftedBinding = binding with
        {
            JournalInstanceHigh = binding.JournalInstanceHigh + 1
        };
        Assert.Throws<ArgumentException>(() =>
            HostManagerAppliedOwnershipProjection.CreatePromote(
                in snapshot,
                in record,
                in action,
                in payload,
                in driftedBinding,
                1,
                record.UpdatedAtUtcMilliseconds + 1));

        record.Identity.ActionId++;
        Assert.Throws<ArgumentException>(() =>
            HostManagerAppliedOwnershipProjection.CreatePromote(
                in snapshot,
                in record,
                in action,
                in payload,
                in binding,
                1,
                record.UpdatedAtUtcMilliseconds + 1));
    }

    private static NativeSmartCoordinatorAction CreateProcessAction(
        ulong actionId,
        NativeSmartCoordinatorActionDisposition disposition,
        NativeSmartCoordinatorProcessGrade from,
        NativeSmartCoordinatorProcessGrade to)
        => new()
        {
            StructSize = NativeSmartCoordinatorSession.SizeOf<NativeSmartCoordinatorAction>(),
            Flags = NativeSmartCoordinatorActionFlags.RequiresFeedback,
            ValidMask = NativeSmartCoordinatorActionValidity.ProcessIdentity |
                NativeSmartCoordinatorActionValidity.ProcessGrade |
                NativeSmartCoordinatorActionValidity.CpuScore,
            ReasonMask = NativeSmartCoordinatorReason.AwaitingStability,
            ActionId = actionId,
            PlanEpoch = 0x202,
            ConfigurationGeneration = 0x303,
            TargetKey = 0x404,
            ProcessStartKey = 0x505,
            CpuScore = 8.25,
            ProcessId = 606,
            Scope = NativeSmartCoordinatorActionScope.ProcessPolicy,
            Disposition = disposition,
            DomainMask = NativeSmartCoordinatorGradeDomains.Process,
            FromProcessGrade = from,
            ToProcessGrade = to,
            FromCpuGrade = NativeSmartCoordinatorAdapterGrade.Normal,
            ToCpuGrade = NativeSmartCoordinatorAdapterGrade.Normal,
            FromGpuGrade = NativeSmartCoordinatorAdapterGrade.Normal,
            ToGpuGrade = NativeSmartCoordinatorAdapterGrade.Normal
        };

    private static NativeSmartCoordinatorAction CreateAdapterAction(
        ulong actionId,
        NativeSmartCoordinatorAdapterGrade fromCpu,
        NativeSmartCoordinatorAdapterGrade toCpu,
        NativeSmartCoordinatorAdapterGrade fromGpu,
        NativeSmartCoordinatorAdapterGrade toGpu)
        => new()
        {
            StructSize = NativeSmartCoordinatorSession.SizeOf<NativeSmartCoordinatorAction>(),
            Flags = NativeSmartCoordinatorActionFlags.RequiresFeedback,
            ValidMask = NativeSmartCoordinatorActionValidity.SoftwareIdentity |
                NativeSmartCoordinatorActionValidity.CpuGrade |
                NativeSmartCoordinatorActionValidity.GpuGrade |
                NativeSmartCoordinatorActionValidity.CpuScore |
                NativeSmartCoordinatorActionValidity.GpuScore,
            ReasonMask = NativeSmartCoordinatorReason.AwaitingStability,
            ActionId = actionId,
            PlanEpoch = 0x202,
            ConfigurationGeneration = 0x303,
            TargetKey = 0x707,
            SoftwareKey = 0x707,
            CpuScore = 8.25,
            GpuScore = 6.5,
            Scope = NativeSmartCoordinatorActionScope.AdapterSoftware,
            Disposition = NativeSmartCoordinatorActionDisposition.Apply,
            DomainMask = NativeSmartCoordinatorGradeDomains.Cpu |
                NativeSmartCoordinatorGradeDomains.Gpu,
            FromProcessGrade = NativeSmartCoordinatorProcessGrade.Normal,
            ToProcessGrade = NativeSmartCoordinatorProcessGrade.Normal,
            FromCpuGrade = fromCpu,
            ToCpuGrade = toCpu,
            FromGpuGrade = fromGpu,
            ToGpuGrade = toGpu
        };

    private static NativeTransactionJournalPayloadReference CreatePayload()
        => new(7, 8, 128, 0x901, 0x902);

    private static NativeTransactionJournalSnapshotHeader CreateJournalSnapshot()
        => new()
        {
            AbiVersion = NativeTransactionJournalAbi.Version,
            StructSize = NativeTransactionJournalSession
                .SizeOf<NativeTransactionJournalSnapshotHeader>(),
            JournalRevision = 11,
            JournalInstanceLow = JournalInstanceLow,
            JournalInstanceHigh = JournalInstanceHigh
        };

    private static NativeTransactionJournalPayloadBinding CreateBinding(
        NativeSmartCoordinatorAction action)
        => HostManagerTransactionJournalProjection.CreatePayloadBinding(
            in action,
            JournalInstanceLow,
            JournalInstanceHigh,
            HostSessionIncarnation,
            PreparedAt,
            maximumRecoveryAttempts: 5,
            RecoveryDeadline);

    private static NativeTransactionJournalRecord CreateRecord(
        NativeSmartCoordinatorAction action,
        NativeTransactionJournalPayloadReference payload)
    {
        var provenance = new NativeTransactionJournalPayloadProvenance(
            DigestLow: 0x9901,
            DigestHigh: 0x9902);
        var prepare = HostManagerTransactionJournalProjection.CreatePrepare(
            in action,
            HostSessionIncarnation,
            expectedJournalRevision: 11,
            in payload,
            in provenance,
            PreparedAt,
            maximumRecoveryAttempts: 5,
            RecoveryDeadline);
        return new NativeTransactionJournalRecord
        {
            Identity = prepare.Identity,
            Scope = prepare.Scope,
            Disposition = prepare.Disposition,
            DomainMask = prepare.DomainMask,
            Phase = (uint)NativeTransactionJournalPhase.EffectObserved,
            GradeValidMask = prepare.GradeValidMask,
            ProcessFromGrade = prepare.ProcessFromGrade,
            ProcessToGrade = prepare.ProcessToGrade,
            CpuFromGrade = prepare.CpuFromGrade,
            CpuToGrade = prepare.CpuToGrade,
            GpuFromGrade = prepare.GpuFromGrade,
            GpuToGrade = prepare.GpuToGrade,
            PayloadKind = prepare.PayloadKind,
            PayloadSlot = prepare.PayloadSlot,
            PayloadGeneration = prepare.PayloadGeneration,
            PayloadLength = prepare.PayloadLength,
            PayloadDigestLow = prepare.PayloadDigestLow,
            PayloadDigestHigh = prepare.PayloadDigestHigh,
            PreparedAtUtcMilliseconds = prepare.NowUtcMilliseconds,
            UpdatedAtUtcMilliseconds = prepare.NowUtcMilliseconds + actionIdOffset(action),
            EntryRevision = action.ActionId + 50,
            MaximumRecoveryAttempts = prepare.MaximumRecoveryAttempts,
            RecoveryDeadlineUtcMilliseconds = prepare.RecoveryDeadlineUtcMilliseconds,
            PayloadProvenanceDigestLow = prepare.PayloadProvenanceDigestLow,
            PayloadProvenanceDigestHigh = prepare.PayloadProvenanceDigestHigh
        };

        static ulong actionIdOffset(NativeSmartCoordinatorAction value)
            => value.ActionId + 1;
    }

    private static NativeAppliedOwnershipRecord CreateOwnershipRecord(
        NativeAppliedOwnershipPromoteInput promote,
        ulong recordRevision)
        => new()
        {
            Primary = promote.Primary,
            OriginalBinding = promote.OriginalBinding,
            Payload = promote.Payload,
            CurrentGrades = promote.CurrentGrades,
            RecordRevision = recordRevision,
            PromotedAtUtcMilliseconds = promote.PromotedAtUtcMilliseconds,
            UpdatedAtUtcMilliseconds = promote.PromotedAtUtcMilliseconds
        };
}
