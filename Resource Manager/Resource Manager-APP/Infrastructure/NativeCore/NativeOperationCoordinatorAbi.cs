using System.Runtime.InteropServices;

namespace ResourceManager.App.Infrastructure.NativeCore;

internal static class NativeOperationCoordinatorAbi
{
    public const uint Version = 0x0001_0000;
    public const uint NoSlot = uint.MaxValue;
}

internal enum NativeOperationCoordinatorStatus : int
{
    Ok = 0,
    InvalidArgument = 1,
    AbiMismatch = 2,
    Unavailable = 3,
    NoData = 4,
    BufferTooSmall = 5,
    StaleFrame = 6,
    OutOfMemory = 7
}

internal enum NativeOperationState : uint
{
    Queued = 1,
    StartPending = 2,
    Running = 3,
    CancelPending = 4,
    RetryWait = 5,
    RecoveryPending = 6,
    Succeeded = 7,
    Failed = 8,
    Canceled = 9,
    StateUncertain = 10
}

internal enum NativeOperationActionKind : uint
{
    Start = 1,
    Cancel = 2,
    Recover = 3
}

internal enum NativeOperationActionFeedbackOutcome : uint
{
    Started = 1,
    StartRetryableFailure = 2,
    StartTerminalFailure = 3,
    CancelCompleted = 4,
    CancelRetryableFailure = 5,
    RecoveredQueued = 6,
    RecoveredSucceeded = 7,
    RecoveredFailed = 8,
    RecoveredCanceled = 9,
    RecoveredUncertain = 10
}

internal enum NativeOperationCompletionOutcome : uint
{
    Succeeded = 1,
    RetryableFailure = 2,
    TerminalFailure = 3,
    Canceled = 4,
    StateUncertain = 5
}

[Flags]
internal enum NativeOperationSubmitValidity : ulong
{
    None = 0,
    Domain = 1UL << 0,
    Title = 1UL << 1,
    Request = 1UL << 2,
    Known = Domain | Title | Request
}

[Flags]
internal enum NativeOperationFlags : ulong
{
    None = 0,
    DomainValid = 1UL << 0,
    TitleValid = 1UL << 1,
    RequestValid = 1UL << 2,
    ResultValid = 1UL << 3,
    ErrorValid = 1UL << 4,
    ProgressValid = 1UL << 5,
    CancelRequested = 1UL << 6,
    Terminal = 1UL << 7,
    ImportedRecovery = 1UL << 8,
    Known = DomainValid | TitleValid | RequestValid | ResultValid | ErrorValid
        | ProgressValid | CancelRequested | Terminal | ImportedRecovery
}

[Flags]
internal enum NativeOperationSubmitOutputFlags : uint
{
    None = 0,
    Inserted = 1U << 0,
    ActiveDomainDuplicate = 1U << 1,
    OperationIdDuplicate = 1U << 2,
    Known = Inserted | ActiveDomainDuplicate | OperationIdDuplicate
}

[Flags]
internal enum NativeOperationActionFlags : ulong
{
    None = 0,
    CancelRequested = 1UL << 0,
    ExecutionTimeout = 1UL << 1,
    ImportedRecovery = 1UL << 2,
    Known = CancelRequested | ExecutionTimeout | ImportedRecovery
}

[Flags]
internal enum NativeOperationResultValidity : ulong
{
    None = 0,
    Result = 1UL << 0,
    Error = 1UL << 1,
    Known = Result | Error
}

[Flags]
internal enum NativeOperationProgressValidity : ulong
{
    None = 0,
    PercentMilli = 1UL << 0,
    BytesDone = 1UL << 1,
    BytesTotal = 1UL << 2,
    SpeedBytesPerSecond = 1UL << 3,
    Stage = 1UL << 4,
    Message = 1UL << 5,
    Checkpoint = 1UL << 6,
    Known = PercentMilli | BytesDone | BytesTotal | SpeedBytesPerSecond
        | Stage | Message | Checkpoint
}

[Flags]
internal enum NativeOperationSnapshotFlags : ulong
{
    None = 0,
    NextWakeValid = 1UL << 0,
    Known = NextWakeValid
}

[StructLayout(LayoutKind.Sequential)]
internal readonly record struct NativeOperationHandle128(ulong High, ulong Low)
{
    public bool IsZero => High == 0 && Low == 0;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeOperationCoordinatorConfiguration
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong Generation;
    public NativeOperationHandle128 SessionInstanceId;
    public NativeOperationHandle128 ClockInstanceId;
    public uint MaximumOperationCount;
    public uint MaximumDomainCount;
    public uint MaximumActionCount;
    public uint OperationIndexCapacity;
    public uint DomainIndexCapacity;
    public uint MaximumGlobalRunningCount;
    public uint MaximumRecentTerminalCount;
    public uint MaximumReadCount;
    public uint MaximumStartActionsPerPlan;
    public uint MaximumCancelActionsPerPlan;
    public uint MaximumRecoverActionsPerPlan;
    public uint ReservedU32;
    public ulong MaximumFutureSkewMilliseconds;
    public ulong MaximumPersistenceByteCount;
    public ulong ResidentByteBudget;
    public ulong Flags;
    public fixed ulong Reserved[5];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeOperationCoordinatorCapacity
{
    public uint StructSize;
    public uint OperationCapacity;
    public uint DomainCapacity;
    public uint ActionCapacity;
    public uint OperationIndexCapacity;
    public uint DomainIndexCapacity;
    public uint OrderScratchCapacity;
    public uint PersistenceCapacity;
    public NativeOperationHandle128 SessionInstanceId;
    public NativeOperationHandle128 ClockInstanceId;
    public ulong MaximumPersistenceByteCount;
    public ulong ResidentByteCount;
    public fixed ulong Reserved[4];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeOperationSubmitInput
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong ConfigurationGeneration;
    public ulong SubmitEpoch;
    public NativeOperationHandle128 OperationId;
    public NativeOperationHandle128 DomainId;
    public NativeOperationHandle128 KindHandle;
    public NativeOperationHandle128 TitleHandle;
    public NativeOperationHandle128 RequestHandle;
    public long Priority;
    public uint MaximumAttempts;
    public uint RetryDelayMilliseconds;
    public ulong ExecutionTimeoutMilliseconds;
    public ulong CancelGraceMilliseconds;
    public ulong TerminalRetentionMilliseconds;
    public long CreatedUtcMilliseconds;
    public ulong CreatedMonotonicMilliseconds;
    public long ObservedUtcMilliseconds;
    public ulong ObservedMonotonicMilliseconds;
    public ulong ValidMask;
    public ulong Flags;
    public fixed ulong Reserved[4];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeOperationSubmitOutput
{
    public uint StructSize;
    public uint Flags;
    public NativeOperationHandle128 OperationId;
    public ulong StateRevision;
    public ulong SubmitSequence;
    public uint State;
    public uint ReservedU32;
    public fixed ulong Reserved[3];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeOperationCancelInput
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong ConfigurationGeneration;
    public ulong RequestEpoch;
    public NativeOperationHandle128 OperationId;
    public long ObservedUtcMilliseconds;
    public ulong ObservedMonotonicMilliseconds;
    public ulong Flags;
    public fixed ulong Reserved[4];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeOperationCancelOutput
{
    public uint StructSize;
    public uint State;
    public NativeOperationHandle128 OperationId;
    public ulong StateRevision;
    public ulong Flags;
    public fixed ulong Reserved[3];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeOperationPlanInput
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong ConfigurationGeneration;
    public ulong PlanEpoch;
    public long ObservedUtcMilliseconds;
    public ulong ObservedMonotonicMilliseconds;
    public uint ActionCapacity;
    public uint Flags;
    public fixed ulong Reserved[4];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeOperationPlanOutput
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong ConfigurationGeneration;
    public ulong PlanEpoch;
    public ulong StateRevision;
    public uint ActionCount;
    public uint ActiveOperationCount;
    public uint RunningOperationCount;
    public uint TerminalOperationCount;
    public ulong NextWakeMonotonicMilliseconds;
    public ulong Flags;
    public fixed ulong Reserved[4];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeOperationActionOutput
{
    public uint StructSize;
    public uint Kind;
    public ulong ActionId;
    public ulong ConfigurationGeneration;
    public NativeOperationHandle128 OperationId;
    public NativeOperationHandle128 AttemptToken;
    public ulong PlanEpoch;
    public uint AttemptNumber;
    public uint ReservedU32;
    public ulong DeadlineMonotonicMilliseconds;
    public ulong Flags;
    public fixed ulong Reserved[3];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeOperationActionFeedbackInput
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong ConfigurationGeneration;
    public ulong FeedbackEpoch;
    public ulong ActionId;
    public ulong PlanEpoch;
    public NativeOperationHandle128 OperationId;
    public NativeOperationHandle128 AttemptToken;
    public uint ActionKind;
    public uint Outcome;
    public long ObservedUtcMilliseconds;
    public ulong ObservedMonotonicMilliseconds;
    public NativeOperationHandle128 ResultHandle;
    public NativeOperationHandle128 ErrorHandle;
    public ulong ValidMask;
    public ulong Flags;
    public fixed ulong Reserved[3];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeOperationCompletionInput
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong ConfigurationGeneration;
    public ulong CompletionEpoch;
    public NativeOperationHandle128 OperationId;
    public NativeOperationHandle128 AttemptToken;
    public uint Outcome;
    public uint ReservedU32;
    public long ObservedUtcMilliseconds;
    public ulong ObservedMonotonicMilliseconds;
    public NativeOperationHandle128 ResultHandle;
    public NativeOperationHandle128 ErrorHandle;
    public ulong ValidMask;
    public ulong Flags;
    public fixed ulong Reserved[3];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeOperationProgressInput
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong ConfigurationGeneration;
    public NativeOperationHandle128 OperationId;
    public NativeOperationHandle128 AttemptToken;
    public ulong ProgressSequence;
    public long ObservedUtcMilliseconds;
    public ulong ObservedMonotonicMilliseconds;
    public uint PercentMilli;
    public uint ReservedU32;
    public ulong BytesDone;
    public ulong BytesTotal;
    public ulong SpeedBytesPerSecond;
    public NativeOperationHandle128 StageHandle;
    public NativeOperationHandle128 MessageHandle;
    public NativeOperationHandle128 CheckpointHandle;
    public ulong ValidMask;
    public ulong Flags;
    public fixed ulong Reserved[3];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeOperationSnapshotOutput
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong ConfigurationGeneration;
    public ulong StateRevision;
    public ulong LastPlanEpoch;
    public long LastObservedUtcMilliseconds;
    public ulong LastObservedMonotonicMilliseconds;
    public ulong NextWakeMonotonicMilliseconds;
    public uint OperationCount;
    public uint ActiveOperationCount;
    public uint RunningOperationCount;
    public uint TerminalOperationCount;
    public uint ActionCount;
    public uint ReservedU32;
    public ulong Flags;
    public fixed ulong Reserved[4];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeOperationReadInput
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong ConfigurationGeneration;
    public ulong StateRevision;
    public ulong PlanEpoch;
    public ulong Flags;
    public fixed ulong Reserved[3];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeOperationOutput
{
    public uint StructSize;
    public uint State;
    public NativeOperationHandle128 OperationId;
    public NativeOperationHandle128 DomainId;
    public NativeOperationHandle128 KindHandle;
    public NativeOperationHandle128 TitleHandle;
    public NativeOperationHandle128 RequestHandle;
    public NativeOperationHandle128 ResultHandle;
    public NativeOperationHandle128 ErrorHandle;
    public NativeOperationHandle128 AttemptToken;
    public long Priority;
    public ulong SubmitSequence;
    public ulong StateRevision;
    public long CreatedUtcMilliseconds;
    public ulong CreatedMonotonicMilliseconds;
    public long UpdatedUtcMilliseconds;
    public ulong UpdatedMonotonicMilliseconds;
    public ulong StartedMonotonicMilliseconds;
    public ulong RetryAtMonotonicMilliseconds;
    public ulong TerminalAtMonotonicMilliseconds;
    public ulong TerminalRetireAtMonotonicMilliseconds;
    public uint MaximumAttempts;
    public uint AttemptNumber;
    public uint RetryDelayMilliseconds;
    public uint RecoveryOriginState;
    public ulong ExecutionTimeoutMilliseconds;
    public ulong CancelGraceMilliseconds;
    public ulong TerminalRetentionMilliseconds;
    public ulong ProgressSequence;
    public ulong ProgressValidMask;
    public uint PercentMilli;
    public uint ReservedU32;
    public ulong BytesDone;
    public ulong BytesTotal;
    public ulong SpeedBytesPerSecond;
    public NativeOperationHandle128 StageHandle;
    public NativeOperationHandle128 MessageHandle;
    public NativeOperationHandle128 CheckpointHandle;
    public ulong Flags;
    public ulong AttemptConfigurationGeneration;
    public ulong QueueOrder;
    public fixed ulong Reserved[2];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeOperationPersistenceInput
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong ConfigurationGeneration;
    public ulong OperationEpoch;
    public ulong Flags;
    public fixed ulong Reserved[4];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeOperationPersistenceHeader
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong ConfigurationGeneration;
    public NativeOperationHandle128 SessionInstanceId;
    public NativeOperationHandle128 ClockInstanceId;
    public ulong OperationEpoch;
    public ulong StateRevision;
    public ulong SubmitSequence;
    public ulong LastSubmitEpoch;
    public ulong LastRequestEpoch;
    public ulong LastFeedbackEpoch;
    public ulong LastCompletionEpoch;
    public ulong LastPlanEpoch;
    public long LastObservedUtcMilliseconds;
    public ulong LastObservedMonotonicMilliseconds;
    public ulong NextActionId;
    public uint OperationCount;
    public uint RecordSize;
    public ulong Flags;
    public ulong ChecksumHigh;
    public ulong ChecksumLow;
    public fixed ulong Reserved[4];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeOperationPersistenceImportInput
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong ConfigurationGeneration;
    public ulong OperationEpoch;
    public long ObservedUtcMilliseconds;
    public ulong ObservedMonotonicMilliseconds;
    public NativeOperationHandle128 ClockInstanceId;
    public ulong Flags;
    public fixed ulong Reserved[4];
}
