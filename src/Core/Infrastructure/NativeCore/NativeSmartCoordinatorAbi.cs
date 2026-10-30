using System.Runtime.InteropServices;

namespace ResourceManager.App.Infrastructure.NativeCore;

internal static class NativeSmartCoordinatorAbi
{
    public const uint Version = 0x000c_0001;
    public const int RuntimeStateCount = 7;
}

internal enum NativeSmartCoordinatorStatus : int
{
    Ok = 0,
    InvalidArgument = 1,
    AbiMismatch = 2,
    Unavailable = 3,
    NoData = 4,
    BufferTooSmall = 5,
    StaleGeneration = 6,
    OutOfMemory = 7,
    CapacityExceeded = 8,
    ConflictingFacts = 9,
    FeedbackMismatch = 10,
    RecreateRequired = 11
}

internal enum NativeSmartCoordinatorRuntimeState : byte
{
    Unknown = 0,
    ForegroundFocused = 1,
    ForegroundUnfocused = 2,
    BackgroundWindow = 3,
    TrayOnly = 4,
    BackgroundProcess = 5,
    NotRunning = 6
}

internal enum NativeSmartCoordinatorSoftwareKind : byte
{
    Unknown = 0,
    Game = 1,
    HighPerformance = 2,
    WindowsSystem = 3,
    Adapted = 4,
    Dependency = 5,
    Runtime = 6,
    Controlled = 7,
    Managed = 8,
    Unattributed = 9,
    GeneralApplication = 10
}

internal enum NativeSmartCoordinatorMetricKind : byte
{
    None = 0,
    CpuUsagePercent = 1,
    GpuUsagePercent = 2,
    VramUsagePercent = 3
}

internal enum NativeSmartCoordinatorProcessGrade : sbyte
{
    Level4 = -4,
    Level3 = -3,
    Level2 = -2,
    Level1 = -1,
    Normal = 0,
    A1 = 1
}

internal enum NativeSmartCoordinatorActionScope : byte
{
    ProcessPolicy = 1,
}

internal enum NativeSmartCoordinatorActionDisposition : byte
{
    NoOp = 0,
    Retain = 1,
    Restore = 2,
    Apply = 3
}

internal enum NativeSmartCoordinatorFeedbackStatus : byte
{
    Succeeded = 1,
    FailedUnchanged = 2,
    Rejected = 3,
    Skipped = 4,
    OwnershipLost = 5,
    StateUncertain = 6
}

internal enum NativeSmartCoordinatorSnapshotRowKind : byte
{
    Process = 1,
    Software = 2
}

[Flags]
internal enum NativeSmartCoordinatorConfigFields : ulong
{
    None = 0,
    Capacities = 1UL << 0,
    Timing = 1UL << 1,
    ProcessScoring = 1UL << 2,
    Features = 1UL << 5,
    Required = Capacities | Timing | ProcessScoring | Features
}

[Flags]
internal enum NativeSmartCoordinatorFeatures : ulong
{
    None = 0,
    ProcessPolicy = 1UL << 0,
    CriticalEvents = 1UL << 3,
    Known = ProcessPolicy | CriticalEvents
}

[Flags]
internal enum NativeSmartCoordinatorCycleValidity : ulong
{
    None = 0,
    ScoreOnlyMode = 1UL << 2,
    MaximumActionsThisCycle = 1UL << 3,
    Known = ScoreOnlyMode | MaximumActionsThisCycle,
    Required = ScoreOnlyMode | MaximumActionsThisCycle
}

[Flags]
internal enum NativeSmartCoordinatorCycleFlags : ulong
{
    None = 0,
    FullProcessSnapshot = 1UL << 0,
    AuthoritativeAppliedFacts = 1UL << 2,
    ScoreOnly = 1UL << 3,
    ExternalEvent = 1UL << 4,
    Known = FullProcessSnapshot | AuthoritativeAppliedFacts | ScoreOnly | ExternalEvent
}

[Flags]
internal enum NativeSmartCoordinatorInputValidity : ulong
{
    None = 0,
    ProcessIdentity = 1UL << 0,
    SoftwareIdentity = 1UL << 1,
    SoftwareKind = 1UL << 2,
    BaseScore = 1UL << 3,
    SurfaceFacts = 1UL << 4,
    Eligibility = 1UL << 5,
    Protection = 1UL << 6,
    Metric = 1UL << 9,
    AppliedProcessGrade = 1UL << 10,
    ProcessScore = 1UL << 13,
    SoftwareScore = 1UL << 14,
    ScoreMemberCount = 1UL << 15,
    Known = ProcessIdentity | SoftwareIdentity | SoftwareKind | BaseScore | SurfaceFacts |
        Eligibility | Protection | Metric |
        AppliedProcessGrade | ProcessScore |
        SoftwareScore | ScoreMemberCount
}

[Flags]
internal enum NativeSmartCoordinatorInputFlags : ulong
{
    None = 0,
    Running = 1UL << 0,
    ForegroundFocused = 1UL << 1,
    HasVisibleWindow = 1UL << 2,
    HasBackgroundWindow = 1UL << 3,
    HasHiddenWindow = 1UL << 4,
    ProcessCpuMetricsComplete = 1UL << 5,
    ProcessGpuMetricsComplete = 1UL << 6,
    CanApplyProcessPolicy = 1UL << 7,
    HardwareSchedulingEligible = 1UL << 9,
    OwnsProcessGrade = 1UL << 10,
    Known = Running | ForegroundFocused | HasVisibleWindow | HasBackgroundWindow |
        HasHiddenWindow | ProcessCpuMetricsComplete | ProcessGpuMetricsComplete |
        CanApplyProcessPolicy | HardwareSchedulingEligible |
        OwnsProcessGrade
}

[Flags]
internal enum NativeSmartCoordinatorGradeDomains : byte
{
    None = 0,
    Process = 1 << 0,
    Known = Process
}

[Flags]
internal enum NativeSmartCoordinatorActionValidity : ulong
{
    None = 0,
    ProcessIdentity = 1UL << 0,
    SoftwareIdentity = 1UL << 1,
    ProcessGrade = 1UL << 2,
    CpuScore = 1UL << 5,
    AtomicGroup = 1UL << 6,
    Known = ProcessIdentity | SoftwareIdentity | ProcessGrade | CpuScore | AtomicGroup
}

[Flags]
internal enum NativeSmartCoordinatorActionFlags : uint
{
    None = 0,
    RequiresFeedback = 1U << 0,
    Atomic = 1U << 1,
    Compensation = 1U << 2,
    Retry = 1U << 3,
    Known = RequiresFeedback | Atomic | Compensation | Retry
}

[Flags]
internal enum NativeSmartCoordinatorFeedbackValidity : ulong
{
    None = 0,
    CompletedAt = 1UL << 0,
    ActualProcessGrade = 1UL << 1,
    Known = CompletedAt | ActualProcessGrade
}

[Flags]
internal enum NativeSmartCoordinatorFeedbackFlags : uint
{
    None = 0,
    ProcessOwned = 1U << 0,
    RollbackPayloadPersisted = 1U << 3,
    Known = ProcessOwned | RollbackPayloadPersisted
}

[Flags]
internal enum NativeSmartCoordinatorSnapshotFlags : uint
{
    None = 0,
    HasInvalidFacts = 1U << 0,
    EventBoostActive = 1U << 1,
    ScoreOnly = 1U << 2,
    AwaitingFeedback = 1U << 3,
    RequiresAuthoritativeResync = 1U << 4,
    Known = HasInvalidFacts | EventBoostActive | ScoreOnly | AwaitingFeedback | RequiresAuthoritativeResync
}

[Flags]
internal enum NativeSmartCoordinatorSnapshotRowFlags : uint
{
    None = 0,
    ProcessOwned = 1U << 0,
    ProcessInflight = 1U << 3,
    FreezeReady = 1U << 6,
    CriticalFact = 1U << 7,
    Known = ProcessOwned | ProcessInflight | FreezeReady | CriticalFact
}

[Flags]
internal enum NativeSmartCoordinatorReason : ulong
{
    None = 0,
    ReservedSystemReason0 = 1UL << 0,
    MissingProcessIdentity = 1UL << 1,
    MissingBaseScore = 1UL << 2,
    MissingSurfaceFacts = 1UL << 3,
    MissingCpuMetric = 1UL << 4,
    ReservedProcessReason5 = 1UL << 5,
    IncompleteGpuMetrics = 1UL << 6,
    Ineligible = 1UL << 7,
    NotRunning = 1UL << 8,
    HighTierBlocksOptimization = 1UL << 9,
    MiddleTierBlocksEnhancement = 1UL << 10,
    MiddleTierBlocksFreeze = 1UL << 11,
    LowTierBlocksEnhancement = 1UL << 12,
    ProtectionLevel1ClampsFreeze = 1UL << 13,
    ProtectionLevel2BlocksOptimization = 1UL << 14,
    ReservedProcessReason15 = 1UL << 15,
    ReservedProcessReason16 = 1UL << 16,
    FreezeGroupBlocked = 1UL << 17,
    AwaitingStability = 1UL << 18,
    AppliedMatchesDesired = 1UL << 19,
    CriticalFactChanged = 1UL << 23,
    CriticalMembershipChanged = 1UL << 24,
    StartupGraceActive = 1UL << 25,
    TargetDisappeared = 1UL << 26,
    FeatureDisabled = 1UL << 27,
    FeedbackFailed = 1UL << 28,
    OwnershipLost = 1UL << 29,
    StateUncertain = 1UL << 30,
    RetryBackoff = 1UL << 31,
    ScoreOnly = 1UL << 32,
    NoCompleteSnapshot = 1UL << 33,
    DuplicateCycle = 1UL << 34,
    AtomicGroupCompensation = 1UL << 35,
    ReservationTimedOut = 1UL << 37,
    Known = ((1UL << 38) - 1) & ~((7UL << 20) | (1UL << 5) | (1UL << 15) | (1UL << 16) | (1UL << 36))
}

[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 264)]
internal unsafe struct NativeSmartCoordinatorConfiguration
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong Generation;
    public NativeSmartCoordinatorConfigFields FieldMask;
    public NativeSmartCoordinatorFeatures FeatureFlags;

    public uint MaximumProcesses;
    public uint MaximumSoftwareGroups;
    public uint MaximumGpuStates;
    public uint MaximumInputRows;
    public uint MaximumActions;
    public uint MaximumReservations;
    public uint MaximumAtomicGroups;

    public uint NormalIntervalMilliseconds;
    public uint EventIntervalMilliseconds;
    public uint EventBoostRounds;
    public uint GameStartGraceMilliseconds;
    public uint RequiredConsecutiveDecisions;
    public uint FailureRetryMilliseconds;
    public uint ReservationTimeoutMilliseconds;
    public fixed uint ReservedTiming[3];

    public fixed double ProcessStateMultipliers[NativeSmartCoordinatorAbi.RuntimeStateCount];
    public double A1MinimumCpuScore;
    public double DefaultMinimumCpuScoreScale;
    public double Level1MaximumCpuScoreScale;
    public double Level2MaximumCpuScoreScale;
    public double Level3MaximumCpuScoreScale;
    public double LowTierLevel4MaximumCpuScoreScale;
    public double ReservedProcessPolicy0;
    public double HighTierMinimumBaseScore;
    public double MiddleTierMinimumBaseScore;

    public fixed ulong Reserved[4];
}

[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 88)]
internal unsafe struct NativeSmartCoordinatorCapacity
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong ConfigurationGeneration;
    public uint InputRowStructSize;
    public uint CycleInputStructSize;
    public uint ActionStructSize;
    public uint FeedbackStructSize;
    public uint SnapshotStructSize;
    public uint SnapshotRowStructSize;
    public uint InputRowCapacity;
    public uint ActionCapacity;
    public uint FeedbackCapacity;
    public uint SnapshotRowCapacity;
    public uint ProcessCapacity;
    public uint SoftwareCapacity;
    public uint GpuStateCapacity;
    public uint AtomicGroupCapacity;
    public fixed ulong Reserved[2];
}

[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 80)]
internal struct NativeSmartCoordinatorCycleInput
{
    public uint AbiVersion;
    public uint StructSize;
    public uint InputRowStructSize;
    public uint ActionStructSize;
    public ulong ConfigurationGeneration;
    public ulong CycleSequence;
    public long ObservedAtMilliseconds;
    public NativeSmartCoordinatorCycleValidity ValidMask;
    public NativeSmartCoordinatorCycleFlags Flags;
    public uint InputCount;
    public uint ActionCapacity;
    public ulong RoundNumber;
    public uint MaximumActionsThisCycle;
    public uint Reserved;
}

[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 104)]
internal unsafe struct NativeSmartCoordinatorInputRow
{
    public uint StructSize;
    public NativeSmartCoordinatorMetricKind MetricKind;
    public NativeSmartCoordinatorSoftwareKind SoftwareKind;
    public byte ProtectionLevel;
    public byte Reserved0;
    public NativeSmartCoordinatorInputValidity ValidMask;
    public NativeSmartCoordinatorInputFlags Flags;
    public ulong TargetKey;
    public ulong SoftwareKey;
    public ulong ProcessStartKey;
    public double BaseScore;
    public double MetricValue;
    public uint SourceIndex;
    public uint ProcessId;
    public uint DeviceIndex;
    public uint ScoreMemberCount;
    public NativeSmartCoordinatorProcessGrade AppliedProcessGrade;
    public fixed byte Reserved2[7];
    public double ProcessScore;
    public double SoftwareScore;
}

[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 120)]
internal unsafe struct NativeSmartCoordinatorAction
{
    public uint StructSize;
    public NativeSmartCoordinatorActionFlags Flags;
    public NativeSmartCoordinatorActionValidity ValidMask;
    public NativeSmartCoordinatorReason ReasonMask;
    public ulong ActionId;
    public ulong PlanEpoch;
    public ulong ConfigurationGeneration;
    public ulong AtomicGroupId;
    public ulong TargetKey;
    public ulong SoftwareKey;
    public ulong ProcessStartKey;
    public double CpuScore;
    public uint SourceIndex;
    public uint ProcessId;
    public uint OrderKey;
    public uint WakeAfterMilliseconds;
    public uint GroupMemberIndex;
    public uint GroupMemberCount;
    public NativeSmartCoordinatorActionScope Scope;
    public NativeSmartCoordinatorActionDisposition Disposition;
    public NativeSmartCoordinatorGradeDomains DomainMask;
    public byte Reserved0;
    public NativeSmartCoordinatorProcessGrade FromProcessGrade;
    public NativeSmartCoordinatorProcessGrade ToProcessGrade;
    public fixed byte Reserved1[2];
}

[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 96)]
internal unsafe struct NativeSmartCoordinatorFeedback
{
    public uint StructSize;
    public NativeSmartCoordinatorFeedbackFlags Flags;
    public NativeSmartCoordinatorFeedbackValidity ValidMask;
    public ulong ActionId;
    public ulong PlanEpoch;
    public ulong ConfigurationGeneration;
    public ulong TargetKey;
    public ulong SoftwareKey;
    public ulong ProcessStartKey;
    public long CompletedAtMilliseconds;
    public uint ProcessId;
    public uint SystemErrorCode;
    public NativeSmartCoordinatorFeedbackStatus Status;
    public NativeSmartCoordinatorActionScope Scope;
    public NativeSmartCoordinatorProcessGrade ActualProcessGrade;
    public fixed byte Reserved0[5];
    public ulong Reserved1;
}

[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 128)]
internal unsafe struct NativeSmartCoordinatorSnapshot
{
    public uint AbiVersion;
    public uint StructSize;
    public uint SnapshotRowStructSize;
    public NativeSmartCoordinatorSnapshotFlags Flags;
    public ulong ConfigurationGeneration;
    public ulong CycleSequence;
    public ulong PlanEpoch;
    public ulong StateRevision;
    public long ObservedAtMilliseconds;
    public long NextWakeAtMilliseconds;
    public uint WakeAfterMilliseconds;
    public uint ActionCount;
    public uint ProcessCount;
    public uint SoftwareCount;
    public uint PendingCount;
    public uint InflightCount;
    public uint InvalidFactCount;
    public uint SnapshotRowCount;
    public NativeSmartCoordinatorReason ReasonMask;
    public uint EventBoostRoundsRemaining;
    public uint EarlyWakeAfterMilliseconds;
    public fixed ulong Reserved[2];
}

[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 160)]
internal struct NativeSmartCoordinatorSnapshotRow
{
    public uint StructSize;
    public NativeSmartCoordinatorSnapshotRowFlags Flags;
    public NativeSmartCoordinatorInputValidity ValidMask;
    public NativeSmartCoordinatorReason ReasonMask;
    public ulong TargetKey;
    public ulong SoftwareKey;
    public ulong ProcessStartKey;
    public ulong LastSeenCycle;
    public long PendingFirstSeenMilliseconds;
    public long PendingLastSeenMilliseconds;
    public long LastFeedbackMilliseconds;
    public long GameStartedAtMilliseconds;
    public double BaseScore;
    public double CpuScore;
    public double CpuOccupancyPercent;
    public double ReservedProcessScore0;
    public double ReservedProcessRatio0;
    public uint SourceIndex;
    public uint ProcessId;
    public uint ProcessPendingCount;
    public uint FailureCount;
    public NativeSmartCoordinatorSnapshotRowKind RowKind;
    public NativeSmartCoordinatorSoftwareKind SoftwareKind;
    public NativeSmartCoordinatorRuntimeState RuntimeState;
    public byte ProtectionLevel;
    public NativeSmartCoordinatorProcessGrade DesiredProcessGrade;
    public NativeSmartCoordinatorProcessGrade AppliedProcessGrade;
    public NativeSmartCoordinatorProcessGrade PendingProcessGrade;
    public byte Reserved0;
    public ulong Reserved1;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeSmartCoordinatorScratchRequirements
{
    internal uint AbiVersion;
    internal uint StructSize;
    internal ulong ByteCount;
    internal uint Alignment;
    internal uint ProcessSize;
    internal uint SoftwareSize;
    internal uint GpuSize;
    internal uint SoftwareGpuSize;
    internal uint ProcessCount;
    internal uint SoftwareCount;
    internal uint GpuCount;
    internal ulong ProcessIndexCount;
    internal ulong SoftwareIndexCount;
    internal ulong GpuIndexCount;
    internal uint Reserved;
    internal uint Reserved1;
}
