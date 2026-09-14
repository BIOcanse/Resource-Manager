using System.Diagnostics;
using System.Numerics;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Infrastructure.Windows;

namespace ResourceManager.App.Infrastructure.Optimization.Transactions;

public sealed class HostManagerProcessPolicyTransaction
{
    private readonly IProcessResourcePolicyWriter policyWriter;

    internal HostManagerProcessPolicyTransaction(IProcessResourcePolicyWriter policyWriter)
    {
        this.policyWriter = policyWriter;
    }

    internal HostManagerProcessPolicyCaptureResult Capture(int processId)
        => CaptureCore(processId, HostManagerProcessPolicyTransactionFields.All, 0);

    internal HostManagerProcessPolicyCaptureResult Capture(
        int processId,
        HostManagerProcessPolicyTransactionFields fields)
        => fields == HostManagerProcessPolicyTransactionFields.MemoryPriority
            ? HostManagerProcessPolicyCaptureResult.Empty(
                HostManagerProcessPolicyCaptureStatus.InvalidRequest,
                processId,
                0)
            : CaptureCore(processId, fields, 0);

    internal HostManagerProcessPolicyCaptureResult CaptureMemoryPriority(
        int processId,
        uint targetMemoryPriority)
        => targetMemoryPriority is >= NativeMethods.MemoryPriorityVeryLow
                and <= NativeMethods.MemoryPriorityNormal
            ? CaptureCore(
                processId,
                HostManagerProcessPolicyTransactionFields.MemoryPriority,
                targetMemoryPriority)
            : HostManagerProcessPolicyCaptureResult.Empty(
                HostManagerProcessPolicyCaptureStatus.InvalidRequest,
                processId,
                0);

    private HostManagerProcessPolicyCaptureResult CaptureCore(
        int processId,
        HostManagerProcessPolicyTransactionFields fields,
        uint targetMemoryPriority)
    {
        if (processId <= 0 || !IsSupportedFieldScope(fields))
        {
            return HostManagerProcessPolicyCaptureResult.Empty(
                HostManagerProcessPolicyCaptureStatus.InvalidRequest,
                0,
                0);
        }

        ProcessResourcePolicySnapshot? snapshot;
        try
        {
            snapshot = policyWriter.TryReadProcess(processId);
        }
        catch (Exception exception) when (IsExpectedBoundaryException(exception))
        {
            return HostManagerProcessPolicyCaptureResult.Empty(
                HostManagerProcessPolicyCaptureStatus.Unavailable,
                processId,
                0);
        }

        if (!TryReadIdentity(snapshot, processId, out var processStartKey)
            || !TryCreatePayload(
                snapshot!,
                processStartKey,
                fields,
                targetMemoryPriority,
                out var payload))
        {
            return HostManagerProcessPolicyCaptureResult.Empty(
                HostManagerProcessPolicyCaptureStatus.Unavailable,
                processId,
                processStartKey);
        }

        return new HostManagerProcessPolicyCaptureResult(
            HostManagerProcessPolicyCaptureStatus.Captured,
            fields,
            processId,
            processStartKey,
            HostManagerProcessPolicyRollbackPayloadCodec.Encode(payload));
    }

    internal HostManagerProcessPolicyApplyResult Apply(
        ReadOnlySpan<byte> payloadBytes,
        int targetGrade)
        => ApplyCore(payloadBytes, targetGrade, null);

    internal HostManagerProcessPolicyApplyResult ApplyMemoryPriority(
        ReadOnlySpan<byte> payloadBytes,
        uint targetMemoryPriority)
        => ApplyCore(payloadBytes, null, targetMemoryPriority);

    private HostManagerProcessPolicyApplyResult ApplyCore(
        ReadOnlySpan<byte> payloadBytes,
        int? targetGrade,
        uint? targetMemoryPriority)
    {
        if (targetGrade == HostManagerProcessGrades.Level4)
        {
            return EmptyApplyResult(HostManagerProcessPolicyApplyStatus.Level4Rejected);
        }
        if (targetGrade is int grade && !IsSupportedGrade(grade))
        {
            return EmptyApplyResult(HostManagerProcessPolicyApplyStatus.InvalidGrade);
        }
        if (targetGrade is null
            && targetMemoryPriority is not (>= NativeMethods.MemoryPriorityVeryLow
                and <= NativeMethods.MemoryPriorityNormal))
        {
            return EmptyApplyResult(HostManagerProcessPolicyApplyStatus.InvalidTarget);
        }
        if (!HostManagerProcessPolicyRollbackPayloadCodec.TryDecode(payloadBytes, out var payload))
        {
            return EmptyApplyResult(HostManagerProcessPolicyApplyStatus.InvalidPayload);
        }
        if (targetMemoryPriority is not null
            && (payload.Fields != HostManagerProcessPolicyTransactionFields.MemoryPriority
                || payload.TargetMemoryPriority != targetMemoryPriority))
        {
            return ApplyResult(payload, HostManagerProcessPolicyApplyStatus.InvalidTarget, 0, 0);
        }
        if (targetGrade is not null
            && payload.Fields == HostManagerProcessPolicyTransactionFields.MemoryPriority)
        {
            return ApplyResult(payload, HostManagerProcessPolicyApplyStatus.InvalidTarget, 0, 0);
        }

        var target = targetMemoryPriority is uint memoryPriority
            ? CreateMemoryPriorityTarget(payload, memoryPriority)
            : CreateTargetPolicy(payload, targetGrade!.Value);
        var before = EvaluateCurrent(payload, target);
        if (before.Status == CurrentPolicyStatus.ProcessExited)
        {
            return ApplyResult(payload, HostManagerProcessPolicyApplyStatus.ProcessExited, 0, 0);
        }
        if (before.Status == CurrentPolicyStatus.Unavailable)
        {
            return ApplyResult(payload, HostManagerProcessPolicyApplyStatus.Unavailable, 0, 0);
        }
        if (before.Status is CurrentPolicyStatus.IdentityChanged or CurrentPolicyStatus.Foreign)
        {
            return ApplyResult(payload, HostManagerProcessPolicyApplyStatus.OwnershipLost, 0, 0);
        }
        if (before.TargetFields == payload.Fields)
        {
            return ApplyResult(payload, HostManagerProcessPolicyApplyStatus.AlreadyApplied, 0, 0);
        }

        var fieldsToApply = payload.Fields & ~before.TargetFields;
        HostManagerProcessPolicyBatchVerification verification;
        try
        {
            verification = VerifyBatchResult(
                policyWriter.TryApplyBatch(
                    [CreateRequest(
                        payload,
                        target,
                        fieldsToApply,
                        useTarget: true,
                        expectedMemoryPriority: before.ObservedMemoryPriority)]),
                payload.ProcessId,
                fieldsToApply);
        }
        catch (Exception exception) when (IsExpectedBoundaryException(exception))
        {
            return ApplyResult(
                payload,
                HostManagerProcessPolicyApplyStatus.StateUncertain,
                fieldsToApply,
                0);
        }

        if (verification.ConflictFields != HostManagerProcessPolicyTransactionFields.None)
        {
            return ApplyResult(
                payload,
                HostManagerProcessPolicyApplyStatus.OwnershipLost,
                fieldsToApply,
                verification.SucceededFields);
        }

        var after = EvaluateCurrent(payload, target);
        if (after.Status == CurrentPolicyStatus.ProcessExited)
        {
            return ApplyResult(
                payload,
                HostManagerProcessPolicyApplyStatus.ProcessExited,
                fieldsToApply,
                verification.SucceededFields);
        }
        if (after.Status == CurrentPolicyStatus.Unavailable)
        {
            return ApplyResult(
                payload,
                HostManagerProcessPolicyApplyStatus.StateUncertain,
                fieldsToApply,
                verification.SucceededFields);
        }
        if (after.Status is CurrentPolicyStatus.IdentityChanged or CurrentPolicyStatus.Foreign)
        {
            return ApplyResult(
                payload,
                HostManagerProcessPolicyApplyStatus.OwnershipLost,
                fieldsToApply,
                verification.SucceededFields);
        }
        if (verification.AllSucceeded(fieldsToApply)
            && after.TargetFields == payload.Fields)
        {
            return ApplyResult(
                payload,
                HostManagerProcessPolicyApplyStatus.Applied,
                fieldsToApply,
                verification.SucceededFields);
        }
        if (verification.ShapeValid
            && verification.SucceededFields == HostManagerProcessPolicyTransactionFields.None
            && after.BaselineFields == before.BaselineFields
            && after.TargetFields == before.TargetFields)
        {
            return ApplyResult(
                payload,
                HostManagerProcessPolicyApplyStatus.Failed,
                fieldsToApply,
                0);
        }

        return ApplyResult(
            payload,
            HostManagerProcessPolicyApplyStatus.StateUncertain,
            fieldsToApply,
            verification.SucceededFields);
    }

    internal HostManagerProcessPolicyRestoreResult Restore(
        ReadOnlySpan<byte> payloadBytes,
        int expectedCurrentGrade)
    {
        if (expectedCurrentGrade == HostManagerProcessGrades.Level4)
        {
            return EmptyRestoreResult(HostManagerProcessPolicyRestoreStatus.Level4Rejected);
        }
        if (!IsSupportedGrade(expectedCurrentGrade))
        {
            return EmptyRestoreResult(HostManagerProcessPolicyRestoreStatus.InvalidGrade);
        }
        Span<int> expectedGrades = stackalloc int[1];
        expectedGrades[0] = expectedCurrentGrade;
        return RestoreKnownTargets(payloadBytes, expectedGrades);
    }

    internal HostManagerProcessPolicyRestoreResult RestoreMemoryPriority(
        ReadOnlySpan<byte> payloadBytes,
        uint expectedCurrentMemoryPriority)
        => RestoreKnownTargets(
            payloadBytes,
            ReadOnlySpan<int>.Empty,
            expectedCurrentMemoryPriority);

    internal HostManagerProcessPolicyInspectionResult InspectMemoryPriority(
        ReadOnlySpan<byte> payloadBytes,
        uint expectedOwnedMemoryPriority)
    {
        if (!HostManagerProcessPolicyRollbackPayloadCodec.TryDecode(
                payloadBytes,
                out var payload))
        {
            return new(
                HostManagerProcessPolicyInspectionStatus.InvalidPayload,
                0,
                0);
        }
        if (payload.Fields != HostManagerProcessPolicyTransactionFields.MemoryPriority
            || expectedOwnedMemoryPriority is < NativeMethods.MemoryPriorityVeryLow
                or > NativeMethods.MemoryPriorityNormal
            || payload.TargetMemoryPriority != expectedOwnedMemoryPriority)
        {
            return new(
                HostManagerProcessPolicyInspectionStatus.InvalidTarget,
                payload.ProcessId,
                payload.ProcessStartKey);
        }

        var current = EvaluateCurrent(
            payload,
            CreateMemoryPriorityTarget(payload, expectedOwnedMemoryPriority));
        var status = current.Status switch
        {
            CurrentPolicyStatus.ProcessExited =>
                HostManagerProcessPolicyInspectionStatus.ProcessExited,
            CurrentPolicyStatus.Unavailable =>
                HostManagerProcessPolicyInspectionStatus.Unavailable,
            CurrentPolicyStatus.IdentityChanged =>
                HostManagerProcessPolicyInspectionStatus.IdentityChanged,
            CurrentPolicyStatus.Foreign =>
                HostManagerProcessPolicyInspectionStatus.Foreign,
            CurrentPolicyStatus.Available
                when current.TargetFields == payload.Fields =>
                HostManagerProcessPolicyInspectionStatus.Owned,
            CurrentPolicyStatus.Available
                when current.BaselineFields == payload.Fields =>
                HostManagerProcessPolicyInspectionStatus.Baseline,
            _ => HostManagerProcessPolicyInspectionStatus.Foreign
        };
        return new(status, payload.ProcessId, payload.ProcessStartKey);
    }

    internal HostManagerProcessPolicyRestoreResult RestoreForRecovery(
        ReadOnlySpan<byte> payloadBytes,
        int fromGrade,
        int toGrade)
    {
        if (!HostManagerProcessPolicyRollbackPayloadCodec.TryDecode(payloadBytes, out var payload))
        {
            return EmptyRestoreResult(HostManagerProcessPolicyRestoreStatus.InvalidPayload);
        }
        if (payload.Fields == HostManagerProcessPolicyTransactionFields.MemoryPriority)
        {
            return payload.TargetMemoryPriority is >= NativeMethods.MemoryPriorityVeryLow
                    and <= NativeMethods.MemoryPriorityNormal
                ? RestoreKnownTargets(
                    payload,
                    ReadOnlySpan<int>.Empty,
                    payload.TargetMemoryPriority)
                : RestoreResult(
                    payload,
                    HostManagerProcessPolicyRestoreStatus.InvalidTarget,
                    0,
                    0);
        }

        Span<int> expectedGrades = stackalloc int[2];
        var count = 0;
        if (!TryAppendRecoveryGrade(fromGrade, expectedGrades, ref count) ||
            !TryAppendRecoveryGrade(toGrade, expectedGrades, ref count))
        {
            return fromGrade == HostManagerProcessGrades.Level4 ||
                toGrade == HostManagerProcessGrades.Level4
                    ? EmptyRestoreResult(HostManagerProcessPolicyRestoreStatus.Level4Rejected)
                    : EmptyRestoreResult(HostManagerProcessPolicyRestoreStatus.InvalidGrade);
        }
        return RestoreKnownTargets(payload, expectedGrades[..count]);
    }

    internal HostManagerProcessPolicyInspectionResult InspectForRecovery(
        ReadOnlySpan<byte> payloadBytes,
        int fromGrade,
        int toGrade)
    {
        if (!HostManagerProcessPolicyRollbackPayloadCodec.TryDecode(
                payloadBytes,
                out var payload))
        {
            return new(
                HostManagerProcessPolicyInspectionStatus.InvalidPayload,
                0,
                0);
        }

        Span<TargetPolicy> expectedTargets = stackalloc TargetPolicy[2];
        var targetCount = 0;
        if (payload.Fields == HostManagerProcessPolicyTransactionFields.MemoryPriority)
        {
            if (payload.TargetMemoryPriority is < NativeMethods.MemoryPriorityVeryLow
                or > NativeMethods.MemoryPriorityNormal)
            {
                return new(
                    HostManagerProcessPolicyInspectionStatus.InvalidTarget,
                    payload.ProcessId,
                    payload.ProcessStartKey);
            }
            expectedTargets[0] = CreateMemoryPriorityTarget(
                payload,
                payload.TargetMemoryPriority);
            targetCount = 1;
        }
        else
        {
            Span<int> expectedGrades = stackalloc int[2];
            if (!TryAppendRecoveryGrade(fromGrade, expectedGrades, ref targetCount)
                || !TryAppendRecoveryGrade(toGrade, expectedGrades, ref targetCount))
            {
                return new(
                    HostManagerProcessPolicyInspectionStatus.InvalidTarget,
                    payload.ProcessId,
                    payload.ProcessStartKey);
            }
            for (var index = 0; index < targetCount; index++)
            {
                expectedTargets[index] = CreateTargetPolicy(payload, expectedGrades[index]);
            }
        }

        var current = EvaluateCurrent(payload, expectedTargets[..targetCount]);
        var status = current.Status switch
        {
            CurrentPolicyStatus.ProcessExited =>
                HostManagerProcessPolicyInspectionStatus.ProcessExited,
            CurrentPolicyStatus.Unavailable =>
                HostManagerProcessPolicyInspectionStatus.Unavailable,
            CurrentPolicyStatus.IdentityChanged =>
                HostManagerProcessPolicyInspectionStatus.IdentityChanged,
            CurrentPolicyStatus.Foreign =>
                HostManagerProcessPolicyInspectionStatus.Foreign,
            CurrentPolicyStatus.Available
                when current.BaselineFields == payload.Fields =>
                HostManagerProcessPolicyInspectionStatus.Baseline,
            CurrentPolicyStatus.Available
                when current.TargetFields != HostManagerProcessPolicyTransactionFields.None
                    && (current.BaselineFields | current.TargetFields) == payload.Fields =>
                HostManagerProcessPolicyInspectionStatus.Owned,
            _ => HostManagerProcessPolicyInspectionStatus.Foreign
        };
        return new(status, payload.ProcessId, payload.ProcessStartKey);
    }

    private HostManagerProcessPolicyRestoreResult RestoreKnownTargets(
        ReadOnlySpan<byte> payloadBytes,
        ReadOnlySpan<int> expectedCurrentGrades,
        uint? expectedCurrentMemoryPriority = null)
    {
        if (!HostManagerProcessPolicyRollbackPayloadCodec.TryDecode(payloadBytes, out var payload))
        {
            return EmptyRestoreResult(HostManagerProcessPolicyRestoreStatus.InvalidPayload);
        }
        return RestoreKnownTargets(payload, expectedCurrentGrades, expectedCurrentMemoryPriority);
    }

    private HostManagerProcessPolicyRestoreResult RestoreKnownTargets(
        HostManagerProcessPolicyRollbackPayload payload,
        ReadOnlySpan<int> expectedCurrentGrades,
        uint? expectedCurrentMemoryPriority = null)
    {

        Span<TargetPolicy> expectedTargets = stackalloc TargetPolicy[2];
        var targetCount = expectedCurrentGrades.Length;
        if (expectedCurrentMemoryPriority is uint memoryPriority)
        {
            if (memoryPriority is < NativeMethods.MemoryPriorityVeryLow
                    or > NativeMethods.MemoryPriorityNormal
                || payload.Fields != HostManagerProcessPolicyTransactionFields.MemoryPriority
                || payload.TargetMemoryPriority != memoryPriority)
            {
                return RestoreResult(
                    payload,
                    HostManagerProcessPolicyRestoreStatus.InvalidTarget,
                    0,
                    0);
            }
            expectedTargets[0] = CreateMemoryPriorityTarget(payload, memoryPriority);
            targetCount = 1;
        }
        else
        {
            if (payload.Fields == HostManagerProcessPolicyTransactionFields.MemoryPriority)
            {
                return RestoreResult(
                    payload,
                    HostManagerProcessPolicyRestoreStatus.InvalidTarget,
                    0,
                    0);
            }
            for (var index = 0; index < expectedCurrentGrades.Length; index++)
            {
                expectedTargets[index] = CreateTargetPolicy(payload, expectedCurrentGrades[index]);
            }
        }
        var activeTargets = expectedTargets[..targetCount];
        var before = EvaluateCurrent(payload, activeTargets);
        if (before.Status == CurrentPolicyStatus.ProcessExited)
        {
            return RestoreResult(payload, HostManagerProcessPolicyRestoreStatus.ProcessExited, 0, 0);
        }
        if (before.Status == CurrentPolicyStatus.Unavailable)
        {
            return RestoreResult(payload, HostManagerProcessPolicyRestoreStatus.Unavailable, 0, 0);
        }
        if (before.Status is CurrentPolicyStatus.IdentityChanged or CurrentPolicyStatus.Foreign)
        {
            return RestoreResult(payload, HostManagerProcessPolicyRestoreStatus.OwnershipLost, 0, 0);
        }
        if (before.BaselineFields == payload.Fields)
        {
            return RestoreResult(payload, HostManagerProcessPolicyRestoreStatus.AlreadyRestored, 0, 0);
        }

        var fieldsToRestore = payload.Fields & ~before.BaselineFields;
        HostManagerProcessPolicyBatchVerification verification;
        try
        {
            var requestTarget = activeTargets.IsEmpty ? default : activeTargets[0];
            verification = VerifyBatchResult(
                policyWriter.TryApplyBatch(
                    [CreateRequest(
                        payload,
                        requestTarget,
                        fieldsToRestore,
                        useTarget: false,
                        expectedMemoryPriority: before.ObservedMemoryPriority)]),
                payload.ProcessId,
                fieldsToRestore);
        }
        catch (Exception exception) when (IsExpectedBoundaryException(exception))
        {
            return RestoreResult(
                payload,
                HostManagerProcessPolicyRestoreStatus.StateUncertain,
                fieldsToRestore,
                0);
        }

        if (verification.ConflictFields != HostManagerProcessPolicyTransactionFields.None)
        {
            return RestoreResult(
                payload,
                HostManagerProcessPolicyRestoreStatus.OwnershipLost,
                fieldsToRestore,
                verification.SucceededFields);
        }

        var after = EvaluateCurrent(payload, activeTargets);
        if (after.Status == CurrentPolicyStatus.ProcessExited)
        {
            return RestoreResult(
                payload,
                HostManagerProcessPolicyRestoreStatus.ProcessExited,
                fieldsToRestore,
                verification.SucceededFields);
        }
        if (after.Status == CurrentPolicyStatus.Unavailable)
        {
            return RestoreResult(
                payload,
                HostManagerProcessPolicyRestoreStatus.StateUncertain,
                fieldsToRestore,
                verification.SucceededFields);
        }
        if (after.Status is CurrentPolicyStatus.IdentityChanged or CurrentPolicyStatus.Foreign)
        {
            return RestoreResult(
                payload,
                HostManagerProcessPolicyRestoreStatus.OwnershipLost,
                fieldsToRestore,
                verification.SucceededFields);
        }
        if (verification.AllSucceeded(fieldsToRestore)
            && after.BaselineFields == payload.Fields)
        {
            return RestoreResult(
                payload,
                HostManagerProcessPolicyRestoreStatus.Restored,
                fieldsToRestore,
                verification.SucceededFields);
        }
        if (verification.ShapeValid
            && verification.SucceededFields == HostManagerProcessPolicyTransactionFields.None
            && after.BaselineFields == before.BaselineFields
            && after.TargetFields == before.TargetFields)
        {
            return RestoreResult(
                payload,
                HostManagerProcessPolicyRestoreStatus.Failed,
                fieldsToRestore,
                0);
        }

        return RestoreResult(
            payload,
            HostManagerProcessPolicyRestoreStatus.StateUncertain,
            fieldsToRestore,
            verification.SucceededFields);
    }

    private static bool TryAppendRecoveryGrade(
        int grade,
        Span<int> destination,
        ref int count)
    {
        if (grade == HostManagerProcessGrades.Normal)
        {
            return true;
        }
        if (!IsSupportedGrade(grade))
        {
            return false;
        }
        for (var index = 0; index < count; index++)
        {
            if (destination[index] == grade)
            {
                return true;
            }
        }
        destination[count++] = grade;
        return true;
    }

    private static bool TryCreatePayload(
        ProcessResourcePolicySnapshot snapshot,
        long processStartKey,
        HostManagerProcessPolicyTransactionFields fields,
        uint targetMemoryPriority,
        out HostManagerProcessPolicyRollbackPayload payload)
    {
        payload = default;
        var priority = default(ProcessPriorityClass);
        var memory = 0U;
        var powerControl = 0U;
        var powerState = 0U;
        if (fields.HasFlag(HostManagerProcessPolicyTransactionFields.PriorityClass)
                && (!Enum.TryParse(snapshot.PriorityClass, true, out priority)
                    || !Enum.IsDefined(priority))
            || fields.HasFlag(HostManagerProcessPolicyTransactionFields.MemoryPriority)
                && (snapshot.MemoryPriority is not uint capturedMemory
                    || capturedMemory is < NativeMethods.MemoryPriorityVeryLow
                        or > NativeMethods.MemoryPriorityNormal)
            || fields.HasFlag(HostManagerProcessPolicyTransactionFields.PowerThrottling)
                && (snapshot.PowerThrottlingControlMask is not uint capturedPowerControl
                    || snapshot.PowerThrottlingStateMask is not uint capturedPowerState))
        {
            return false;
        }

        if (fields.HasFlag(HostManagerProcessPolicyTransactionFields.MemoryPriority))
        {
            memory = snapshot.MemoryPriority!.Value;
        }
        if (fields.HasFlag(HostManagerProcessPolicyTransactionFields.PowerThrottling))
        {
            powerControl = snapshot.PowerThrottlingControlMask!.Value;
            powerState = snapshot.PowerThrottlingStateMask!.Value;
        }

        payload = new HostManagerProcessPolicyRollbackPayload(
            fields,
            snapshot.ProcessId,
            processStartKey,
            checked((uint)priority),
            memory,
            powerControl,
            powerState,
            targetMemoryPriority);
        return true;
    }

    private CurrentPolicyEvaluation EvaluateCurrent(
        HostManagerProcessPolicyRollbackPayload payload,
        TargetPolicy target)
    {
        Span<TargetPolicy> targets = stackalloc TargetPolicy[1];
        targets[0] = target;
        return EvaluateCurrent(payload, targets);
    }

    private CurrentPolicyEvaluation EvaluateCurrent(
        HostManagerProcessPolicyRollbackPayload payload,
        ReadOnlySpan<TargetPolicy> targets)
    {
        RecoveryReadResult<ProcessInstanceRecoverySnapshot> identityRead;
        try
        {
            identityRead = policyWriter.ReadProcessInstanceForRecovery(payload.ProcessId);
        }
        catch (Exception exception) when (IsExpectedBoundaryException(exception))
        {
            return CurrentPolicyEvaluation.WithStatus(CurrentPolicyStatus.Unavailable);
        }

        if (identityRead.Status == RecoveryReadStatus.NotFoundOrExited)
        {
            return CurrentPolicyEvaluation.WithStatus(CurrentPolicyStatus.ProcessExited);
        }
        if (identityRead.Status != RecoveryReadStatus.Found ||
            identityRead.Value is not ProcessInstanceRecoverySnapshot identity)
        {
            return CurrentPolicyEvaluation.WithStatus(CurrentPolicyStatus.Unavailable);
        }
        if (identity.ProcessId != payload.ProcessId ||
            !TryGetProcessStartKey(identity.StartedAt, out var recoveryStartKey) ||
            recoveryStartKey != payload.ProcessStartKey)
        {
            return CurrentPolicyEvaluation.WithStatus(CurrentPolicyStatus.IdentityChanged);
        }

        ProcessResourcePolicySnapshot? snapshot;
        try
        {
            snapshot = policyWriter.TryReadProcess(payload.ProcessId);
        }
        catch (Exception exception) when (IsExpectedBoundaryException(exception))
        {
            return CurrentPolicyEvaluation.WithStatus(CurrentPolicyStatus.Unavailable);
        }

        if (snapshot is null || snapshot.StartedAt is null)
        {
            return CurrentPolicyEvaluation.WithStatus(CurrentPolicyStatus.Unavailable);
        }
        if (snapshot.ProcessId != payload.ProcessId
            || !TryGetProcessStartKey(snapshot.StartedAt.Value, out var processStartKey)
            || processStartKey != payload.ProcessStartKey)
        {
            return CurrentPolicyEvaluation.WithStatus(CurrentPolicyStatus.IdentityChanged);
        }

        var baselineFields = HostManagerProcessPolicyTransactionFields.None;
        var targetFields = HostManagerProcessPolicyTransactionFields.None;
        foreach (var field in OrderedFields)
        {
            if (!payload.Fields.HasFlag(field))
            {
                continue;
            }
            var matchesBaseline = false;
            var matchesKnownTarget = false;
            if (targets.IsEmpty)
            {
                var baselineMatch = MatchField(snapshot, payload, default, field);
                if (baselineMatch == CurrentFieldMatch.Unavailable)
                {
                    return CurrentPolicyEvaluation.WithStatus(CurrentPolicyStatus.Unavailable);
                }
                matchesBaseline = baselineMatch is CurrentFieldMatch.Baseline or
                    CurrentFieldMatch.Both;
            }
            else
            {
                foreach (var target in targets)
                {
                    var match = MatchField(snapshot, payload, target, field);
                    if (match == CurrentFieldMatch.Unavailable)
                    {
                        return CurrentPolicyEvaluation.WithStatus(
                            CurrentPolicyStatus.Unavailable);
                    }
                    matchesBaseline |= match is CurrentFieldMatch.Baseline or
                        CurrentFieldMatch.Both;
                    matchesKnownTarget |= match is CurrentFieldMatch.Target or
                        CurrentFieldMatch.Both;
                }
            }
            if (!matchesBaseline && !matchesKnownTarget)
            {
                return CurrentPolicyEvaluation.WithStatus(CurrentPolicyStatus.Foreign);
            }
            if (matchesBaseline)
            {
                baselineFields |= field;
            }
            if (matchesKnownTarget)
            {
                targetFields |= field;
            }
        }

        return new CurrentPolicyEvaluation(
            CurrentPolicyStatus.Available,
            baselineFields,
            targetFields,
            snapshot.MemoryPriority);
    }

    private static CurrentFieldMatch MatchField(
        ProcessResourcePolicySnapshot snapshot,
        HostManagerProcessPolicyRollbackPayload payload,
        TargetPolicy target,
        HostManagerProcessPolicyTransactionFields field)
    {
        switch (field)
        {
            case HostManagerProcessPolicyTransactionFields.PriorityClass:
                if (!Enum.TryParse<ProcessPriorityClass>(snapshot.PriorityClass, true, out var priority)
                    || !Enum.IsDefined(priority))
                {
                    return CurrentFieldMatch.Unavailable;
                }
                return MatchValue(
                    checked((uint)priority),
                    payload.BaselinePriorityClass,
                    target.PriorityClass);
            case HostManagerProcessPolicyTransactionFields.MemoryPriority:
                return snapshot.MemoryPriority is uint memory
                    ? MatchValue(memory, payload.BaselineMemoryPriority, target.MemoryPriority)
                    : CurrentFieldMatch.Unavailable;
            case HostManagerProcessPolicyTransactionFields.PowerThrottling:
                if (snapshot.PowerThrottlingControlMask is not uint control
                    || snapshot.PowerThrottlingStateMask is not uint state)
                {
                    return CurrentFieldMatch.Unavailable;
                }
                return MatchPower(
                    control,
                    state,
                    payload.BaselinePowerControlMask,
                    payload.BaselinePowerStateMask,
                    target.PowerControlMask,
                    target.PowerStateMask);
            default:
                return CurrentFieldMatch.Foreign;
        }
    }

    private static CurrentFieldMatch MatchValue<T>(T current, T baseline, T target)
        where T : IEquatable<T>
    {
        var matchesBaseline = current.Equals(baseline);
        var matchesTarget = current.Equals(target);
        return (matchesBaseline, matchesTarget) switch
        {
            (true, true) => CurrentFieldMatch.Both,
            (true, false) => CurrentFieldMatch.Baseline,
            (false, true) => CurrentFieldMatch.Target,
            _ => CurrentFieldMatch.Foreign
        };
    }

    private static CurrentFieldMatch MatchPower(
        uint control,
        uint state,
        uint baselineControl,
        uint baselineState,
        uint targetControl,
        uint targetState)
    {
        var matchesBaseline = control == baselineControl && state == baselineState;
        var matchesTarget = control == targetControl && state == targetState;
        return (matchesBaseline, matchesTarget) switch
        {
            (true, true) => CurrentFieldMatch.Both,
            (true, false) => CurrentFieldMatch.Baseline,
            (false, true) => CurrentFieldMatch.Target,
            _ => CurrentFieldMatch.Foreign
        };
    }

    private static ProcessResourcePolicyBatchRequest CreateRequest(
        HostManagerProcessPolicyRollbackPayload payload,
        TargetPolicy target,
        HostManagerProcessPolicyTransactionFields fields,
        bool useTarget,
        uint? expectedMemoryPriority)
    {
        var priorityValue = useTarget ? target.PriorityClass : payload.BaselinePriorityClass;
        var memoryValue = useTarget ? target.MemoryPriority : payload.BaselineMemoryPriority;
        var powerControlValue = useTarget ? target.PowerControlMask : payload.BaselinePowerControlMask;
        var powerStateValue = useTarget ? target.PowerStateMask : payload.BaselinePowerStateMask;

        var includesMemoryPriority =
            (fields & HostManagerProcessPolicyTransactionFields.MemoryPriority) != 0;
        return new ProcessResourcePolicyBatchRequest(
            ProcessId: payload.ProcessId,
            ExpectedStartedAt: DateTimeOffset.FromFileTime(payload.ProcessStartKey),
            PriorityClass: (fields & HostManagerProcessPolicyTransactionFields.PriorityClass) != 0
                ? ((ProcessPriorityClass)checked((int)priorityValue)).ToString()
                : null,
            MemoryPriority: includesMemoryPriority
                ? memoryValue
                : null,
            PowerControlMask: (fields & HostManagerProcessPolicyTransactionFields.PowerThrottling) != 0
                ? powerControlValue
                : null,
            PowerStateMask: (fields & HostManagerProcessPolicyTransactionFields.PowerThrottling) != 0
                ? powerStateValue
                : null,
            ExpectedMemoryPriority: includesMemoryPriority
                ? expectedMemoryPriority
                : null);
    }

    private static HostManagerProcessPolicyBatchVerification VerifyBatchResult(
        IReadOnlyList<ProcessResourcePolicyBatchWriteResult>? results,
        int processId,
        HostManagerProcessPolicyTransactionFields requestedFields)
    {
        if (results is null || results.Count != 1 || results[0].ProcessId != processId)
        {
            return new HostManagerProcessPolicyBatchVerification(false, 0, 0);
        }

        var expectedCount = BitOperations.PopCount((uint)requestedFields);
        var shapeValid = results[0].Fields.Count == expectedCount;
        var observed = HostManagerProcessPolicyTransactionFields.None;
        var succeeded = HostManagerProcessPolicyTransactionFields.None;
        var conflicts = HostManagerProcessPolicyTransactionFields.None;
        foreach (var result in results[0].Fields)
        {
            if (!HostManagerProcessPolicyTransactionFieldMapping.TryFromBatchField(result.Field, out var field)
                || (requestedFields & field) == 0
                || (observed & field) != 0)
            {
                return new HostManagerProcessPolicyBatchVerification(
                    false,
                    succeeded,
                    conflicts);
            }
            observed |= field;
            if (result.Succeeded)
            {
                succeeded |= field;
            }
            else if (result.Conflict)
            {
                conflicts |= field;
            }
        }

        return new HostManagerProcessPolicyBatchVerification(
            shapeValid && observed == requestedFields,
            succeeded,
            conflicts);
    }

    private static bool TryReadIdentity(
        ProcessResourcePolicySnapshot? snapshot,
        int expectedProcessId,
        out long processStartKey)
    {
        processStartKey = 0;
        return snapshot is not null
            && snapshot.ProcessId == expectedProcessId
            && snapshot.StartedAt is DateTimeOffset startedAt
            && TryGetProcessStartKey(startedAt, out processStartKey);
    }

    private static bool TryGetProcessStartKey(DateTimeOffset startedAt, out long processStartKey)
    {
        try
        {
            processStartKey = startedAt.ToFileTime();
            return processStartKey > 0;
        }
        catch (ArgumentOutOfRangeException)
        {
            processStartKey = 0;
            return false;
        }
    }

    private static TargetPolicy CreateTargetPolicy(
        HostManagerProcessPolicyRollbackPayload payload,
        int grade)
    {
        var powerBit = NativeMethods.ProcessPowerThrottlingExecutionSpeed;
        return new TargetPolicy(
            checked((uint)TargetPriority(grade)),
            grade == HostManagerProcessGrades.A1
                ? payload.BaselineMemoryPriority
                : TargetMemoryPriority(grade),
            payload.BaselinePowerControlMask | powerBit,
            grade == HostManagerProcessGrades.A1
                ? payload.BaselinePowerStateMask & ~powerBit
                : payload.BaselinePowerStateMask | powerBit);
    }

    private static bool IsSupportedGrade(int grade) => grade is
        HostManagerProcessGrades.A1
        or HostManagerProcessGrades.Level1
        or HostManagerProcessGrades.Level2
        or HostManagerProcessGrades.Level3;

    private static ProcessPriorityClass TargetPriority(int grade) => grade switch
    {
        HostManagerProcessGrades.A1 => ProcessPriorityClass.High,
        HostManagerProcessGrades.Level1 => ProcessPriorityClass.Normal,
        HostManagerProcessGrades.Level2 => ProcessPriorityClass.BelowNormal,
        HostManagerProcessGrades.Level3 => ProcessPriorityClass.Idle,
            _ => throw new ArgumentOutOfRangeException(nameof(grade))
        };

    private static TargetPolicy CreateMemoryPriorityTarget(
        HostManagerProcessPolicyRollbackPayload payload,
        uint memoryPriority)
        => new(
            payload.BaselinePriorityClass,
            memoryPriority,
            payload.BaselinePowerControlMask,
            payload.BaselinePowerStateMask);

    private static bool IsSupportedFieldScope(HostManagerProcessPolicyTransactionFields fields)
        => fields != HostManagerProcessPolicyTransactionFields.None
            && (fields & ~HostManagerProcessPolicyTransactionFields.All) == 0;

    private static uint TargetMemoryPriority(int grade) => grade switch
    {
        HostManagerProcessGrades.Level1 => NativeMethods.MemoryPriorityBelowNormal,
        HostManagerProcessGrades.Level2 => NativeMethods.MemoryPriorityLow,
        HostManagerProcessGrades.Level3 => NativeMethods.MemoryPriorityVeryLow,
        _ => throw new ArgumentOutOfRangeException(nameof(grade))
    };

    private static HostManagerProcessPolicyApplyResult EmptyApplyResult(
        HostManagerProcessPolicyApplyStatus status) =>
        new(status, 0, 0, 0, 0);

    private static HostManagerProcessPolicyApplyResult ApplyResult(
        HostManagerProcessPolicyRollbackPayload payload,
        HostManagerProcessPolicyApplyStatus status,
        HostManagerProcessPolicyTransactionFields requestedFields,
        HostManagerProcessPolicyTransactionFields succeededFields) =>
        new(status, requestedFields, succeededFields, payload.ProcessId, payload.ProcessStartKey);

    private static HostManagerProcessPolicyRestoreResult EmptyRestoreResult(
        HostManagerProcessPolicyRestoreStatus status) =>
        new(status, 0, 0, 0, 0);

    private static HostManagerProcessPolicyRestoreResult RestoreResult(
        HostManagerProcessPolicyRollbackPayload payload,
        HostManagerProcessPolicyRestoreStatus status,
        HostManagerProcessPolicyTransactionFields requestedFields,
        HostManagerProcessPolicyTransactionFields restoredFields) =>
        new(status, requestedFields, restoredFields, payload.ProcessId, payload.ProcessStartKey);

    private static bool IsExpectedBoundaryException(Exception exception) => exception is
        InvalidOperationException
        or DllNotFoundException
        or EntryPointNotFoundException
        or BadImageFormatException
        or TypeInitializationException;

    private static readonly HostManagerProcessPolicyTransactionFields[] OrderedFields =
    [
        HostManagerProcessPolicyTransactionFields.PriorityClass,
        HostManagerProcessPolicyTransactionFields.MemoryPriority,
        HostManagerProcessPolicyTransactionFields.PowerThrottling
    ];

    private enum CurrentPolicyStatus : byte
    {
        Available = 1,
        Unavailable = 2,
        IdentityChanged = 3,
        Foreign = 4,
        ProcessExited = 5
    }

    private enum CurrentFieldMatch : byte
    {
        Baseline = 1,
        Target = 2,
        Both = 3,
        Unavailable = 4,
        Foreign = 5
    }

    private readonly record struct TargetPolicy(
        uint PriorityClass,
        uint MemoryPriority,
        uint PowerControlMask,
        uint PowerStateMask);

    private readonly record struct CurrentPolicyEvaluation(
        CurrentPolicyStatus Status,
        HostManagerProcessPolicyTransactionFields BaselineFields,
        HostManagerProcessPolicyTransactionFields TargetFields,
        uint? ObservedMemoryPriority)
    {
        internal static CurrentPolicyEvaluation WithStatus(CurrentPolicyStatus status) =>
            new(status, 0, 0, null);
    }
}
