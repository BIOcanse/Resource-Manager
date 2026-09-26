using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ResourceManager.App.Infrastructure.NativeCore;

namespace Resource_Manager_APP.Tests;

public sealed class NativeTransactionJournalAbiTests
{
    private const ulong ResidentBudget = 16UL * 1024 * 1024;

    [Fact]
    public void AbiV5UsesPublishedPodSizesAndOffsets()
    {
        Assert.Equal(0x0005_0000U, NativeTransactionJournalAbi.Version);
        Assert.Equal(128UL, NativeTransactionJournalAbi.ImageHeaderSize);

        Assert.Equal(64, Unsafe.SizeOf<NativeTransactionJournalCreateConfiguration>());
        Assert.Equal(32, Unsafe.SizeOf<NativeTransactionJournalOpenConfiguration>());
        Assert.Equal(32, Unsafe.SizeOf<NativeTransactionJournalCapacity>());
        Assert.Equal(64, Unsafe.SizeOf<NativeTransactionJournalIdentity>());
        Assert.Equal(224, Unsafe.SizeOf<NativeTransactionJournalPrepareInput>());
        Assert.Equal(48, Unsafe.SizeOf<NativeTransactionJournalPrepareBatchInput>());
        Assert.Equal(160, Unsafe.SizeOf<NativeTransactionJournalMutationInput>());
        Assert.Equal(176, Unsafe.SizeOf<NativeTransactionJournalStageFeedbackInput>());
        Assert.Equal(160, Unsafe.SizeOf<NativeTransactionJournalRecoveryEvidenceInput>());
        Assert.Equal(160, Unsafe.SizeOf<NativeTransactionJournalAckInput>());
        Assert.Equal(296, Unsafe.SizeOf<NativeTransactionJournalRecord>());
        Assert.Equal(128, Unsafe.SizeOf<NativeTransactionJournalSnapshotHeader>());

        AssertOffset<NativeTransactionJournalCreateConfiguration>(nameof(NativeTransactionJournalCreateConfiguration.JournalInstanceLow), 8);
        AssertOffset<NativeTransactionJournalCreateConfiguration>(nameof(NativeTransactionJournalCreateConfiguration.RecordCapacity), 24);
        AssertOffset<NativeTransactionJournalCreateConfiguration>(nameof(NativeTransactionJournalCreateConfiguration.MaximumResidentBytes), 32);
        AssertOffset<NativeTransactionJournalCreateConfiguration>(nameof(NativeTransactionJournalCreateConfiguration.Reserved), 40);
        AssertOffset<NativeTransactionJournalOpenConfiguration>(nameof(NativeTransactionJournalOpenConfiguration.MaximumRecordCapacity), 8);
        AssertOffset<NativeTransactionJournalOpenConfiguration>(nameof(NativeTransactionJournalOpenConfiguration.MaximumResidentBytes), 16);
        AssertOffset<NativeTransactionJournalOpenConfiguration>(nameof(NativeTransactionJournalOpenConfiguration.Reserved), 24);
        AssertOffset<NativeTransactionJournalCapacity>(nameof(NativeTransactionJournalCapacity.MaximumImageLength), 8);
        AssertOffset<NativeTransactionJournalCapacity>(nameof(NativeTransactionJournalCapacity.ResidentBytes), 16);
        AssertOffset<NativeTransactionJournalCapacity>(nameof(NativeTransactionJournalCapacity.Reserved), 24);

        AssertOffset<NativeTransactionJournalIdentity>(nameof(NativeTransactionJournalIdentity.PlanEpoch), 8);
        AssertOffset<NativeTransactionJournalIdentity>(nameof(NativeTransactionJournalIdentity.HostSessionIncarnation), 24);
        AssertOffset<NativeTransactionJournalIdentity>(nameof(NativeTransactionJournalIdentity.ProcessStartKey), 48);
        AssertOffset<NativeTransactionJournalIdentity>(nameof(NativeTransactionJournalIdentity.ProcessId), 56);

        AssertOffset<NativeTransactionJournalPrepareInput>(nameof(NativeTransactionJournalPrepareInput.ExpectedJournalRevision), 8);
        AssertOffset<NativeTransactionJournalPrepareInput>(nameof(NativeTransactionJournalPrepareInput.Identity), 16);
        AssertOffset<NativeTransactionJournalPrepareInput>(nameof(NativeTransactionJournalPrepareInput.DomainMask), 88);
        AssertOffset<NativeTransactionJournalPrepareInput>(nameof(NativeTransactionJournalPrepareInput.PayloadKind), 128);
        AssertOffset<NativeTransactionJournalPrepareInput>(nameof(NativeTransactionJournalPrepareInput.PayloadLength), 144);
        AssertOffset<NativeTransactionJournalPrepareInput>(nameof(NativeTransactionJournalPrepareInput.NowUtcMilliseconds), 168);
        AssertOffset<NativeTransactionJournalPrepareInput>(nameof(NativeTransactionJournalPrepareInput.MaximumRecoveryAttempts), 176);
        AssertOffset<NativeTransactionJournalPrepareInput>(nameof(NativeTransactionJournalPrepareInput.RetryPolicyReserved), 180);
        AssertOffset<NativeTransactionJournalPrepareInput>(nameof(NativeTransactionJournalPrepareInput.RecoveryDeadlineUtcMilliseconds), 184);
        AssertOffset<NativeTransactionJournalPrepareInput>(nameof(NativeTransactionJournalPrepareInput.AtomicGroupId), 192);
        AssertOffset<NativeTransactionJournalPrepareInput>(nameof(NativeTransactionJournalPrepareInput.GroupMemberIndex), 200);
        AssertOffset<NativeTransactionJournalPrepareInput>(nameof(NativeTransactionJournalPrepareInput.GroupMemberCount), 204);
        AssertOffset<NativeTransactionJournalPrepareInput>(
            nameof(NativeTransactionJournalPrepareInput.PayloadProvenanceDigestLow),
            208);
        AssertOffset<NativeTransactionJournalPrepareInput>(
            nameof(NativeTransactionJournalPrepareInput.PayloadProvenanceDigestHigh),
            216);

        AssertOffset<NativeTransactionJournalPrepareBatchInput>(nameof(NativeTransactionJournalPrepareBatchInput.ExpectedJournalRevision), 8);
        AssertOffset<NativeTransactionJournalPrepareBatchInput>(nameof(NativeTransactionJournalPrepareBatchInput.InputCount), 16);
        AssertOffset<NativeTransactionJournalPrepareBatchInput>(nameof(NativeTransactionJournalPrepareBatchInput.Flags), 20);
        AssertOffset<NativeTransactionJournalPrepareBatchInput>(nameof(NativeTransactionJournalPrepareBatchInput.Reserved), 24);

        AssertOffset<NativeTransactionJournalMutationInput>(nameof(NativeTransactionJournalMutationInput.Identity), 8);
        AssertOffset<NativeTransactionJournalMutationInput>(nameof(NativeTransactionJournalMutationInput.ExpectedJournalRevision), 72);
        AssertOffset<NativeTransactionJournalMutationInput>(nameof(NativeTransactionJournalMutationInput.NowUtcMilliseconds), 88);
        AssertOffset<NativeTransactionJournalMutationInput>(nameof(NativeTransactionJournalMutationInput.Event), 96);
        AssertOffset<NativeTransactionJournalMutationInput>(nameof(NativeTransactionJournalMutationInput.Reserved), 112);

        AssertOffset<NativeTransactionJournalStageFeedbackInput>(nameof(NativeTransactionJournalStageFeedbackInput.Identity), 8);
        AssertOffset<NativeTransactionJournalStageFeedbackInput>(nameof(NativeTransactionJournalStageFeedbackInput.ExpectedJournalRevision), 72);
        AssertOffset<NativeTransactionJournalStageFeedbackInput>(nameof(NativeTransactionJournalStageFeedbackInput.CompletedAtUtcMilliseconds), 88);
        AssertOffset<NativeTransactionJournalStageFeedbackInput>(nameof(NativeTransactionJournalStageFeedbackInput.FeedbackValidMask), 100);
        AssertOffset<NativeTransactionJournalStageFeedbackInput>(nameof(NativeTransactionJournalStageFeedbackInput.ActualProcessGrade), 124);
        AssertOffset<NativeTransactionJournalStageFeedbackInput>(nameof(NativeTransactionJournalStageFeedbackInput.Reserved), 144);

        AssertOffset<NativeTransactionJournalRecoveryEvidenceInput>(nameof(NativeTransactionJournalRecoveryEvidenceInput.Identity), 8);
        AssertOffset<NativeTransactionJournalRecoveryEvidenceInput>(nameof(NativeTransactionJournalRecoveryEvidenceInput.ExpectedJournalRevision), 72);
        AssertOffset<NativeTransactionJournalRecoveryEvidenceInput>(nameof(NativeTransactionJournalRecoveryEvidenceInput.ObservedAtUtcMilliseconds), 88);
        AssertOffset<NativeTransactionJournalRecoveryEvidenceInput>(nameof(NativeTransactionJournalRecoveryEvidenceInput.Outcome), 104);
        AssertOffset<NativeTransactionJournalRecoveryEvidenceInput>(nameof(NativeTransactionJournalRecoveryEvidenceInput.AuthoritativeFactsGeneration), 128);
        AssertOffset<NativeTransactionJournalRecoveryEvidenceInput>(nameof(NativeTransactionJournalRecoveryEvidenceInput.Reserved), 136);

        AssertOffset<NativeTransactionJournalAckInput>(nameof(NativeTransactionJournalAckInput.Identity), 8);
        AssertOffset<NativeTransactionJournalAckInput>(nameof(NativeTransactionJournalAckInput.ExpectedJournalRevision), 72);
        AssertOffset<NativeTransactionJournalAckInput>(nameof(NativeTransactionJournalAckInput.AcknowledgedAtUtcMilliseconds), 88);
        AssertOffset<NativeTransactionJournalAckInput>(nameof(NativeTransactionJournalAckInput.Result), 104);
        AssertOffset<NativeTransactionJournalAckInput>(nameof(NativeTransactionJournalAckInput.Reserved), 128);

        AssertOffset<NativeTransactionJournalRecord>(nameof(NativeTransactionJournalRecord.Scope), 64);
        AssertOffset<NativeTransactionJournalRecord>(nameof(NativeTransactionJournalRecord.Phase), 76);
        AssertOffset<NativeTransactionJournalRecord>(nameof(NativeTransactionJournalRecord.PayloadKind), 120);
        AssertOffset<NativeTransactionJournalRecord>(nameof(NativeTransactionJournalRecord.PreparedAtUtcMilliseconds), 160);
        AssertOffset<NativeTransactionJournalRecord>(nameof(NativeTransactionJournalRecord.EntryRevision), 184);
        AssertOffset<NativeTransactionJournalRecord>(nameof(NativeTransactionJournalRecord.FeedbackValidMask), 192);
        AssertOffset<NativeTransactionJournalRecord>(nameof(NativeTransactionJournalRecord.FeedbackCompletedAtUtcMilliseconds), 216);
        AssertOffset<NativeTransactionJournalRecord>(nameof(NativeTransactionJournalRecord.RecoveryReasonMask), 240);
        AssertOffset<NativeTransactionJournalRecord>(nameof(NativeTransactionJournalRecord.RetryAttemptCount), 248);
        AssertOffset<NativeTransactionJournalRecord>(nameof(NativeTransactionJournalRecord.MaximumRecoveryAttempts), 252);
        AssertOffset<NativeTransactionJournalRecord>(nameof(NativeTransactionJournalRecord.RecoveryDeadlineUtcMilliseconds), 256);
        AssertOffset<NativeTransactionJournalRecord>(nameof(NativeTransactionJournalRecord.AtomicGroupId), 264);
        AssertOffset<NativeTransactionJournalRecord>(nameof(NativeTransactionJournalRecord.GroupMemberIndex), 272);
        AssertOffset<NativeTransactionJournalRecord>(nameof(NativeTransactionJournalRecord.GroupMemberCount), 276);
        AssertOffset<NativeTransactionJournalRecord>(
            nameof(NativeTransactionJournalRecord.PayloadProvenanceDigestLow),
            280);
        AssertOffset<NativeTransactionJournalRecord>(
            nameof(NativeTransactionJournalRecord.PayloadProvenanceDigestHigh),
            288);

        AssertOffset<NativeTransactionJournalSnapshotHeader>(nameof(NativeTransactionJournalSnapshotHeader.JournalRevision), 8);
        AssertOffset<NativeTransactionJournalSnapshotHeader>(nameof(NativeTransactionJournalSnapshotHeader.EntryCount), 32);
        AssertOffset<NativeTransactionJournalSnapshotHeader>(nameof(NativeTransactionJournalSnapshotHeader.PreparedCount), 40);
        AssertOffset<NativeTransactionJournalSnapshotHeader>(nameof(NativeTransactionJournalSnapshotHeader.ReconciliationPendingCount), 56);
        AssertOffset<NativeTransactionJournalSnapshotHeader>(nameof(NativeTransactionJournalSnapshotHeader.EffectInvocationUncertainCount), 72);
        AssertOffset<NativeTransactionJournalSnapshotHeader>(nameof(NativeTransactionJournalSnapshotHeader.Reserved), 80);
    }

    [Fact]
    public void RealDllAtomicPrepareBatchCommitsOnceAndRejectsInvalidGroupWithoutMutation()
    {
        var configuration = CreateConfiguration(recordCapacity: 4, ResidentBudget);
        using var session = new NativeTransactionJournalSession(in configuration);
        var first = CreatePrepare(
            CreateIdentity() with { ActionId = 101, ProcessStartKey = 1_001, ProcessId = 101 },
            expectedJournalRevision: 1,
            nowUtcMilliseconds: 100);
        first.AtomicGroupId = 77;
        first.GroupMemberIndex = 0;
        first.GroupMemberCount = 2;
        first.ProcessToGrade = (int)NativeTransactionJournalProcessGrade.Level4;
        var second = CreatePrepare(
            CreateIdentity() with { ActionId = 102, ProcessStartKey = 1_002, ProcessId = 102 },
            expectedJournalRevision: 1,
            nowUtcMilliseconds: 100);
        second.AtomicGroupId = 77;
        second.GroupMemberIndex = 1;
        second.GroupMemberCount = 2;
        second.ProcessToGrade = (int)NativeTransactionJournalProcessGrade.Level4;
        var validBatch = CreatePrepareBatch(expectedJournalRevision: 1, inputCount: 2);

        Assert.Equal(
            NativeTransactionJournalStatus.Ok,
            session.PrepareBatch(in validBatch, [first, second]));
        var committed = ReadSnapshot(session);
        Assert.Equal(2UL, committed.Header.JournalRevision);
        Assert.Equal(2U, committed.Header.EntryCount);
        Assert.All(committed.Records, record => Assert.Equal(77UL, record.AtomicGroupId));
        Assert.Equal([0U, 1U], committed.Records.Select(static record => record.GroupMemberIndex).Order().ToArray());

        var duplicateFirst = CreatePrepare(
            CreateIdentity() with { ActionId = 103, ProcessStartKey = 1_003, ProcessId = 103 },
            expectedJournalRevision: 2,
            nowUtcMilliseconds: 200);
        duplicateFirst.AtomicGroupId = 88;
        duplicateFirst.GroupMemberIndex = 0;
        duplicateFirst.GroupMemberCount = 2;
        duplicateFirst.ProcessToGrade = (int)NativeTransactionJournalProcessGrade.Level4;
        var duplicateSecond = CreatePrepare(
            CreateIdentity() with { ActionId = 104, ProcessStartKey = 1_004, ProcessId = 104 },
            expectedJournalRevision: 2,
            nowUtcMilliseconds: 200);
        duplicateSecond.AtomicGroupId = 88;
        duplicateSecond.GroupMemberIndex = 0;
        duplicateSecond.GroupMemberCount = 2;
        duplicateSecond.ProcessToGrade = (int)NativeTransactionJournalProcessGrade.Level4;
        var invalidBatch = CreatePrepareBatch(expectedJournalRevision: 2, inputCount: 2);

        Assert.Equal(
            NativeTransactionJournalStatus.DuplicateIdentity,
            session.PrepareBatch(in invalidBatch, [duplicateFirst, duplicateSecond]));
        var unchanged = ReadSnapshot(session);
        Assert.Equal(2UL, unchanged.Header.JournalRevision);
        Assert.Equal(2U, unchanged.Header.EntryCount);
    }

    [Fact]
    public void RealDllRoundTripPreservesFeedbackPendingAndExactAcceptedSettlement()
    {
        var configuration = CreateConfiguration(recordCapacity: 2, ResidentBudget);
        var identity = CreateIdentity();
        byte[] image;

        using (var created = new NativeTransactionJournalSession(in configuration))
        {
            Assert.Equal(NativeTransactionJournalAbi.Version, NativeTransactionJournalSession.GetAbiVersion());
            Assert.Equal(2U, created.Capacity.RecordCapacity);
            Assert.InRange(created.Capacity.ResidentBytes, 1UL, ResidentBudget);

            var prepare = CreatePrepare(identity, expectedJournalRevision: 1, nowUtcMilliseconds: 100);
            Assert.Equal(NativeTransactionJournalStatus.Ok, created.Prepare(in prepare));

            var mutation = CreateEffectObservedMutation(
                identity,
                expectedJournalRevision: 2,
                expectedEntryRevision: 1,
                nowUtcMilliseconds: 110);
            Assert.Equal(NativeTransactionJournalStatus.Ok, created.Mutate(in mutation));

            var feedback = CreateSucceededFeedback(
                identity,
                expectedJournalRevision: 3,
                expectedEntryRevision: 2,
                completedAtUtcMilliseconds: 120);
            Assert.Equal(NativeTransactionJournalStatus.Ok, created.StageFeedback(in feedback));

            var beforeEncode = ReadSnapshot(created);
            Assert.Equal(4UL, beforeEncode.Header.JournalRevision);
            Assert.Equal(1U, beforeEncode.Header.EntryCount);
            Assert.Equal(1U, beforeEncode.Header.FeedbackPendingCount);
            Assert.Equal((uint)NativeTransactionJournalPhase.FeedbackPending, beforeEncode.Records[0].Phase);
            Assert.Equal(3UL, beforeEncode.Records[0].EntryRevision);

            image = new byte[checked((int)created.Capacity.MaximumImageLength)];
            Assert.Equal(NativeTransactionJournalStatus.Ok, created.Encode(image, out var written));
            Assert.InRange(written, NativeTransactionJournalAbi.ImageHeaderSize, (ulong)image.Length);
            Array.Resize(ref image, checked((int)written));
        }

        var openConfiguration = CreateOpenConfiguration(maximumRecordCapacity: 2, ResidentBudget);
        var openStatus = NativeTransactionJournalSession.TryOpenExisting(
            in openConfiguration,
            image,
            out var opened);
        Assert.Equal(NativeTransactionJournalStatus.Ok, openStatus);
        Assert.NotNull(opened);

        using (opened!)
        {
            var reopened = ReadSnapshot(opened);
            Assert.Equal(4UL, reopened.Header.JournalRevision);
            Assert.Equal(1U, reopened.Header.EntryCount);
            Assert.Equal(1U, reopened.Header.FeedbackPendingCount);
            Assert.Equal(identity.ActionId, reopened.Records[0].Identity.ActionId);
            Assert.Equal(3UL, reopened.Records[0].EntryRevision);

            var acknowledge = CreateAcceptedAcknowledgement(
                identity,
                expectedJournalRevision: 4,
                expectedEntryRevision: 3,
                acknowledgedAtUtcMilliseconds: 130);
            Assert.Equal(NativeTransactionJournalStatus.Ok, opened.Acknowledge(in acknowledge));
            Assert.Equal(NativeTransactionJournalStatus.NoData, opened.Get(in identity, out _));

            var settled = ReadSnapshot(opened);
            Assert.Equal(5UL, settled.Header.JournalRevision);
            Assert.Equal(0U, settled.Header.EntryCount);
            Assert.Equal(0U, settled.Header.FeedbackPendingCount);
            Assert.Empty(settled.Records);
        }
    }

    [Fact]
    public void CorruptedChecksumIsRejectedWithoutReturningSession()
    {
        var configuration = CreateConfiguration(recordCapacity: 1, ResidentBudget);
        byte[] image;
        using (var created = new NativeTransactionJournalSession(in configuration))
        {
            image = new byte[checked((int)created.Capacity.MaximumImageLength)];
            Assert.Equal(NativeTransactionJournalStatus.Ok, created.Encode(image, out var written));
            Array.Resize(ref image, checked((int)written));
        }

        image[64] ^= 0x5A;
        var openConfiguration = CreateOpenConfiguration(maximumRecordCapacity: 1, ResidentBudget);
        var status = NativeTransactionJournalSession.TryOpenExisting(
            in openConfiguration,
            image,
            out var session);

        Assert.Equal(NativeTransactionJournalStatus.ChecksumMismatch, status);
        Assert.Null(session);
    }

    [Fact]
    public void ResidentByteBudgetBelowRequiredFailsCreateAndOpen()
    {
        var configuration = CreateConfiguration(recordCapacity: 2, ResidentBudget);
        byte[] image;
        ulong requiredResidentBytes;
        using (var created = new NativeTransactionJournalSession(in configuration))
        {
            requiredResidentBytes = created.Capacity.ResidentBytes;
            Assert.True(requiredResidentBytes > 1);
            image = new byte[checked((int)created.Capacity.MaximumImageLength)];
            Assert.Equal(NativeTransactionJournalStatus.Ok, created.Encode(image, out var written));
            Array.Resize(ref image, checked((int)written));
        }

        var insufficientCreate = CreateConfiguration(recordCapacity: 2, requiredResidentBytes - 1);
        var createFailure = Assert.Throws<InvalidOperationException>(
            () => new NativeTransactionJournalSession(in insufficientCreate));
        Assert.Contains(nameof(NativeTransactionJournalStatus.CapacityFull), createFailure.Message);

        var insufficientOpen = CreateOpenConfiguration(
            maximumRecordCapacity: 2,
            maximumResidentBytes: requiredResidentBytes - 1);
        var openStatus = NativeTransactionJournalSession.TryOpenExisting(
            in insufficientOpen,
            image,
            out var opened);
        Assert.Equal(NativeTransactionJournalStatus.CapacityFull, openStatus);
        Assert.Null(opened);
    }

    private static NativeTransactionJournalCreateConfiguration CreateConfiguration(
        uint recordCapacity,
        ulong maximumResidentBytes)
        => new()
        {
            AbiVersion = NativeTransactionJournalAbi.Version,
            StructSize = NativeTransactionJournalSession.SizeOf<NativeTransactionJournalCreateConfiguration>(),
            JournalInstanceLow = 0x1111_2222_3333_4444,
            JournalInstanceHigh = 0xAAAA_BBBB_CCCC_DDDD,
            RecordCapacity = recordCapacity,
            MaximumResidentBytes = maximumResidentBytes
        };

    private static NativeTransactionJournalOpenConfiguration CreateOpenConfiguration(
        uint maximumRecordCapacity,
        ulong maximumResidentBytes)
        => new()
        {
            AbiVersion = NativeTransactionJournalAbi.Version,
            StructSize = NativeTransactionJournalSession.SizeOf<NativeTransactionJournalOpenConfiguration>(),
            MaximumRecordCapacity = maximumRecordCapacity,
            MaximumResidentBytes = maximumResidentBytes
        };

    private static NativeTransactionJournalIdentity CreateIdentity()
        => new()
        {
            ConfigurationGeneration = 7,
            PlanEpoch = 19,
            ActionId = 1,
            HostSessionIncarnation = 23,
            TargetId = 30,
            SoftwareId = 31,
            ProcessStartKey = 37,
            ProcessId = 41
        };

    private static NativeTransactionJournalPrepareInput CreatePrepare(
        NativeTransactionJournalIdentity identity,
        ulong expectedJournalRevision,
        ulong nowUtcMilliseconds)
        => new()
        {
            AbiVersion = NativeTransactionJournalAbi.Version,
            StructSize = NativeTransactionJournalSession.SizeOf<NativeTransactionJournalPrepareInput>(),
            ExpectedJournalRevision = expectedJournalRevision,
            Identity = identity,
            Scope = (uint)NativeTransactionJournalScope.Process,
            Disposition = (uint)NativeTransactionJournalDisposition.Apply,
            DomainMask = (uint)NativeTransactionJournalDomain.Process,
            GradeValidMask = (uint)NativeTransactionJournalGradeValidity.Process,
            ProcessFromGrade = (int)NativeTransactionJournalProcessGrade.Normal,
            ProcessToGrade = (int)NativeTransactionJournalProcessGrade.Level1,
            StableSystemStatus = 100,
            PayloadKind = (uint)NativeTransactionJournalPayloadKind.Durable,
            PayloadSlot = 101,
            PayloadGeneration = 3,
            PayloadLength = 512,
            PayloadDigestLow = 0x1234,
            PayloadDigestHigh = 0x5678,
            PayloadProvenanceDigestLow = 0x9abc,
            PayloadProvenanceDigestHigh = 0xdef0,
            NowUtcMilliseconds = nowUtcMilliseconds,
            MaximumRecoveryAttempts = 3,
            RecoveryDeadlineUtcMilliseconds = nowUtcMilliseconds + 10_000
        };

    private static NativeTransactionJournalPrepareBatchInput CreatePrepareBatch(
        ulong expectedJournalRevision,
        uint inputCount)
        => new()
        {
            AbiVersion = NativeTransactionJournalAbi.Version,
            StructSize = NativeTransactionJournalSession.SizeOf<NativeTransactionJournalPrepareBatchInput>(),
            ExpectedJournalRevision = expectedJournalRevision,
            InputCount = inputCount,
            Flags = 0
        };

    private static NativeTransactionJournalMutationInput CreateEffectObservedMutation(
        NativeTransactionJournalIdentity identity,
        ulong expectedJournalRevision,
        ulong expectedEntryRevision,
        ulong nowUtcMilliseconds)
        => new()
        {
            AbiVersion = NativeTransactionJournalAbi.Version,
            StructSize = NativeTransactionJournalSession.SizeOf<NativeTransactionJournalMutationInput>(),
            Identity = identity,
            ExpectedJournalRevision = expectedJournalRevision,
            ExpectedEntryRevision = expectedEntryRevision,
            NowUtcMilliseconds = nowUtcMilliseconds,
            Event = (uint)NativeTransactionJournalMutationEvent.ConfirmEffectObserved,
            ExpectedPhase = (uint)NativeTransactionJournalPhase.Prepared,
            StableSystemStatus = 202
        };

    private static NativeTransactionJournalStageFeedbackInput CreateSucceededFeedback(
        NativeTransactionJournalIdentity identity,
        ulong expectedJournalRevision,
        ulong expectedEntryRevision,
        ulong completedAtUtcMilliseconds)
        => new()
        {
            AbiVersion = NativeTransactionJournalAbi.Version,
            StructSize = NativeTransactionJournalSession.SizeOf<NativeTransactionJournalStageFeedbackInput>(),
            Identity = identity,
            ExpectedJournalRevision = expectedJournalRevision,
            ExpectedEntryRevision = expectedEntryRevision,
            CompletedAtUtcMilliseconds = completedAtUtcMilliseconds,
            ExpectedPhase = (uint)NativeTransactionJournalPhase.EffectObserved,
            FeedbackValidMask = (uint)(NativeTransactionJournalFeedbackValidity.CompletedAt |
                NativeTransactionJournalFeedbackValidity.ActualProcessGrade),
            FeedbackFlags = (uint)(NativeTransactionJournalFeedbackFlags.ProcessOwned |
                NativeTransactionJournalFeedbackFlags.RollbackPayloadPersisted),
            FeedbackStatus = (uint)NativeTransactionJournalFeedbackStatus.Succeeded,
            FeedbackSystemStatus = 300,
            ActualProcessGrade = (int)NativeTransactionJournalProcessGrade.Level1
        };

    private static NativeTransactionJournalAckInput CreateAcceptedAcknowledgement(
        NativeTransactionJournalIdentity identity,
        ulong expectedJournalRevision,
        ulong expectedEntryRevision,
        ulong acknowledgedAtUtcMilliseconds)
        => new()
        {
            AbiVersion = NativeTransactionJournalAbi.Version,
            StructSize = NativeTransactionJournalSession.SizeOf<NativeTransactionJournalAckInput>(),
            Identity = identity,
            ExpectedJournalRevision = expectedJournalRevision,
            ExpectedEntryRevision = expectedEntryRevision,
            AcknowledgedAtUtcMilliseconds = acknowledgedAtUtcMilliseconds,
            Result = (uint)NativeTransactionJournalAckResult.Accepted,
            ExpectedPhase = (uint)NativeTransactionJournalPhase.FeedbackPending,
            StableSystemStatus = 501
        };

    private static (
        NativeTransactionJournalSnapshotHeader Header,
        NativeTransactionJournalRecord[] Records) ReadSnapshot(
            NativeTransactionJournalSession session)
    {
        var header = new NativeTransactionJournalSnapshotHeader
        {
            AbiVersion = NativeTransactionJournalAbi.Version,
            StructSize = NativeTransactionJournalSession.SizeOf<NativeTransactionJournalSnapshotHeader>()
        };
        var records = new NativeTransactionJournalRecord[session.Capacity.RecordCapacity];
        Assert.Equal(NativeTransactionJournalStatus.Ok, session.GetSnapshot(ref header, records));
        Array.Resize(ref records, checked((int)header.EntryCount));
        return (header, records);
    }

    private static void AssertOffset<T>(string fieldName, int expected) where T : struct
        => Assert.Equal(new IntPtr(expected), Marshal.OffsetOf<T>(fieldName));
}
