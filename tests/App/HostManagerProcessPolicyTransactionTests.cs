using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Infrastructure.Optimization.Transactions;
using Xunit;

namespace ResourceManager.App.Tests;

public sealed class HostManagerProcessPolicyTransactionTests
{
    private static readonly DateTimeOffset StartedAt =
        new(2026, 7, 22, 12, 34, 56, TimeSpan.Zero);

    [Fact]
    public void Capture_StoresOnlyExactIdentityAndBaselineInStrictV4Payload()
    {
        var writer = new RecordingPolicyWriter(CreateSnapshot(affinity: 0));
        var transaction = new HostManagerProcessPolicyTransaction(writer);

        var result = transaction.Capture(42);

        Assert.Equal(HostManagerProcessPolicyCaptureStatus.Captured, result.Status);
        Assert.Equal(HostManagerProcessPolicyTransactionFields.All, result.Fields);
        Assert.Equal(HostManagerProcessPolicyRollbackPayloadCodec.PayloadSize, result.Payload.Length);
        Assert.Equal(HostManagerProcessPolicyRollbackPayloadCodec.Version, BitConverter.ToUInt16(result.Payload, 4));
        Assert.True(HostManagerProcessPolicyRollbackPayloadCodec.TryDecode(result.Payload, out var payload));
        Assert.Equal(HostManagerProcessPolicyTransactionFields.All, payload.Fields);
        Assert.Equal(42, payload.ProcessId);
        Assert.Equal(StartedAt.ToFileTime(), payload.ProcessStartKey);
        Assert.Equal((uint)ProcessPriorityClass.AboveNormal, payload.BaselinePriorityClass);
        Assert.Equal(5U, payload.BaselineMemoryPriority);
        Assert.Equal(0x10U, payload.BaselinePowerControlMask);
        Assert.Equal(0x20U, payload.BaselinePowerStateMask);
        Assert.Equal(0U, payload.TargetMemoryPriority);
        Assert.Empty(writer.Requests);
    }

    [Fact]
    public void MemoryPriorityScope_CapturesAppliesAndRestoresOnlyTheOwnedField()
    {
        var original = CreateSnapshot() with
        {
            PriorityClass = "unavailable-to-memory-transaction",
            PowerThrottlingControlMask = null,
            PowerThrottlingStateMask = null
        };
        var writer = new RecordingPolicyWriter(original);
        var transaction = new HostManagerProcessPolicyTransaction(writer);

        var capture = transaction.CaptureMemoryPriority(42, 3);

        Assert.Equal(HostManagerProcessPolicyCaptureStatus.Captured, capture.Status);
        Assert.Equal(HostManagerProcessPolicyTransactionFields.MemoryPriority, capture.Fields);
        Assert.True(HostManagerProcessPolicyRollbackPayloadCodec.TryDecode(capture.Payload, out var payload));
        Assert.Equal(HostManagerProcessPolicyTransactionFields.MemoryPriority, payload.Fields);
        Assert.Equal(0U, payload.BaselinePriorityClass);
        Assert.Equal(5U, payload.BaselineMemoryPriority);
        Assert.Equal(0U, payload.BaselinePowerControlMask);
        Assert.Equal(0U, payload.BaselinePowerStateMask);
        Assert.Equal(3U, payload.TargetMemoryPriority);

        var applied = transaction.ApplyMemoryPriority(capture.Payload, 3);

        Assert.Equal(HostManagerProcessPolicyApplyStatus.Applied, applied.Status);
        Assert.Equal(HostManagerProcessPolicyTransactionFields.MemoryPriority, applied.RequestedFields);
        var applyRequest = Assert.Single(writer.Requests);
        Assert.Null(applyRequest.PriorityClass);
        Assert.Equal(3U, applyRequest.MemoryPriority);
        Assert.Equal(5U, applyRequest.ExpectedMemoryPriority);
        Assert.Equal(original.StartedAt, applyRequest.ExpectedStartedAt);
        Assert.Null(applyRequest.PowerControlMask);
        Assert.Null(applyRequest.PowerStateMask);
        Assert.Equal(original.PriorityClass, writer.Current!.PriorityClass);
        Assert.Null(writer.Current.PowerThrottlingControlMask);
        Assert.Null(writer.Current.PowerThrottlingStateMask);

        writer.Requests.Clear();
        var restored = transaction.RestoreMemoryPriority(capture.Payload, 3);

        Assert.Equal(HostManagerProcessPolicyRestoreStatus.Restored, restored.Status);
        Assert.Equal(HostManagerProcessPolicyTransactionFields.MemoryPriority, restored.RequestedFields);
        var restoreRequest = Assert.Single(writer.Requests);
        Assert.Null(restoreRequest.PriorityClass);
        Assert.Equal(5U, restoreRequest.MemoryPriority);
        Assert.Equal(3U, restoreRequest.ExpectedMemoryPriority);
        Assert.Equal(original.StartedAt, restoreRequest.ExpectedStartedAt);
        Assert.Null(restoreRequest.PowerControlMask);
        Assert.Null(restoreRequest.PowerStateMask);
        Assert.Equal(original, writer.Current);
    }

    [Fact]
    public void MemoryPriorityScope_MapsExecutionBoundaryConflictsToOwnershipLossWithoutWriting()
    {
        var applyWriter = new RecordingPolicyWriter(CreateSnapshot());
        var applyTransaction = new HostManagerProcessPolicyTransaction(applyWriter);
        var applyCapture = applyTransaction.CaptureMemoryPriority(42, 3);
        applyWriter.BeforeBatch = _ =>
            applyWriter.Current = applyWriter.Current! with { MemoryPriority = 4 };

        var apply = applyTransaction.ApplyMemoryPriority(applyCapture.Payload, 3);

        Assert.Equal(HostManagerProcessPolicyApplyStatus.OwnershipLost, apply.Status);
        Assert.Equal(4U, applyWriter.Current!.MemoryPriority);
        var applyRequest = Assert.Single(applyWriter.Requests);
        Assert.Equal(5U, applyRequest.ExpectedMemoryPriority);
        Assert.Equal(3U, applyRequest.MemoryPriority);

        var restoreWriter = new RecordingPolicyWriter(CreateSnapshot());
        var restoreTransaction = new HostManagerProcessPolicyTransaction(restoreWriter);
        var restoreCapture = restoreTransaction.CaptureMemoryPriority(42, 3);
        Assert.Equal(
            HostManagerProcessPolicyApplyStatus.Applied,
            restoreTransaction.ApplyMemoryPriority(restoreCapture.Payload, 3).Status);
        restoreWriter.Requests.Clear();
        restoreWriter.BeforeBatch = _ =>
            restoreWriter.Current = restoreWriter.Current! with { MemoryPriority = 4 };

        var restore = restoreTransaction.RestoreMemoryPriority(restoreCapture.Payload, 3);

        Assert.Equal(HostManagerProcessPolicyRestoreStatus.OwnershipLost, restore.Status);
        Assert.Equal(4U, restoreWriter.Current!.MemoryPriority);
        var restoreRequest = Assert.Single(restoreWriter.Requests);
        Assert.Equal(3U, restoreRequest.ExpectedMemoryPriority);
        Assert.Equal(5U, restoreRequest.MemoryPriority);
    }

    [Fact]
    public void MemoryPriorityInspection_DistinguishesOwnedBaselineAndForeignState()
    {
        var writer = new RecordingPolicyWriter(CreateSnapshot());
        var transaction = new HostManagerProcessPolicyTransaction(writer);
        var capture = transaction.CaptureMemoryPriority(42, 3);

        Assert.Equal(
            HostManagerProcessPolicyInspectionStatus.Baseline,
            transaction.InspectMemoryPriority(capture.Payload, 3).Status);

        Assert.Equal(
            HostManagerProcessPolicyApplyStatus.Applied,
            transaction.ApplyMemoryPriority(capture.Payload, 3).Status);
        Assert.Equal(
            HostManagerProcessPolicyInspectionStatus.Owned,
            transaction.InspectMemoryPriority(capture.Payload, 3).Status);

        writer.Current = writer.Current! with { MemoryPriority = 4 };
        Assert.Equal(
            HostManagerProcessPolicyInspectionStatus.Foreign,
            transaction.InspectMemoryPriority(capture.Payload, 3).Status);
        Assert.Single(writer.Requests);
    }

    [Theory]
    [InlineData(
        RecoveryReadStatus.Unavailable,
        (int)HostManagerProcessPolicyInspectionStatus.Unavailable)]
    [InlineData(
        RecoveryReadStatus.NotFoundOrExited,
        (int)HostManagerProcessPolicyInspectionStatus.ProcessExited)]
    public void MemoryPriorityInspection_PropagatesIdentityReadFailures(
        RecoveryReadStatus identityReadStatus,
        int expected)
    {
        var writer = new RecordingPolicyWriter(CreateSnapshot());
        var transaction = new HostManagerProcessPolicyTransaction(writer);
        var capture = transaction.CaptureMemoryPriority(42, 3);
        writer.IdentityReadStatus = identityReadStatus;

        var result = transaction.InspectMemoryPriority(capture.Payload, 3);

        Assert.Equal((HostManagerProcessPolicyInspectionStatus)expected, result.Status);
        Assert.Empty(writer.Requests);
    }

    [Fact]
    public void MemoryPriorityInspection_RejectsPidReuse()
    {
        var writer = new RecordingPolicyWriter(CreateSnapshot());
        var transaction = new HostManagerProcessPolicyTransaction(writer);
        var capture = transaction.CaptureMemoryPriority(42, 3);
        writer.Current = writer.Current! with
        {
            StartedAt = StartedAt.AddSeconds(1)
        };

        var result = transaction.InspectMemoryPriority(capture.Payload, 3);

        Assert.Equal(
            HostManagerProcessPolicyInspectionStatus.IdentityChanged,
            result.Status);
        Assert.Empty(writer.Requests);
    }

    [Fact]
    public void MemoryPriorityCapture_RequiresAnExplicitCanonicalTargetBeforeReadingProcess()
    {
        var writer = new RecordingPolicyWriter(CreateSnapshot());
        var transaction = new HostManagerProcessPolicyTransaction(writer);

        var fieldOnly = transaction.Capture(
            42,
            HostManagerProcessPolicyTransactionFields.MemoryPriority);
        var tooLow = transaction.CaptureMemoryPriority(42, 0);
        var tooHigh = transaction.CaptureMemoryPriority(42, 6);

        Assert.Equal(HostManagerProcessPolicyCaptureStatus.InvalidRequest, fieldOnly.Status);
        Assert.Equal(HostManagerProcessPolicyCaptureStatus.InvalidRequest, tooLow.Status);
        Assert.Equal(HostManagerProcessPolicyCaptureStatus.InvalidRequest, tooHigh.Status);
        Assert.Equal(0, writer.ReadCount);
        Assert.Empty(writer.Requests);
    }

    [Fact]
    public void CpuPolicyScope_DoesNotOwnOrModifyMemoryPriority()
    {
        var original = CreateSnapshot();
        var writer = new RecordingPolicyWriter(original);
        var transaction = new HostManagerProcessPolicyTransaction(writer);
        var capture = transaction.Capture(
            42,
            HostManagerProcessPolicyTransactionFields.CpuPolicy);

        var applied = transaction.Apply(capture.Payload, HostManagerProcessGrades.Level2);

        Assert.Equal(HostManagerProcessPolicyApplyStatus.Applied, applied.Status);
        Assert.Equal(HostManagerProcessPolicyTransactionFields.CpuPolicy, applied.RequestedFields);
        var request = Assert.Single(writer.Requests);
        Assert.Equal(ProcessPriorityClass.BelowNormal.ToString(), request.PriorityClass);
        Assert.Null(request.MemoryPriority);
        Assert.NotNull(request.PowerControlMask);
        Assert.Equal(original.MemoryPriority, writer.Current!.MemoryPriority);

        writer.Requests.Clear();
        var restored = transaction.Restore(capture.Payload, HostManagerProcessGrades.Level2);

        Assert.Equal(HostManagerProcessPolicyRestoreStatus.Restored, restored.Status);
        Assert.Equal(HostManagerProcessPolicyTransactionFields.CpuPolicy, restored.RequestedFields);
        Assert.Null(Assert.Single(writer.Requests).MemoryPriority);
        Assert.Equal(original, writer.Current);
    }

    [Fact]
    public void MemoryPriorityEntryPoints_RejectAFullPolicyPayloadWithoutWriting()
    {
        var writer = new RecordingPolicyWriter(CreateSnapshot());
        var transaction = new HostManagerProcessPolicyTransaction(writer);
        var capture = transaction.Capture(42);

        var applied = transaction.ApplyMemoryPriority(capture.Payload, 3);
        var restored = transaction.RestoreMemoryPriority(capture.Payload, 3);

        Assert.Equal(HostManagerProcessPolicyApplyStatus.InvalidTarget, applied.Status);
        Assert.Equal(HostManagerProcessPolicyRestoreStatus.InvalidTarget, restored.Status);
        Assert.Empty(writer.Requests);
    }

    [Fact]
    public void MemoryPriorityEntryPoints_RejectTargetsDifferentFromTheSealedTarget()
    {
        var writer = new RecordingPolicyWriter(CreateSnapshot());
        var transaction = new HostManagerProcessPolicyTransaction(writer);
        var capture = transaction.CaptureMemoryPriority(42, 3);
        var readsAfterCapture = writer.ReadCount;

        var applied = transaction.ApplyMemoryPriority(capture.Payload, 2);
        var restored = transaction.RestoreMemoryPriority(capture.Payload, 2);

        Assert.Equal(HostManagerProcessPolicyApplyStatus.InvalidTarget, applied.Status);
        Assert.Equal(HostManagerProcessPolicyRestoreStatus.InvalidTarget, restored.Status);
        Assert.Equal(readsAfterCapture, writer.ReadCount);
        Assert.Empty(writer.Requests);
    }

    [Fact]
    public void PayloadCodec_DecodesPersistedStrictV2FullPolicyPayloads()
    {
        var writer = new RecordingPolicyWriter(CreateSnapshot());
        var transaction = new HostManagerProcessPolicyTransaction(writer);
        var legacy = transaction.Capture(42).Payload.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(legacy.AsSpan(0, 4), 0x3254_5048);
        BinaryPrimitives.WriteUInt16LittleEndian(
            legacy.AsSpan(4, 2),
            HostManagerProcessPolicyRollbackPayloadCodec.LegacyVersion);
        RewriteDigest(legacy);

        Assert.True(HostManagerProcessPolicyRollbackPayloadCodec.TryDecode(legacy, out var payload));
        Assert.Equal(HostManagerProcessPolicyTransactionFields.All, payload.Fields);
        Assert.Equal(42, payload.ProcessId);
        Assert.Equal(0U, payload.TargetMemoryPriority);

        Assert.Equal(
            HostManagerProcessPolicyApplyStatus.Applied,
            transaction.Apply(legacy, HostManagerProcessGrades.Level1).Status);
        writer.Requests.Clear();

        Assert.Equal(
            HostManagerProcessPolicyRestoreStatus.Restored,
            transaction.RestoreForRecovery(
                legacy,
                HostManagerProcessGrades.Level1,
                HostManagerProcessGrades.Level1).Status);
        Assert.Equal(CreateSnapshot(), writer.Current);
        Assert.Single(writer.Requests);
    }

    [Fact]
    public void PayloadCodec_DecodesPersistedStrictV3CpuPayloadsAndRecoveryRemainsAvailable()
    {
        var original = CreateSnapshot();
        var writer = new RecordingPolicyWriter(original);
        var transaction = new HostManagerProcessPolicyTransaction(writer);
        var previous = transaction.Capture(
            42,
            HostManagerProcessPolicyTransactionFields.CpuPolicy).Payload.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(previous.AsSpan(0, 4), 0x3354_5048);
        BinaryPrimitives.WriteUInt16LittleEndian(
            previous.AsSpan(4, 2),
            HostManagerProcessPolicyRollbackPayloadCodec.PreviousVersion);
        RewriteDigest(previous);

        Assert.True(HostManagerProcessPolicyRollbackPayloadCodec.TryDecode(previous, out var payload));
        Assert.Equal(HostManagerProcessPolicyTransactionFields.CpuPolicy, payload.Fields);
        Assert.Equal(0U, payload.TargetMemoryPriority);
        Assert.Equal(
            HostManagerProcessPolicyApplyStatus.Applied,
            transaction.Apply(previous, HostManagerProcessGrades.Level2).Status);
        writer.Requests.Clear();

        Assert.Equal(
            HostManagerProcessPolicyRestoreStatus.Restored,
            transaction.RestoreForRecovery(
                previous,
                HostManagerProcessGrades.Level2,
                HostManagerProcessGrades.Level2).Status);
        Assert.Equal(original, writer.Current);
        Assert.Single(writer.Requests);
    }

    [Fact]
    public void PersistedV3MemoryPayload_DecodesButRecoveryRejectsItsMissingTarget()
    {
        var writer = new RecordingPolicyWriter(CreateSnapshot());
        var transaction = new HostManagerProcessPolicyTransaction(writer);
        var previous = transaction.CaptureMemoryPriority(42, 3).Payload.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(previous.AsSpan(0, 4), 0x3354_5048);
        BinaryPrimitives.WriteUInt16LittleEndian(
            previous.AsSpan(4, 2),
            HostManagerProcessPolicyRollbackPayloadCodec.PreviousVersion);
        previous.AsSpan(48, 4).Clear();
        RewriteDigest(previous);
        var readsAfterCapture = writer.ReadCount;

        Assert.True(HostManagerProcessPolicyRollbackPayloadCodec.TryDecode(previous, out var payload));
        Assert.Equal(HostManagerProcessPolicyTransactionFields.MemoryPriority, payload.Fields);
        Assert.Equal(0U, payload.TargetMemoryPriority);

        var restored = transaction.RestoreForRecovery(
            previous,
            HostManagerProcessGrades.Normal,
            HostManagerProcessGrades.Normal);

        Assert.Equal(HostManagerProcessPolicyRestoreStatus.InvalidTarget, restored.Status);
        Assert.Equal(readsAfterCapture, writer.ReadCount);
        Assert.Empty(writer.Requests);
    }

    [Fact]
    public void MemoryPriorityRecovery_UsesTheSealedTargetAndIgnoresCpuGradeFields()
    {
        var original = CreateSnapshot();
        var writer = new RecordingPolicyWriter(original);
        var transaction = new HostManagerProcessPolicyTransaction(writer);
        var capture = transaction.CaptureMemoryPriority(42, 3);
        Assert.Equal(
            HostManagerProcessPolicyApplyStatus.Applied,
            transaction.ApplyMemoryPriority(capture.Payload, 3).Status);
        writer.Requests.Clear();

        var restored = transaction.RestoreForRecovery(
            capture.Payload,
            HostManagerProcessGrades.Normal,
            HostManagerProcessGrades.Level4);

        Assert.Equal(HostManagerProcessPolicyRestoreStatus.Restored, restored.Status);
        Assert.Equal(original, writer.Current);
        var request = Assert.Single(writer.Requests);
        Assert.Null(request.PriorityClass);
        Assert.Equal(original.MemoryPriority, request.MemoryPriority);
        Assert.Equal(3U, request.ExpectedMemoryPriority);
        Assert.Null(request.PowerControlMask);
        Assert.Null(request.PowerStateMask);
    }

    [Fact]
    public void RecoveryInspectionClassifiesNoEffectTerminalStatesBeforeRestore()
    {
        var writer = new RecordingPolicyWriter(CreateSnapshot());
        var transaction = new HostManagerProcessPolicyTransaction(writer);
        var capture = transaction.Capture(42);

        Assert.Equal(
            HostManagerProcessPolicyInspectionStatus.Baseline,
            transaction.InspectForRecovery(
                capture.Payload,
                HostManagerProcessGrades.Level1,
                HostManagerProcessGrades.Level1).Status);
        Assert.Empty(writer.Requests);

        Assert.Equal(
            HostManagerProcessPolicyApplyStatus.Applied,
            transaction.Apply(capture.Payload, HostManagerProcessGrades.Level1).Status);
        writer.Requests.Clear();
        Assert.Equal(
            HostManagerProcessPolicyInspectionStatus.Owned,
            transaction.InspectForRecovery(
                capture.Payload,
                HostManagerProcessGrades.Level1,
                HostManagerProcessGrades.Level1).Status);
        Assert.Empty(writer.Requests);

        writer.IdentityReadStatus = RecoveryReadStatus.NotFoundOrExited;
        Assert.Equal(
            HostManagerProcessPolicyInspectionStatus.ProcessExited,
            transaction.InspectForRecovery(
                capture.Payload,
                HostManagerProcessGrades.Level1,
                HostManagerProcessGrades.Level1).Status);
        Assert.Empty(writer.Requests);
    }

    [Fact]
    public void Capture_FailsClosedForInvalidIdentityOrIncompleteBaseline()
    {
        var invalidRequestWriter = new RecordingPolicyWriter(CreateSnapshot());
        var invalidRequest = new HostManagerProcessPolicyTransaction(invalidRequestWriter).Capture(0);
        Assert.Equal(HostManagerProcessPolicyCaptureStatus.InvalidRequest, invalidRequest.Status);
        Assert.Equal(0, invalidRequestWriter.ReadCount);

        var incompleteWriter = new RecordingPolicyWriter(CreateSnapshot() with { MemoryPriority = null });
        var incomplete = new HostManagerProcessPolicyTransaction(incompleteWriter).Capture(42);
        Assert.Equal(HostManagerProcessPolicyCaptureStatus.Unavailable, incomplete.Status);
        Assert.Empty(incomplete.Payload);
    }

    [Theory]
    [InlineData(
        HostManagerProcessGrades.A1,
        (uint)(HostManagerProcessPolicyTransactionFields.PriorityClass
            | HostManagerProcessPolicyTransactionFields.PowerThrottling),
        ProcessPriorityClass.High,
        5U,
        0x11U,
        0x20U)]
    [InlineData(
        HostManagerProcessGrades.Level1,
        (uint)HostManagerProcessPolicyTransactionFields.All,
        ProcessPriorityClass.Normal,
        4U,
        0x11U,
        0x21U)]
    [InlineData(
        HostManagerProcessGrades.Level2,
        (uint)HostManagerProcessPolicyTransactionFields.All,
        ProcessPriorityClass.BelowNormal,
        2U,
        0x11U,
        0x21U)]
    [InlineData(
        HostManagerProcessGrades.Level3,
        (uint)HostManagerProcessPolicyTransactionFields.All,
        ProcessPriorityClass.Idle,
        1U,
        0x11U,
        0x21U)]
    public void ApplyAndRestore_DeriveEverySupportedGradeFromOneBaselinePayload(
        int grade,
        uint expectedFieldsValue,
        ProcessPriorityClass expectedPriority,
        uint expectedMemory,
        uint expectedPowerControl,
        uint expectedPowerState)
    {
        var expectedFields = (HostManagerProcessPolicyTransactionFields)expectedFieldsValue;
        var original = CreateSnapshot();
        var writer = new RecordingPolicyWriter(original);
        var transaction = new HostManagerProcessPolicyTransaction(writer);
        var capture = transaction.Capture(42);
        var payloadBefore = capture.Payload.ToArray();

        var applied = transaction.Apply(capture.Payload, grade);

        Assert.Equal(HostManagerProcessPolicyApplyStatus.Applied, applied.Status);
        Assert.Equal(expectedFields, applied.RequestedFields);
        Assert.Equal(expectedFields, applied.SucceededFields);
        Assert.Equal(expectedPriority.ToString(), writer.Current!.PriorityClass);
        Assert.Equal(expectedMemory, writer.Current.MemoryPriority);
        Assert.Equal(expectedPowerControl, writer.Current.PowerThrottlingControlMask);
        Assert.Equal(expectedPowerState, writer.Current.PowerThrottlingStateMask);
        Assert.Equal(original.ProcessorAffinityMask, writer.Current.ProcessorAffinityMask);

        var restored = transaction.Restore(capture.Payload, grade);
        var repeatedRestore = transaction.Restore(capture.Payload, grade);

        Assert.Equal(HostManagerProcessPolicyRestoreStatus.Restored, restored.Status);
        Assert.Equal(expectedFields, restored.RequestedFields);
        Assert.Equal(expectedFields, restored.RestoredFields);
        Assert.Equal(HostManagerProcessPolicyRestoreStatus.AlreadyRestored, repeatedRestore.Status);
        Assert.Equal(original, writer.Current);
        Assert.Equal(payloadBefore, capture.Payload);
        Assert.All(writer.Requests, static request => Assert.Null(request.AffinityMask));
    }

    [Fact]
    public void SamePayload_SupportsGradeMigrationThroughExactRestore()
    {
        var original = CreateSnapshot();
        var writer = new RecordingPolicyWriter(original);
        var transaction = new HostManagerProcessPolicyTransaction(writer);
        var capture = transaction.Capture(42);
        var stablePayload = capture.Payload.ToArray();

        Assert.Equal(
            HostManagerProcessPolicyApplyStatus.Applied,
            transaction.Apply(capture.Payload, HostManagerProcessGrades.Level1).Status);
        Assert.Equal(
            HostManagerProcessPolicyRestoreStatus.Restored,
            transaction.Restore(capture.Payload, HostManagerProcessGrades.Level1).Status);
        Assert.Equal(
            HostManagerProcessPolicyApplyStatus.Applied,
            transaction.Apply(capture.Payload, HostManagerProcessGrades.Level3).Status);

        Assert.Equal(stablePayload, capture.Payload);
        Assert.Equal(ProcessPriorityClass.Idle.ToString(), writer.Current!.PriorityClass);
        Assert.Equal(1U, writer.Current.MemoryPriority);
        Assert.Equal(0x11U, writer.Current.PowerThrottlingControlMask);
        Assert.Equal(0x21U, writer.Current.PowerThrottlingStateMask);
        Assert.All(writer.Requests, static request => Assert.Null(request.AffinityMask));
    }

    [Fact]
    public void Apply_ContinuesFromMixedBaselineAndExpectedTargetState()
    {
        var writer = new RecordingPolicyWriter(CreateSnapshot());
        var transaction = new HostManagerProcessPolicyTransaction(writer);
        var capture = transaction.Capture(42);
        writer.Current = writer.Current! with
        {
            PriorityClass = ProcessPriorityClass.BelowNormal.ToString()
        };

        var result = transaction.Apply(capture.Payload, HostManagerProcessGrades.Level2);

        Assert.Equal(HostManagerProcessPolicyApplyStatus.Applied, result.Status);
        Assert.Equal(
            HostManagerProcessPolicyTransactionFields.MemoryPriority
                | HostManagerProcessPolicyTransactionFields.PowerThrottling,
            result.RequestedFields);
        var request = Assert.Single(writer.Requests);
        Assert.Null(request.PriorityClass);
        Assert.Equal(2U, request.MemoryPriority);
        Assert.Equal(0x11U, request.PowerControlMask);
        Assert.Null(request.AffinityMask);
    }

    [Fact]
    public void Restore_ContinuesFromMixedBaselineAndExpectedTargetState()
    {
        var original = CreateSnapshot();
        var writer = new RecordingPolicyWriter(original);
        var transaction = new HostManagerProcessPolicyTransaction(writer);
        var capture = transaction.Capture(42);
        Assert.Equal(
            HostManagerProcessPolicyApplyStatus.Applied,
            transaction.Apply(capture.Payload, HostManagerProcessGrades.Level2).Status);
        writer.Requests.Clear();
        writer.Current = writer.Current! with { PriorityClass = original.PriorityClass };

        var result = transaction.Restore(capture.Payload, HostManagerProcessGrades.Level2);

        Assert.Equal(HostManagerProcessPolicyRestoreStatus.Restored, result.Status);
        Assert.Equal(
            HostManagerProcessPolicyTransactionFields.MemoryPriority
                | HostManagerProcessPolicyTransactionFields.PowerThrottling,
            result.RequestedFields);
        var request = Assert.Single(writer.Requests);
        Assert.Null(request.PriorityClass);
        Assert.Equal(original.MemoryPriority, request.MemoryPriority);
        Assert.Equal(original.PowerThrottlingControlMask, request.PowerControlMask);
        Assert.Null(request.AffinityMask);
        Assert.Equal(original, writer.Current);
    }

    [Fact]
    public void RecoveryRestore_AcceptsBaselineFromAndToGradeMixturesThenRestoresBaseline()
    {
        var original = CreateSnapshot();
        var writer = new RecordingPolicyWriter(original);
        var transaction = new HostManagerProcessPolicyTransaction(writer);
        var capture = transaction.Capture(42);
        Assert.Equal(
            HostManagerProcessPolicyApplyStatus.Applied,
            transaction.Apply(capture.Payload, HostManagerProcessGrades.Level1).Status);

        writer.Requests.Clear();
        writer.Current = writer.Current! with
        {
            MemoryPriority = 1,
            PowerThrottlingControlMask = original.PowerThrottlingControlMask,
            PowerThrottlingStateMask = original.PowerThrottlingStateMask
        };

        var restored = transaction.RestoreForRecovery(
            capture.Payload,
            HostManagerProcessGrades.Level1,
            HostManagerProcessGrades.Level3);

        Assert.Equal(HostManagerProcessPolicyRestoreStatus.Restored, restored.Status);
        Assert.Equal(original, writer.Current);
        var recoveryRequest = Assert.Single(writer.Requests);
        Assert.Equal(1U, recoveryRequest.ExpectedMemoryPriority);

        writer.Requests.Clear();
        writer.Current = writer.Current! with
        {
            PriorityClass = ProcessPriorityClass.RealTime.ToString()
        };
        var foreign = transaction.RestoreForRecovery(
            capture.Payload,
            HostManagerProcessGrades.Level1,
            HostManagerProcessGrades.Level3);

        Assert.Equal(HostManagerProcessPolicyRestoreStatus.OwnershipLost, foreign.Status);
        Assert.Empty(writer.Requests);
    }

    [Fact]
    public void ApplyAndRestore_RejectForeignFieldValuesWithoutWriting()
    {
        var applyWriter = new RecordingPolicyWriter(CreateSnapshot());
        var applyTransaction = new HostManagerProcessPolicyTransaction(applyWriter);
        var applyCapture = applyTransaction.Capture(42);
        applyWriter.Current = applyWriter.Current! with
        {
            PriorityClass = ProcessPriorityClass.RealTime.ToString()
        };

        var apply = applyTransaction.Apply(applyCapture.Payload, HostManagerProcessGrades.Level2);

        Assert.Equal(HostManagerProcessPolicyApplyStatus.OwnershipLost, apply.Status);
        Assert.Empty(applyWriter.Requests);

        var restoreWriter = new RecordingPolicyWriter(CreateSnapshot());
        var restoreTransaction = new HostManagerProcessPolicyTransaction(restoreWriter);
        var restoreCapture = restoreTransaction.Capture(42);
        Assert.Equal(
            HostManagerProcessPolicyApplyStatus.Applied,
            restoreTransaction.Apply(restoreCapture.Payload, HostManagerProcessGrades.Level2).Status);
        restoreWriter.Requests.Clear();
        restoreWriter.Current = restoreWriter.Current! with { MemoryPriority = 4 };

        var restore = restoreTransaction.Restore(
            restoreCapture.Payload,
            HostManagerProcessGrades.Level2);

        Assert.Equal(HostManagerProcessPolicyRestoreStatus.OwnershipLost, restore.Status);
        Assert.Empty(restoreWriter.Requests);
    }

    [Fact]
    public void ApplyAndRestore_RejectPidReuseBeforeAnyExternalEffect()
    {
        var applyWriter = new RecordingPolicyWriter(CreateSnapshot());
        var applyTransaction = new HostManagerProcessPolicyTransaction(applyWriter);
        var applyCapture = applyTransaction.Capture(42);
        applyWriter.Current = applyWriter.Current! with { StartedAt = StartedAt.AddSeconds(1) };

        var apply = applyTransaction.Apply(applyCapture.Payload, HostManagerProcessGrades.Level1);

        Assert.Equal(HostManagerProcessPolicyApplyStatus.OwnershipLost, apply.Status);
        Assert.Empty(applyWriter.Requests);

        var restoreWriter = new RecordingPolicyWriter(CreateSnapshot());
        var restoreTransaction = new HostManagerProcessPolicyTransaction(restoreWriter);
        var restoreCapture = restoreTransaction.Capture(42);
        Assert.Equal(
            HostManagerProcessPolicyApplyStatus.Applied,
            restoreTransaction.Apply(restoreCapture.Payload, HostManagerProcessGrades.Level1).Status);
        restoreWriter.Requests.Clear();
        restoreWriter.Current = restoreWriter.Current! with { StartedAt = StartedAt.AddSeconds(1) };

        var restore = restoreTransaction.Restore(
            restoreCapture.Payload,
            HostManagerProcessGrades.Level1);

        Assert.Equal(HostManagerProcessPolicyRestoreStatus.OwnershipLost, restore.Status);
        Assert.Empty(restoreWriter.Requests);
    }

    [Fact]
    public void ApplyAndRecoveryRestore_DistinguishProcessExitFromUnavailable()
    {
        var applyWriter = new RecordingPolicyWriter(CreateSnapshot());
        var applyTransaction = new HostManagerProcessPolicyTransaction(applyWriter);
        var applyCapture = applyTransaction.Capture(42);
        applyWriter.IdentityReadStatus = RecoveryReadStatus.NotFoundOrExited;

        var exitedApply = applyTransaction.Apply(
            applyCapture.Payload,
            HostManagerProcessGrades.Level1);

        Assert.Equal(HostManagerProcessPolicyApplyStatus.ProcessExited, exitedApply.Status);
        Assert.Empty(applyWriter.Requests);

        var restoreWriter = new RecordingPolicyWriter(CreateSnapshot());
        var restoreTransaction = new HostManagerProcessPolicyTransaction(restoreWriter);
        var restoreCapture = restoreTransaction.Capture(42);
        Assert.Equal(
            HostManagerProcessPolicyApplyStatus.Applied,
            restoreTransaction.Apply(restoreCapture.Payload, HostManagerProcessGrades.Level1).Status);
        restoreWriter.Requests.Clear();
        restoreWriter.IdentityReadStatus = RecoveryReadStatus.NotFoundOrExited;

        var exitedRestore = restoreTransaction.RestoreForRecovery(
            restoreCapture.Payload,
            HostManagerProcessGrades.Level1,
            HostManagerProcessGrades.Level2);

        Assert.Equal(HostManagerProcessPolicyRestoreStatus.ProcessExited, exitedRestore.Status);
        Assert.Empty(restoreWriter.Requests);

        restoreWriter.IdentityReadStatus = RecoveryReadStatus.Unavailable;
        var unavailableRestore = restoreTransaction.RestoreForRecovery(
            restoreCapture.Payload,
            HostManagerProcessGrades.Level1,
            HostManagerProcessGrades.Level2);

        Assert.Equal(HostManagerProcessPolicyRestoreStatus.Unavailable, unavailableRestore.Status);
        Assert.Empty(restoreWriter.Requests);
    }

    [Fact]
    public void ApplyAndRestore_RejectLevel4AndNormalBeforeReadingPayloadOrProcess()
    {
        var writer = new RecordingPolicyWriter(CreateSnapshot());
        var transaction = new HostManagerProcessPolicyTransaction(writer);
        var capture = transaction.Capture(42);
        var readsAfterCapture = writer.ReadCount;

        Assert.Equal(
            HostManagerProcessPolicyApplyStatus.Level4Rejected,
            transaction.Apply(capture.Payload, HostManagerProcessGrades.Level4).Status);
        Assert.Equal(
            HostManagerProcessPolicyApplyStatus.InvalidGrade,
            transaction.Apply(capture.Payload, HostManagerProcessGrades.Normal).Status);
        Assert.Equal(
            HostManagerProcessPolicyRestoreStatus.Level4Rejected,
            transaction.Restore(capture.Payload, HostManagerProcessGrades.Level4).Status);
        Assert.Equal(
            HostManagerProcessPolicyRestoreStatus.InvalidGrade,
            transaction.Restore(capture.Payload, HostManagerProcessGrades.Normal).Status);
        Assert.Equal(readsAfterCapture, writer.ReadCount);
        Assert.Empty(writer.Requests);
    }

    [Fact]
    public void RetiredV1CorruptionReservedBytesAndNonCanonicalBaselineAreRejected()
    {
        var writer = new RecordingPolicyWriter(CreateSnapshot());
        var transaction = new HostManagerProcessPolicyTransaction(writer);
        var capture = transaction.Capture(42);
        var invalidPayloads = new List<byte[]>();

        var corrupt = capture.Payload.ToArray();
        corrupt[32] ^= 0x01;
        invalidPayloads.Add(corrupt);

        var retiredV1 = capture.Payload.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(retiredV1.AsSpan(4, 2), 1);
        RewriteDigest(retiredV1);
        invalidPayloads.Add(retiredV1);

        var reserved = capture.Payload.ToArray();
        reserved[20] = 1;
        RewriteDigest(reserved);
        invalidPayloads.Add(reserved);

        var invalidMemory = capture.Payload.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(invalidMemory.AsSpan(36, 4), 0);
        RewriteDigest(invalidMemory);
        invalidPayloads.Add(invalidMemory);

        var nonCanonicalFullPolicyTarget = capture.Payload.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(
            nonCanonicalFullPolicyTarget.AsSpan(48, 4),
            3);
        RewriteDigest(nonCanonicalFullPolicyTarget);
        invalidPayloads.Add(nonCanonicalFullPolicyTarget);

        writer.ReadCount = 0;
        foreach (var payload in invalidPayloads)
        {
            Assert.False(HostManagerProcessPolicyRollbackPayloadCodec.TryDecode(payload, out _));
            Assert.Equal(
                HostManagerProcessPolicyApplyStatus.InvalidPayload,
                transaction.Apply(payload, HostManagerProcessGrades.Level1).Status);
            Assert.Equal(
                HostManagerProcessPolicyRestoreStatus.InvalidPayload,
                transaction.Restore(payload, HostManagerProcessGrades.Level1).Status);
        }

        Assert.Equal(0, writer.ReadCount);
        Assert.Empty(writer.Requests);
    }

    [Theory]
    [InlineData(0U)]
    [InlineData(6U)]
    public void V4MemoryPayload_RejectsNonCanonicalSealedTargets(uint targetMemoryPriority)
    {
        var writer = new RecordingPolicyWriter(CreateSnapshot());
        var transaction = new HostManagerProcessPolicyTransaction(writer);
        var payload = transaction.CaptureMemoryPriority(42, 3).Payload.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(
            payload.AsSpan(48, 4),
            targetMemoryPriority);
        RewriteDigest(payload);
        var readsAfterCapture = writer.ReadCount;

        Assert.False(HostManagerProcessPolicyRollbackPayloadCodec.TryDecode(payload, out _));
        Assert.Equal(
            HostManagerProcessPolicyApplyStatus.InvalidPayload,
            transaction.ApplyMemoryPriority(payload, 3).Status);
        Assert.Equal(
            HostManagerProcessPolicyRestoreStatus.InvalidPayload,
            transaction.RestoreForRecovery(
                payload,
                HostManagerProcessGrades.Normal,
                HostManagerProcessGrades.Normal).Status);
        Assert.Equal(readsAfterCapture, writer.ReadCount);
        Assert.Empty(writer.Requests);
    }

    [Fact]
    public void Apply_FailsClosedWhenBatchResultIsIncomplete()
    {
        var writer = new RecordingPolicyWriter(CreateSnapshot())
        {
            ResultFactory = static request =>
            [
                new ProcessResourcePolicyBatchFieldResult(
                    ProcessResourcePolicyBatchFields.PriorityClass,
                    true,
                    "ok")
            ]
        };
        var transaction = new HostManagerProcessPolicyTransaction(writer);
        var capture = transaction.Capture(42);

        var result = transaction.Apply(capture.Payload, HostManagerProcessGrades.Level1);

        Assert.Equal(HostManagerProcessPolicyApplyStatus.StateUncertain, result.Status);
        Assert.Equal(
            HostManagerProcessPolicyTransactionFields.PriorityClass,
            result.SucceededFields);
    }

    [Fact]
    public void Apply_ReturnsFailedWhenEveryRequestedFieldFailsAndStateIsUnchanged()
    {
        var writer = new RecordingPolicyWriter(CreateSnapshot())
        {
            ResultFactory = static request => CreateFieldResults(request, succeeded: false)
        };
        var transaction = new HostManagerProcessPolicyTransaction(writer);
        var capture = transaction.Capture(42);

        var result = transaction.Apply(capture.Payload, HostManagerProcessGrades.Level1);

        Assert.Equal(HostManagerProcessPolicyApplyStatus.Failed, result.Status);
        Assert.Equal(HostManagerProcessPolicyTransactionFields.None, result.SucceededFields);
    }

    [Fact]
    public void MissingReadbackAfterWriteReturnsStateUncertain()
    {
        var writer = new RecordingPolicyWriter(CreateSnapshot())
        {
            NullOnReadNumber = 3
        };
        var transaction = new HostManagerProcessPolicyTransaction(writer);
        var capture = transaction.Capture(42);

        var result = transaction.Apply(capture.Payload, HostManagerProcessGrades.Level1);

        Assert.Equal(HostManagerProcessPolicyApplyStatus.StateUncertain, result.Status);
        Assert.Single(writer.Requests);
    }

    private static ProcessResourcePolicySnapshot CreateSnapshot(
        ProcessPriorityClass priorityClass = ProcessPriorityClass.AboveNormal,
        uint memoryPriority = 5,
        uint powerControl = 0x10,
        uint powerState = 0x20,
        long affinity = 0xff)
    {
        return new ProcessResourcePolicySnapshot(
            42,
            "target",
            @"C:\Apps\target.exe",
            StartedAt,
            priorityClass.ToString(),
            affinity,
            32,
            memoryPriority,
            memoryPriority.ToString(),
            memoryPriority.ToString(),
            powerControl,
            powerState,
            $"control={powerControl};state={powerState}",
            "power");
    }

    private static void RewriteDigest(byte[] payload)
    {
        SHA256.HashData(
            payload.AsSpan(0, HostManagerProcessPolicyRollbackPayloadCodec.DigestOffset),
            payload.AsSpan(HostManagerProcessPolicyRollbackPayloadCodec.DigestOffset));
    }

    private static IReadOnlyList<ProcessResourcePolicyBatchFieldResult> CreateFieldResults(
        ProcessResourcePolicyBatchRequest request,
        bool succeeded)
    {
        var results = new List<ProcessResourcePolicyBatchFieldResult>();
        Add(request.PriorityClass is not null, ProcessResourcePolicyBatchFields.PriorityClass);
        Add(request.MemoryPriority is not null, ProcessResourcePolicyBatchFields.MemoryPriority);
        Add(request.PowerControlMask is not null, ProcessResourcePolicyBatchFields.PowerThrottling);
        return results;

        void Add(bool included, ProcessResourcePolicyBatchFields field)
        {
            if (included)
            {
                results.Add(new ProcessResourcePolicyBatchFieldResult(
                    field,
                    succeeded,
                    succeeded ? "ok" : "failed"));
            }
        }
    }

    private sealed class RecordingPolicyWriter(ProcessResourcePolicySnapshot? current)
        : IProcessResourcePolicyWriter
    {
        internal ProcessResourcePolicySnapshot? Current { get; set; } = current;

        internal int ReadCount { get; set; }

        internal int? NullOnReadNumber { get; init; }

        internal RecoveryReadStatus IdentityReadStatus { get; set; } = RecoveryReadStatus.Found;

        internal List<ProcessResourcePolicyBatchRequest> Requests { get; } = [];

        internal Func<
            ProcessResourcePolicyBatchRequest,
            IReadOnlyList<ProcessResourcePolicyBatchFieldResult>>? ResultFactory
        {
            get;
            init;
        }

        internal Action<ProcessResourcePolicyBatchRequest>? BeforeBatch { get; set; }

        public ProcessResourcePolicySnapshot? TryReadProcess(int processId)
        {
            ReadCount++;
            return ReadCount == NullOnReadNumber ? null : Current;
        }

        public IReadOnlyList<ProcessResourcePolicyBatchWriteResult> TryApplyBatch(
            IReadOnlyList<ProcessResourcePolicyBatchRequest> requests)
        {
            var request = Assert.Single(requests);
            Assert.Null(request.AffinityMask);
            Requests.Add(request);
            BeforeBatch?.Invoke(request);
            BeforeBatch = null;
            var fields = ResultFactory?.Invoke(request) ?? CreateDefaultFieldResults(request);
            ApplySuccessfulFields(request, fields);
            return [new ProcessResourcePolicyBatchWriteResult(request.ProcessId, fields)];
        }

        private IReadOnlyList<ProcessResourcePolicyBatchFieldResult> CreateDefaultFieldResults(
            ProcessResourcePolicyBatchRequest request)
        {
            var fields = CreateFieldResults(request, succeeded: true).ToArray();
            if (request.MemoryPriority is null)
            {
                return fields;
            }

            var memoryIndex = Array.FindIndex(
                fields,
                static field => field.Field == ProcessResourcePolicyBatchFields.MemoryPriority);
            if (memoryIndex >= 0
                && (Current?.StartedAt != request.ExpectedStartedAt
                    || Current?.MemoryPriority != request.ExpectedMemoryPriority))
            {
                fields[memoryIndex] = new ProcessResourcePolicyBatchFieldResult(
                    ProcessResourcePolicyBatchFields.MemoryPriority,
                    ProcessResourcePolicyBatchFieldStatus.Conflict,
                    "expected-current conflict");
            }
            return fields;
        }

        private void ApplySuccessfulFields(
            ProcessResourcePolicyBatchRequest request,
            IReadOnlyList<ProcessResourcePolicyBatchFieldResult> fields)
        {
            if (Current is null)
            {
                return;
            }
            foreach (var field in fields
                .Where(static item => item.Succeeded)
                .Select(static item => item.Field)
                .Distinct())
            {
                Current = field switch
                {
                    ProcessResourcePolicyBatchFields.PriorityClass => Current with
                    {
                        PriorityClass = request.PriorityClass!
                    },
                    ProcessResourcePolicyBatchFields.MemoryPriority => Current with
                    {
                        MemoryPriority = request.MemoryPriority
                    },
                    ProcessResourcePolicyBatchFields.PowerThrottling => Current with
                    {
                        PowerThrottlingControlMask = request.PowerControlMask,
                        PowerThrottlingStateMask = request.PowerStateMask
                    },
                    _ => Current
                };
            }
        }

        public DateTimeOffset? TryReadProcessStartedAt(int processId) => throw Unexpected();

        public RecoveryReadResult<ProcessInstanceRecoverySnapshot> ReadProcessInstanceForRecovery(int processId) =>
            IdentityReadStatus switch
            {
                RecoveryReadStatus.Found when Current?.StartedAt is DateTimeOffset startedAt =>
                    RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Found(
                        new ProcessInstanceRecoverySnapshot(processId, startedAt)),
                RecoveryReadStatus.NotFoundOrExited =>
                    RecoveryReadResult<ProcessInstanceRecoverySnapshot>.NotFoundOrExited(
                        0,
                        "process exited"),
                _ => RecoveryReadResult<ProcessInstanceRecoverySnapshot>.Unavailable(
                    5,
                    "process state unavailable")
            };

        public RecoveryReadResult<ProcessPlacementRecoverySnapshot> ReadPlacementStateForRecovery(
            int processId,
            ProcessPlacementReadFields requiredFields) => throw Unexpected();

        public RecoveryReadResult<ThreadCpuSetPolicySnapshot> ReadThreadPlacementStateForRecovery(
            int processId,
            int threadId) => throw Unexpected();

        public ProcessResourcePolicyWriteResult TrySetPriorityClass(int processId, string priorityClass) =>
            throw Unexpected();

        public ProcessResourcePolicyWriteResult TrySetProcessorAffinity(int processId, long affinityMask) =>
            throw Unexpected();

        public IReadOnlyList<uint>? TryReadProcessDefaultCpuSets(int processId) => throw Unexpected();

        public ProcessResourcePolicyWriteResult TrySetProcessDefaultCpuSets(
            int processId,
            IReadOnlyList<uint> cpuSetIds) => throw Unexpected();

        public ThreadCpuSetPolicySnapshot? TryReadThreadSelectedCpuSets(int processId, int threadId) =>
            throw Unexpected();

        public ProcessResourcePolicyWriteResult TrySetThreadSelectedCpuSets(
            int processId,
            int threadId,
            DateTimeOffset expectedCreatedAt,
            IReadOnlyList<uint> cpuSetIds) => throw Unexpected();

        public ProcessMemoryPrioritySnapshot? TryReadMemoryPriority(int processId) => throw Unexpected();

        public ProcessResourcePolicyWriteResult TrySetMemoryPriority(
            int processId,
            DateTimeOffset expectedStartedAt,
            uint expectedCurrentMemoryPriority,
            uint memoryPriority) =>
            throw Unexpected();

        public ProcessPowerThrottlingSnapshot? TryReadPowerThrottling(int processId) => throw Unexpected();

        public ProcessResourcePolicyWriteResult TrySetPowerThrottling(
            int processId,
            uint controlMask,
            uint stateMask) => throw Unexpected();

        private static InvalidOperationException Unexpected() =>
            new("Unexpected legacy writer call.");
    }
}
