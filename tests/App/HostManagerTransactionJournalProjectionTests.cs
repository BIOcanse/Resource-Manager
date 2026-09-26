using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization;

namespace Resource_Manager_APP.Tests;

public sealed class HostManagerTransactionJournalProjectionTests
{
    private const ulong HostSessionIncarnation = 0x8801;
    private const ulong JournalRevision = 0x1201;
    private const ulong PreparedAt = 50_000;
    private const ulong RecoveryDeadline = 80_000;
    private const uint MaximumRecoveryAttempts = 7;

    [Fact]
    public void CreatePayloadBinding_UsesTheSameProjectedActionFactsAsPrepare()
    {
        var action = CreateAtomicProcessAction(1, 2);
        var actions = CreateAtomicGroup(2);
        var binding = HostManagerTransactionJournalProjection.CreatePayloadBinding(
            in action,
            journalInstanceLow: 0,
            journalInstanceHigh: 0xCAFE,
            HostSessionIncarnation,
            PreparedAt,
            MaximumRecoveryAttempts,
            RecoveryDeadline);
        var prepared = new NativeTransactionJournalPrepareInput[2];
        var prepare = HostManagerTransactionJournalProjection.CreatePrepareBatch(
            actions,
            HostSessionIncarnation,
            JournalRevision,
            new[] { CreatePayload(1), CreatePayload(2) },
            CreatePayloadProvenances(actions),
            PreparedAt,
            MaximumRecoveryAttempts,
            RecoveryDeadline,
            prepared);

        Assert.True(binding.IsValid);
        Assert.Equal(0UL, binding.JournalInstanceLow);
        Assert.Equal(0xCAFEUL, binding.JournalInstanceHigh);
        Assert.Equal(action.ConfigurationGeneration, binding.ActionIdentity.ConfigurationGeneration);
        Assert.Equal(action.PlanEpoch, binding.ActionIdentity.PlanEpoch);
        Assert.Equal(action.ActionId, binding.ActionIdentity.ActionId);
        Assert.Equal(HostSessionIncarnation, binding.ActionIdentity.HostSessionIncarnation);
        Assert.Equal(action.TargetKey, binding.ActionIdentity.TargetId);
        Assert.Equal(action.SoftwareKey, binding.ActionIdentity.SoftwareId);
        Assert.Equal(action.ProcessStartKey, binding.ActionIdentity.ProcessStartKey);
        Assert.Equal(action.ProcessId, binding.ActionIdentity.ProcessId);
        Assert.Equal(NativeTransactionJournalScope.Process, binding.Scope);
        Assert.Equal(NativeTransactionJournalDisposition.Apply, binding.Disposition);
        Assert.Equal(NativeTransactionJournalDomain.Process, binding.Domain);
        Assert.Equal(NativeTransactionJournalGradeValidity.Process, binding.GradeValidMask);
        Assert.Equal((int)NativeTransactionJournalProcessGrade.Normal, binding.ProcessFromGrade);
        Assert.Equal((int)NativeTransactionJournalProcessGrade.Level4, binding.ProcessToGrade);
        Assert.Equal(action.AtomicGroupId, binding.AtomicGroupId);
        Assert.Equal(1U, binding.GroupMemberIndex);
        Assert.Equal(2U, binding.GroupMemberCount);
        Assert.Equal(prepared[1].Identity.ConfigurationGeneration, binding.ActionIdentity.ConfigurationGeneration);
        Assert.Equal(prepared[1].Identity.PlanEpoch, binding.ActionIdentity.PlanEpoch);
        Assert.Equal(prepared[1].Identity.ActionId, binding.ActionIdentity.ActionId);
        Assert.Equal(prepared[1].Identity.HostSessionIncarnation, binding.ActionIdentity.HostSessionIncarnation);
        Assert.Equal(prepared[1].Identity.TargetId, binding.ActionIdentity.TargetId);
        Assert.Equal(prepared[1].Identity.SoftwareId, binding.ActionIdentity.SoftwareId);
        Assert.Equal(prepared[1].Identity.ProcessStartKey, binding.ActionIdentity.ProcessStartKey);
        Assert.Equal(prepared[1].Identity.ProcessId, binding.ActionIdentity.ProcessId);
        Assert.Equal(prepared[1].Scope, (uint)binding.Scope);
        Assert.Equal(prepared[1].Disposition, (uint)binding.Disposition);
        Assert.Equal(prepared[1].DomainMask, (uint)binding.Domain);
        Assert.Equal(prepared[1].GradeValidMask, (uint)binding.GradeValidMask);
        Assert.Equal(prepared[1].ProcessFromGrade, binding.ProcessFromGrade);
        Assert.Equal(prepared[1].ProcessToGrade, binding.ProcessToGrade);
        Assert.Equal(prepared[1].AtomicGroupId, binding.AtomicGroupId);
        Assert.Equal(prepared[1].GroupMemberIndex, binding.GroupMemberIndex);
        Assert.Equal(prepared[1].GroupMemberCount, binding.GroupMemberCount);
        Assert.Equal(NativeTransactionJournalAbi.Version, prepare.AbiVersion);

        Assert.Throws<ArgumentException>(() =>
            HostManagerTransactionJournalProjection.CreatePayloadBinding(
                in action,
                journalInstanceLow: 0,
                journalInstanceHigh: 0,
                HostSessionIncarnation,
                PreparedAt,
                MaximumRecoveryAttempts,
                RecoveryDeadline));
    }

    [Fact]
    public void CreatePrepare_MapsNonAtomicProcessActionWithoutSoftwareIdentityExactly()
    {
        var action = CreateProcessAction();
        var payload = CreatePayload(1);

        var result = CreatePrepare(action, payload);

        Assert.Equal(NativeTransactionJournalAbi.Version, result.AbiVersion);
        Assert.Equal(
            NativeTransactionJournalSession.SizeOf<NativeTransactionJournalPrepareInput>(),
            result.StructSize);
        Assert.Equal(JournalRevision, result.ExpectedJournalRevision);
        AssertIdentity(action, result.Identity, HostSessionIncarnation);
        Assert.Equal(0UL, result.Identity.SoftwareId);
        Assert.Equal((uint)NativeTransactionJournalScope.Process, result.Scope);
        Assert.Equal((uint)NativeTransactionJournalDisposition.Apply, result.Disposition);
        Assert.Equal((uint)NativeTransactionJournalDomain.Process, result.DomainMask);
        Assert.Equal((uint)NativeTransactionJournalGradeValidity.Process, result.GradeValidMask);
        Assert.Equal((int)NativeTransactionJournalProcessGrade.Normal, result.ProcessFromGrade);
        Assert.Equal((int)NativeTransactionJournalProcessGrade.Level2, result.ProcessToGrade);
        Assert.Equal(0, result.CpuFromGrade);
        Assert.Equal(0, result.CpuToGrade);
        Assert.Equal(0, result.GpuFromGrade);
        Assert.Equal(0, result.GpuToGrade);
        AssertPayload(payload, result);
        Assert.Equal(PreparedAt, result.NowUtcMilliseconds);
        Assert.Equal(MaximumRecoveryAttempts, result.MaximumRecoveryAttempts);
        Assert.Equal(RecoveryDeadline, result.RecoveryDeadlineUtcMilliseconds);
        Assert.Equal(0UL, result.AtomicGroupId);
        Assert.Equal(0U, result.GroupMemberIndex);
        Assert.Equal(0U, result.GroupMemberCount);
    }

    [Fact]
    public void CreatePrepare_RejectsAtomicActionAndInvalidPayloadReferences()
    {
        AssertPrepareRejected(CreateAtomicProcessAction(0, 2));

        var action = CreateProcessAction();
        AssertPrepareRejected(action, new NativeTransactionJournalPayloadReference());
        AssertPrepareRejected(action, CreatePayload(1) with { Slot = 0 });
        AssertPrepareRejected(action, CreatePayload(1) with { Generation = 0 });
        AssertPrepareRejected(action, CreatePayload(1) with { Length = 0 });
        AssertPrepareRejected(action, CreatePayload(1) with { DigestLow = 0 });
        AssertPrepareRejected(action, CreatePayload(1) with { DigestHigh = 0 });
    }

    [Fact]
    public void CreatePrepare_RejectsUnknownBitsAndNonzeroReservedFields()
    {
        var unknownFlags = CreateProcessAction();
        unknownFlags.Flags |= (NativeSmartCoordinatorActionFlags)(1U << 31);
        AssertPrepareRejected(unknownFlags);

        var unknownValidity = CreateProcessAction();
        unknownValidity.ValidMask |= (NativeSmartCoordinatorActionValidity)(1UL << 63);
        AssertPrepareRejected(unknownValidity);

        var unknownReason = CreateProcessAction();
        unknownReason.ReasonMask |= (NativeSmartCoordinatorReason)(1UL << 63);
        AssertPrepareRejected(unknownReason);

        var reservedByte = CreateProcessAction();
        SetPodByte(ref reservedByte, 122, 1);
        AssertPrepareRejected(reservedByte);

    }

    [Fact]
    public void CreatePrepare_RejectsProcessIdentityGradeScopeAndDomainDrift()
    {
        var missingProcessIdentity = CreateProcessAction();
        missingProcessIdentity.ProcessStartKey = 0;
        AssertPrepareRejected(missingProcessIdentity);

        var inconsistentSoftwareIdentity = CreateProcessAction();
        inconsistentSoftwareIdentity.SoftwareKey = 901;
        AssertPrepareRejected(inconsistentSoftwareIdentity);

        var invalidScope = CreateProcessAction();
        invalidScope.Scope = NativeSmartCoordinatorActionScope.AdapterSoftware;
        AssertPrepareRejected(invalidScope);

        var invalidDomain = CreateProcessAction();
        invalidDomain.DomainMask = NativeSmartCoordinatorGradeDomains.Cpu;
        AssertPrepareRejected(invalidDomain);

        var invalidAdapterGrade = CreateProcessAction();
        invalidAdapterGrade.ToCpuGrade = NativeSmartCoordinatorAdapterGrade.Optimize;
        AssertPrepareRejected(invalidAdapterGrade);

        var invalidProcessGrade = CreateProcessAction();
        invalidProcessGrade.ToProcessGrade = (NativeSmartCoordinatorProcessGrade)12;
        AssertPrepareRejected(invalidProcessGrade);
    }

    [Fact]
    public void CreatePrepareBatch_MapsCompleteAtomicLevel4GroupExactly()
    {
        var actions = new[]
        {
            CreateAtomicProcessAction(0, 2),
            CreateAtomicProcessAction(1, 2)
        };
        var payloads = new[] { CreatePayload(1), CreatePayload(2) };
        var prepared = new NativeTransactionJournalPrepareInput[2];

        var batch = HostManagerTransactionJournalProjection.CreatePrepareBatch(
            actions,
            HostSessionIncarnation,
            JournalRevision,
            payloads,
            CreatePayloadProvenances(actions),
            PreparedAt,
            MaximumRecoveryAttempts,
            RecoveryDeadline,
            prepared);

        Assert.Equal(NativeTransactionJournalAbi.Version, batch.AbiVersion);
        Assert.Equal(
            NativeTransactionJournalSession.SizeOf<NativeTransactionJournalPrepareBatchInput>(),
            batch.StructSize);
        Assert.Equal(JournalRevision, batch.ExpectedJournalRevision);
        Assert.Equal(2U, batch.InputCount);
        Assert.Equal(0U, batch.Flags);
        AssertPodBytesAreZero(in batch, 24, 24);

        for (var index = 0; index < prepared.Length; index++)
        {
            AssertIdentity(actions[index], prepared[index].Identity, HostSessionIncarnation);
            AssertPayload(payloads[index], prepared[index]);
            Assert.Equal((uint)NativeTransactionJournalScope.Process, prepared[index].Scope);
            Assert.Equal((uint)NativeTransactionJournalDisposition.Apply, prepared[index].Disposition);
            Assert.Equal((uint)NativeTransactionJournalDomain.Process, prepared[index].DomainMask);
            Assert.Equal((int)NativeTransactionJournalProcessGrade.Level4, prepared[index].ProcessToGrade);
            Assert.Equal(actions[index].AtomicGroupId, prepared[index].AtomicGroupId);
            Assert.Equal((uint)index, prepared[index].GroupMemberIndex);
            Assert.Equal(2U, prepared[index].GroupMemberCount);
        }
    }

    [Fact]
    public void CreatePrepareBatch_RejectsIncompleteOrDriftingAtomicGroups()
    {
        AssertBatchRejected([CreateAtomicProcessAction(0, 2)]);

        var missingMember = CreateAtomicGroup(3);
        AssertBatchRejected([missingMember[0], missingMember[1]]);

        var groupDrift = CreateAtomicGroup(2);
        groupDrift[1].AtomicGroupId++;
        AssertBatchRejected(groupDrift);

        var configurationDrift = CreateAtomicGroup(2);
        configurationDrift[1].ConfigurationGeneration++;
        AssertBatchRejected(configurationDrift);

        var planDrift = CreateAtomicGroup(2);
        planDrift[1].PlanEpoch++;
        AssertBatchRejected(planDrift);

        var softwareDrift = CreateAtomicGroup(2);
        softwareDrift[1].SoftwareKey++;
        AssertBatchRejected(softwareDrift);

        var countDrift = CreateAtomicGroup(2);
        countDrift[1].GroupMemberCount++;
        AssertBatchRejected(countDrift);
    }

    [Fact]
    public void CreatePrepareBatch_RejectsDuplicateOrInvalidMemberShape()
    {
        var duplicateMember = CreateAtomicGroup(2);
        duplicateMember[1].GroupMemberIndex = 0;
        AssertBatchRejected(duplicateMember);

        var outOfRangeMember = CreateAtomicGroup(2);
        outOfRangeMember[1].GroupMemberIndex = 2;
        AssertBatchRejected(outOfRangeMember);

        var missingAtomicValidity = CreateAtomicGroup(2);
        missingAtomicValidity[1].ValidMask &= ~NativeSmartCoordinatorActionValidity.AtomicGroup;
        AssertBatchRejected(missingAtomicValidity);
    }

    [Fact]
    public void CreatePrepare_MapsAdapterCpuAndGpuActionExactly()
    {
        var action = CreateAdapterAction();
        var payload = CreatePayload(3);

        var result = CreatePrepare(action, payload);

        AssertIdentity(action, result.Identity, HostSessionIncarnation);
        Assert.Equal(action.SoftwareKey, result.Identity.TargetId);
        Assert.Equal(action.SoftwareKey, result.Identity.SoftwareId);
        Assert.Equal(0UL, result.Identity.ProcessStartKey);
        Assert.Equal(0U, result.Identity.ProcessId);
        Assert.Equal((uint)NativeTransactionJournalScope.Software, result.Scope);
        Assert.Equal(
            (uint)(NativeTransactionJournalDomain.Cpu | NativeTransactionJournalDomain.Gpu),
            result.DomainMask);
        Assert.Equal(
            (uint)(NativeTransactionJournalGradeValidity.Cpu |
                NativeTransactionJournalGradeValidity.Gpu),
            result.GradeValidMask);
        Assert.Equal((int)NativeTransactionJournalAdapterGrade.Normal, result.CpuFromGrade);
        Assert.Equal((int)NativeTransactionJournalAdapterGrade.Optimize, result.CpuToGrade);
        Assert.Equal((int)NativeTransactionJournalAdapterGrade.Normal, result.GpuFromGrade);
        Assert.Equal((int)NativeTransactionJournalAdapterGrade.Extreme, result.GpuToGrade);
        Assert.Equal(0UL, result.AtomicGroupId);
    }

    [Fact]
    public void CreatePrepare_RejectsInvalidAdapterIdentityProcessFieldsAndAtomicShape()
    {
        var targetDrift = CreateAdapterAction();
        targetDrift.TargetKey++;
        AssertPrepareRejected(targetDrift);

        var processFields = CreateAdapterAction();
        processFields.ProcessId = 55;
        processFields.ProcessStartKey = 66;
        AssertPrepareRejected(processFields);

        var atomic = CreateAdapterAction();
        atomic.Flags |= NativeSmartCoordinatorActionFlags.Atomic;
        atomic.ValidMask |= NativeSmartCoordinatorActionValidity.AtomicGroup;
        atomic.AtomicGroupId = 77;
        atomic.GroupMemberCount = 2;
        AssertPrepareRejected(atomic);
    }

    [Fact]
    public void CreateFeedback_MapsSuccessfulRestoreToNormalWithoutOwnership()
    {
        var action = CreateProcessAction();
        action.Disposition = NativeSmartCoordinatorActionDisposition.Restore;
        action.FromProcessGrade = NativeSmartCoordinatorProcessGrade.Level2;
        action.ToProcessGrade = NativeSmartCoordinatorProcessGrade.Normal;
        var record = CreateRecord(CreatePrepare(action, CreatePayload(4)));
        var feedback = CreateProcessFeedback(
            record,
            NativeSmartCoordinatorFeedbackStatus.Succeeded,
            NativeSmartCoordinatorProcessGrade.Normal,
            NativeSmartCoordinatorFeedbackFlags.None,
            NativeSmartCoordinatorFeedbackValidity.CompletedAt |
                NativeSmartCoordinatorFeedbackValidity.ActualProcessGrade);

        var result = HostManagerTransactionJournalProjection.CreateFeedback(
            in record,
            JournalRevision + 1,
            HostSessionIncarnation,
            in feedback);

        Assert.Equal(record.Identity.ConfigurationGeneration, result.Identity.ConfigurationGeneration);
        Assert.Equal(record.Identity.ActionId, result.Identity.ActionId);
        Assert.Equal(record.EntryRevision, result.ExpectedEntryRevision);
        Assert.Equal((uint)NativeTransactionJournalPhase.EffectObserved, result.ExpectedPhase);
        Assert.Equal(
            (uint)(NativeTransactionJournalFeedbackValidity.CompletedAt |
                NativeTransactionJournalFeedbackValidity.ActualProcessGrade),
            result.FeedbackValidMask);
        Assert.Equal((uint)NativeTransactionJournalFeedbackFlags.None, result.FeedbackFlags);
        Assert.Equal((int)NativeTransactionJournalProcessGrade.Normal, result.ActualProcessGrade);
    }

    [Fact]
    public void CreateFeedback_AcceptsOwnershipLostOnlyWithoutGradeOrOwnershipClaims()
    {
        var action = CreateProcessAction();
        var record = CreateRecord(CreatePrepare(action, CreatePayload(5)));
        var feedback = CreateProcessFeedback(
            record,
            NativeSmartCoordinatorFeedbackStatus.OwnershipLost,
            NativeSmartCoordinatorProcessGrade.Normal,
            NativeSmartCoordinatorFeedbackFlags.None,
            NativeSmartCoordinatorFeedbackValidity.CompletedAt);

        var result = HostManagerTransactionJournalProjection.CreateFeedback(
            in record,
            JournalRevision + 1,
            HostSessionIncarnation,
            in feedback);

        Assert.Equal((uint)NativeTransactionJournalFeedbackStatus.OwnershipLost, result.FeedbackStatus);
        Assert.Equal((uint)NativeTransactionJournalFeedbackValidity.CompletedAt, result.FeedbackValidMask);
        Assert.Equal((uint)NativeTransactionJournalFeedbackFlags.None, result.FeedbackFlags);
        Assert.Equal(0, result.ActualProcessGrade);
    }

    [Fact]
    public void CreateFeedback_RejectsHostPhaseIdentityShapeAndUnknownBits()
    {
        var record = CreateRecord(CreatePrepare(CreateProcessAction(), CreatePayload(6)));
        var feedback = CreateProcessFeedback(record);

        Assert.Throws<ArgumentException>(() =>
            HostManagerTransactionJournalProjection.CreateFeedback(
                in record,
                JournalRevision + 1,
                HostSessionIncarnation + 1,
                in feedback));

        var wrongPhase = record;
        wrongPhase.Phase = (uint)NativeTransactionJournalPhase.Prepared;
        AssertFeedbackRejected(wrongPhase, feedback);

        var wrongIdentity = feedback;
        wrongIdentity.ActionId++;
        AssertFeedbackRejected(record, wrongIdentity);

        var wrongScope = feedback;
        wrongScope.Scope = NativeSmartCoordinatorActionScope.AdapterSoftware;
        AssertFeedbackRejected(record, wrongScope);

        var unknownValidity = feedback;
        unknownValidity.ValidMask |= (NativeSmartCoordinatorFeedbackValidity)(1UL << 63);
        AssertFeedbackRejected(record, unknownValidity);

        var unknownFlags = feedback;
        unknownFlags.Flags |= (NativeSmartCoordinatorFeedbackFlags)(1U << 31);
        AssertFeedbackRejected(record, unknownFlags);

        var reserved = feedback;
        SetPodByte(ref reserved, 85, 1);
        AssertFeedbackRejected(record, reserved);
    }

    [Fact]
    public void CreateFeedback_DoesNotInventRollbackPayloadProof()
    {
        var record = CreateRecord(CreatePrepare(CreateProcessAction(), CreatePayload(7)));
        var feedback = CreateProcessFeedback(record);
        feedback.Flags &= ~NativeSmartCoordinatorFeedbackFlags.RollbackPayloadPersisted;

        var result = HostManagerTransactionJournalProjection.CreateFeedback(
            in record,
            JournalRevision + 1,
            HostSessionIncarnation,
            in feedback);

        Assert.Equal((uint)NativeTransactionJournalFeedbackFlags.ProcessOwned, result.FeedbackFlags);
    }

    [Fact]
    public void CreateMutation_MapsExactIdentityAndEntryRevision()
    {
        var prepare = CreatePrepare(CreateProcessAction(), CreatePayload(8));
        var record = CreateRecord(prepare, NativeTransactionJournalPhase.Prepared);

        var result = HostManagerTransactionJournalProjection.CreateMutation(
            in record,
            JournalRevision + 4,
            NativeTransactionJournalMutationEvent.ConfirmPreviousEffectRestored,
            record.UpdatedAtUtcMilliseconds + 1,
            0x44,
            0x55);

        AssertIdentity(record.Identity, result.Identity);
        Assert.Equal(JournalRevision + 4, result.ExpectedJournalRevision);
        Assert.Equal(record.EntryRevision, result.ExpectedEntryRevision);
        Assert.Equal(record.Phase, result.ExpectedPhase);
        Assert.Equal(
            (uint)NativeTransactionJournalMutationEvent.ConfirmPreviousEffectRestored,
            result.Event);
        Assert.Equal(0x44U, result.StableSystemStatus);
        Assert.Equal(0x55U, result.StableSystemError);

        var missingRevision = record;
        missingRevision.EntryRevision = 0;
        Assert.Throws<ArgumentException>(() =>
            HostManagerTransactionJournalProjection.CreateMutation(
                in missingRevision,
                JournalRevision + 4,
                NativeTransactionJournalMutationEvent.ConfirmPreviousEffectRestored,
                PreparedAt + 1,
                0,
                0));
    }

    private static NativeSmartCoordinatorAction CreateProcessAction()
        => new()
        {
            StructSize = NativeSmartCoordinatorSession.SizeOf<NativeSmartCoordinatorAction>(),
            Flags = NativeSmartCoordinatorActionFlags.RequiresFeedback,
            ValidMask = NativeSmartCoordinatorActionValidity.ProcessIdentity |
                NativeSmartCoordinatorActionValidity.ProcessGrade |
                NativeSmartCoordinatorActionValidity.CpuScore,
            ReasonMask = NativeSmartCoordinatorReason.AwaitingStability,
            ActionId = 0x101,
            PlanEpoch = 0x202,
            ConfigurationGeneration = 0x303,
            TargetKey = 0x404,
            SoftwareKey = 0,
            ProcessStartKey = 0x505,
            CpuScore = 8.25,
            ProcessId = 606,
            Scope = NativeSmartCoordinatorActionScope.ProcessPolicy,
            Disposition = NativeSmartCoordinatorActionDisposition.Apply,
            DomainMask = NativeSmartCoordinatorGradeDomains.Process,
            FromProcessGrade = NativeSmartCoordinatorProcessGrade.Normal,
            ToProcessGrade = NativeSmartCoordinatorProcessGrade.Level2,
            FromCpuGrade = NativeSmartCoordinatorAdapterGrade.Normal,
            ToCpuGrade = NativeSmartCoordinatorAdapterGrade.Normal,
            FromGpuGrade = NativeSmartCoordinatorAdapterGrade.Normal,
            ToGpuGrade = NativeSmartCoordinatorAdapterGrade.Normal
        };

    private static NativeSmartCoordinatorAction CreateAtomicProcessAction(
        uint memberIndex,
        uint memberCount)
    {
        var action = CreateProcessAction();
        action.Flags |= NativeSmartCoordinatorActionFlags.Atomic;
        action.ValidMask |= NativeSmartCoordinatorActionValidity.SoftwareIdentity |
            NativeSmartCoordinatorActionValidity.AtomicGroup;
        action.ActionId += memberIndex;
        action.TargetKey += memberIndex;
        action.SoftwareKey = 0x909;
        action.ToProcessGrade = NativeSmartCoordinatorProcessGrade.Level4;
        action.AtomicGroupId = 0xA0A;
        action.GroupMemberIndex = memberIndex;
        action.GroupMemberCount = memberCount;
        return action;
    }

    private static NativeSmartCoordinatorAction[] CreateAtomicGroup(uint memberCount)
        => Enumerable.Range(0, checked((int)memberCount))
            .Select(index => CreateAtomicProcessAction(checked((uint)index), memberCount))
            .ToArray();

    private static NativeSmartCoordinatorAction CreateAdapterAction()
        => new()
        {
            StructSize = NativeSmartCoordinatorSession.SizeOf<NativeSmartCoordinatorAction>(),
            Flags = NativeSmartCoordinatorActionFlags.RequiresFeedback,
            ValidMask = NativeSmartCoordinatorActionValidity.SoftwareIdentity |
                NativeSmartCoordinatorActionValidity.CpuGrade |
                NativeSmartCoordinatorActionValidity.GpuGrade |
                NativeSmartCoordinatorActionValidity.CpuScore |
                NativeSmartCoordinatorActionValidity.GpuScore,
            ReasonMask = NativeSmartCoordinatorReason.CriticalFactChanged,
            ActionId = 0x111,
            PlanEpoch = 0x222,
            ConfigurationGeneration = 0x333,
            TargetKey = 0x444,
            SoftwareKey = 0x444,
            CpuScore = 9.5,
            GpuScore = 7.25,
            Scope = NativeSmartCoordinatorActionScope.AdapterSoftware,
            Disposition = NativeSmartCoordinatorActionDisposition.Apply,
            DomainMask = NativeSmartCoordinatorGradeDomains.Cpu |
                NativeSmartCoordinatorGradeDomains.Gpu,
            FromProcessGrade = NativeSmartCoordinatorProcessGrade.Normal,
            ToProcessGrade = NativeSmartCoordinatorProcessGrade.Normal,
            FromCpuGrade = NativeSmartCoordinatorAdapterGrade.Normal,
            ToCpuGrade = NativeSmartCoordinatorAdapterGrade.Optimize,
            FromGpuGrade = NativeSmartCoordinatorAdapterGrade.Normal,
            ToGpuGrade = NativeSmartCoordinatorAdapterGrade.Extreme
        };

    private static NativeTransactionJournalPrepareInput CreatePrepare(
        NativeSmartCoordinatorAction action,
        NativeTransactionJournalPayloadReference payload)
    {
        var provenance = CreatePayloadProvenance(action);
        return HostManagerTransactionJournalProjection.CreatePrepare(
            in action,
            HostSessionIncarnation,
            JournalRevision,
            in payload,
            in provenance,
            PreparedAt,
            MaximumRecoveryAttempts,
            RecoveryDeadline);
    }

    private static NativeTransactionJournalPayloadProvenance CreatePayloadProvenance(
        NativeSmartCoordinatorAction action)
    {
        var binding = HostManagerTransactionJournalProjection.CreatePayloadBinding(
            in action,
            journalInstanceLow: 0,
            journalInstanceHigh: 0xCAFE,
            HostSessionIncarnation,
            PreparedAt,
            MaximumRecoveryAttempts,
            RecoveryDeadline);
        return NativeTransactionJournalPayloadProvenance.Create(binding);
    }

    private static NativeTransactionJournalPayloadProvenance[] CreatePayloadProvenances(
        NativeSmartCoordinatorAction[] actions)
        => actions.Select(CreatePayloadProvenance).ToArray();

    private static NativeTransactionJournalPayloadReference CreatePayload(uint slot)
        => new(
            slot,
            slot + 10,
            128UL + slot,
            0x1000UL + slot,
            0x2000UL + slot);

    private static NativeTransactionJournalRecord CreateRecord(
        NativeTransactionJournalPrepareInput prepare,
        NativeTransactionJournalPhase phase = NativeTransactionJournalPhase.EffectObserved)
        => new()
        {
            Identity = prepare.Identity,
            Scope = prepare.Scope,
            Disposition = prepare.Disposition,
            DomainMask = prepare.DomainMask,
            Phase = (uint)phase,
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
            UpdatedAtUtcMilliseconds = prepare.NowUtcMilliseconds + 5,
            EntryRevision = 0x707,
            MaximumRecoveryAttempts = prepare.MaximumRecoveryAttempts,
            RecoveryDeadlineUtcMilliseconds = prepare.RecoveryDeadlineUtcMilliseconds,
            AtomicGroupId = prepare.AtomicGroupId,
            GroupMemberIndex = prepare.GroupMemberIndex,
            GroupMemberCount = prepare.GroupMemberCount,
            PayloadProvenanceDigestLow = prepare.PayloadProvenanceDigestLow,
            PayloadProvenanceDigestHigh = prepare.PayloadProvenanceDigestHigh
        };

    private static NativeSmartCoordinatorFeedback CreateProcessFeedback(
        NativeTransactionJournalRecord record,
        NativeSmartCoordinatorFeedbackStatus status = NativeSmartCoordinatorFeedbackStatus.Succeeded,
        NativeSmartCoordinatorProcessGrade actualGrade = NativeSmartCoordinatorProcessGrade.Level2,
        NativeSmartCoordinatorFeedbackFlags flags =
            NativeSmartCoordinatorFeedbackFlags.ProcessOwned |
            NativeSmartCoordinatorFeedbackFlags.RollbackPayloadPersisted,
        NativeSmartCoordinatorFeedbackValidity validMask =
            NativeSmartCoordinatorFeedbackValidity.CompletedAt |
            NativeSmartCoordinatorFeedbackValidity.ActualProcessGrade)
        => new()
        {
            StructSize = NativeSmartCoordinatorSession.SizeOf<NativeSmartCoordinatorFeedback>(),
            Flags = flags,
            ValidMask = validMask,
            ActionId = record.Identity.ActionId,
            PlanEpoch = record.Identity.PlanEpoch,
            ConfigurationGeneration = record.Identity.ConfigurationGeneration,
            TargetKey = record.Identity.TargetId,
            SoftwareKey = record.Identity.SoftwareId,
            ProcessStartKey = record.Identity.ProcessStartKey,
            CompletedAtMilliseconds = checked((long)record.UpdatedAtUtcMilliseconds + 1),
            ProcessId = record.Identity.ProcessId,
            Status = status,
            Scope = NativeSmartCoordinatorActionScope.ProcessPolicy,
            ActualProcessGrade = actualGrade
        };

    private static void AssertPrepareRejected(
        NativeSmartCoordinatorAction action,
        NativeTransactionJournalPayloadReference? payload = null)
    {
        var exactPayload = payload ?? CreatePayload(99);
        Assert.ThrowsAny<ArgumentException>(() => CreatePrepare(action, exactPayload));
    }

    private static void AssertBatchRejected(NativeSmartCoordinatorAction[] actions)
    {
        var payloads = Enumerable.Range(1, actions.Length)
            .Select(index => CreatePayload(checked((uint)index)))
            .ToArray();
        var prepared = new NativeTransactionJournalPrepareInput[Math.Max(1, actions.Length)];
        Assert.Throws<ArgumentException>(() =>
            HostManagerTransactionJournalProjection.CreatePrepareBatch(
                actions,
                HostSessionIncarnation,
                JournalRevision,
                payloads,
                CreatePayloadProvenances(actions),
                PreparedAt,
                MaximumRecoveryAttempts,
                RecoveryDeadline,
                prepared));
    }

    private static void AssertFeedbackRejected(
        NativeTransactionJournalRecord record,
        NativeSmartCoordinatorFeedback feedback)
        => Assert.ThrowsAny<ArgumentException>(() =>
            HostManagerTransactionJournalProjection.CreateFeedback(
                in record,
                JournalRevision + 1,
                HostSessionIncarnation,
                in feedback));

    private static void AssertIdentity(
        NativeSmartCoordinatorAction action,
        NativeTransactionJournalIdentity identity,
        ulong hostSessionIncarnation)
    {
        Assert.Equal(action.ConfigurationGeneration, identity.ConfigurationGeneration);
        Assert.Equal(action.PlanEpoch, identity.PlanEpoch);
        Assert.Equal(action.ActionId, identity.ActionId);
        Assert.Equal(hostSessionIncarnation, identity.HostSessionIncarnation);
        Assert.Equal(action.TargetKey, identity.TargetId);
        Assert.Equal(action.SoftwareKey, identity.SoftwareId);
        Assert.Equal(action.ProcessStartKey, identity.ProcessStartKey);
        Assert.Equal(action.ProcessId, identity.ProcessId);
    }

    private static void AssertIdentity(
        NativeTransactionJournalIdentity expected,
        NativeTransactionJournalIdentity actual)
    {
        Assert.Equal(expected.ConfigurationGeneration, actual.ConfigurationGeneration);
        Assert.Equal(expected.PlanEpoch, actual.PlanEpoch);
        Assert.Equal(expected.ActionId, actual.ActionId);
        Assert.Equal(expected.HostSessionIncarnation, actual.HostSessionIncarnation);
        Assert.Equal(expected.TargetId, actual.TargetId);
        Assert.Equal(expected.SoftwareId, actual.SoftwareId);
        Assert.Equal(expected.ProcessStartKey, actual.ProcessStartKey);
        Assert.Equal(expected.ProcessId, actual.ProcessId);
    }

    private static void AssertPayload(
        NativeTransactionJournalPayloadReference expected,
        NativeTransactionJournalPrepareInput actual)
    {
        Assert.Equal((uint)NativeTransactionJournalPayloadKind.Durable, actual.PayloadKind);
        Assert.Equal(expected.Slot, actual.PayloadSlot);
        Assert.Equal(expected.Generation, actual.PayloadGeneration);
        Assert.Equal(expected.Length, actual.PayloadLength);
        Assert.Equal(expected.DigestLow, actual.PayloadDigestLow);
        Assert.Equal(expected.DigestHigh, actual.PayloadDigestHigh);
    }

    private static void SetPodByte<T>(ref T value, int offset, byte replacement)
        where T : struct
    {
        var bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref value, 1));
        Assert.InRange(offset, 0, bytes.Length - 1);
        bytes[offset] = replacement;
    }

    private static void AssertPodBytesAreZero<T>(
        in T value,
        int offset,
        int length)
        where T : struct
    {
        ref var mutable = ref Unsafe.AsRef(in value);
        var bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref mutable, 1));
        Assert.All(bytes.Slice(offset, length).ToArray(), item => Assert.Equal(0, item));
    }
}
