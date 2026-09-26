using ResourceManager.App.Application.Optimization;

namespace ResourceManager.App.Infrastructure.Optimization.Transactions;

[Flags]
internal enum HostManagerProcessPolicyTransactionFields : uint
{
    None = 0,
    PriorityClass = 1 << 0,
    MemoryPriority = 1 << 1,
    PowerThrottling = 1 << 2,
    CpuPolicy = PriorityClass | PowerThrottling,
    MemoryPolicy = MemoryPriority,
    All = CpuPolicy | MemoryPolicy
}

internal enum HostManagerProcessPolicyCaptureStatus : byte
{
    Captured = 1,
    InvalidRequest = 2,
    Unavailable = 3
}

internal enum HostManagerProcessPolicyApplyStatus : byte
{
    Applied = 1,
    AlreadyApplied = 2,
    InvalidPayload = 3,
    InvalidGrade = 4,
    Level4Rejected = 5,
    Unavailable = 6,
    OwnershipLost = 7,
    Failed = 8,
    StateUncertain = 9,
    ProcessExited = 10,
    InvalidTarget = 11
}

internal enum HostManagerProcessPolicyRestoreStatus : byte
{
    Restored = 1,
    AlreadyRestored = 2,
    InvalidPayload = 3,
    InvalidGrade = 4,
    Level4Rejected = 5,
    Unavailable = 6,
    OwnershipLost = 7,
    Failed = 8,
    StateUncertain = 9,
    ProcessExited = 10,
    InvalidTarget = 11
}

internal enum HostManagerProcessPolicyInspectionStatus : byte
{
    Owned = 1,
    Baseline = 2,
    Foreign = 3,
    Unavailable = 4,
    IdentityChanged = 5,
    ProcessExited = 6,
    InvalidPayload = 7,
    InvalidTarget = 8
}

internal readonly record struct HostManagerProcessPolicyCaptureResult(
    HostManagerProcessPolicyCaptureStatus Status,
    HostManagerProcessPolicyTransactionFields Fields,
    int ProcessId,
    long ProcessStartKey,
    byte[] Payload)
{
    internal static HostManagerProcessPolicyCaptureResult Empty(
        HostManagerProcessPolicyCaptureStatus status,
        int processId,
        long processStartKey) =>
        new(status, HostManagerProcessPolicyTransactionFields.None, processId, processStartKey, []);
}

internal readonly record struct HostManagerProcessPolicyApplyResult(
    HostManagerProcessPolicyApplyStatus Status,
    HostManagerProcessPolicyTransactionFields RequestedFields,
    HostManagerProcessPolicyTransactionFields SucceededFields,
    int ProcessId,
    long ProcessStartKey);

internal readonly record struct HostManagerProcessPolicyRestoreResult(
    HostManagerProcessPolicyRestoreStatus Status,
    HostManagerProcessPolicyTransactionFields RequestedFields,
    HostManagerProcessPolicyTransactionFields RestoredFields,
    int ProcessId,
    long ProcessStartKey);

internal readonly record struct HostManagerProcessPolicyInspectionResult(
    HostManagerProcessPolicyInspectionStatus Status,
    int ProcessId,
    long ProcessStartKey);

internal readonly record struct HostManagerProcessPolicyRollbackPayload(
    HostManagerProcessPolicyTransactionFields Fields,
    int ProcessId,
    long ProcessStartKey,
    uint BaselinePriorityClass,
    uint BaselineMemoryPriority,
    uint BaselinePowerControlMask,
    uint BaselinePowerStateMask,
    uint TargetMemoryPriority);

internal readonly record struct HostManagerProcessPolicyBatchVerification(
    bool ShapeValid,
    HostManagerProcessPolicyTransactionFields SucceededFields,
    HostManagerProcessPolicyTransactionFields ConflictFields)
{
    internal bool AllSucceeded(HostManagerProcessPolicyTransactionFields requestedFields) =>
        ShapeValid && SucceededFields == requestedFields;
}

internal static class HostManagerProcessPolicyTransactionFieldMapping
{
    internal static bool TryFromBatchField(
        ProcessResourcePolicyBatchFields field,
        out HostManagerProcessPolicyTransactionFields result)
    {
        result = field switch
        {
            ProcessResourcePolicyBatchFields.PriorityClass => HostManagerProcessPolicyTransactionFields.PriorityClass,
            ProcessResourcePolicyBatchFields.MemoryPriority => HostManagerProcessPolicyTransactionFields.MemoryPriority,
            ProcessResourcePolicyBatchFields.PowerThrottling => HostManagerProcessPolicyTransactionFields.PowerThrottling,
            _ => HostManagerProcessPolicyTransactionFields.None
        };
        return result != HostManagerProcessPolicyTransactionFields.None;
    }
}
