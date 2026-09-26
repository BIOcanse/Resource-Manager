using System.Runtime.InteropServices;

namespace ResourceManager.App.Infrastructure.NativeCore;

internal static class NativeReportCoordinatorAbi
{
    public const uint Version = 0x0006_0000;
    public const ulong AllFamiliesTrustHandle = ulong.MaxValue;
}

internal enum NativeReportCoordinatorStatus : int
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

internal enum NativeReportSourceStatus : uint
{
    Complete = 1,
    Unavailable = 2,
    Skipped = 3
}

internal enum NativeReportMetricSelector : uint
{
    Current = 1,
    Average = 2,
    Peak = 3,
    Sum24Hours = 4,
    Sum7Days = 5,
    Events24Hours = 6,
    Events7Days = 7,
    RequestEvents24Hours = 8,
    RequestEvents7Days = 9,
    ConnectionEvents24Hours = 10,
    ConnectionEvents7Days = 11,
    SampleHits24Hours = 12,
    SampleHits7Days = 13
}

internal enum NativeReportComparison : uint
{
    GreaterOrEqual = 1,
    LessOrEqual = 2,
    Equal = 3,
    NotEqual = 4,
    BitAllSet = 5,
    LeftMinusRightGreaterOrEqual = 6,
    LeftMinusRightLessOrEqual = 7
}

internal enum NativeReportTrustCommandKind : uint
{
    Add = 1,
    Remove = 2
}

internal enum NativeReportPersistenceKind : uint
{
    Source = 1,
    Observation = 2,
    Bucket = 3,
    Report = 4,
    Trust = 5,
    Metadata = 6
}

internal enum NativeReportPersistenceFeedbackStatus : uint
{
    Persisted = 1,
    Failed = 2
}

[Flags]
internal enum NativeReportRuleFlags : uint
{
    None = 0,
    Rolling = 1U << 0,
    Known = Rolling
}

[Flags]
internal enum NativeReportFactFlags : uint
{
    None = 0,
    CurrentValid = 1U << 0,
    DeltaValid = 1U << 1,
    PeakValid = 1U << 2,
    EventCountValid = 1U << 3,
    RequestEventCountValid = 1U << 4,
    ConnectionEventCountValid = 1U << 5,
    SampleHitCountValid = 1U << 6,
    SecondaryCurrentValid = 1U << 7,
    Known = CurrentValid | DeltaValid | PeakValid | EventCountValid |
        RequestEventCountValid | ConnectionEventCountValid | SampleHitCountValid |
        SecondaryCurrentValid
}

[Flags]
internal enum NativeReportOutputFlags : uint
{
    None = 0,
    Active = 1U << 0,
    TrustedSuppressed = 1U << 1,
    Known = Active | TrustedSuppressed
}

[Flags]
internal enum NativeReportPersistenceFlags : uint
{
    None = 0,
    Delete = 1U << 0,
    Active = 1U << 1,
    TrustedSuppressed = 1U << 2,
    CurrentValid = 1U << 3,
    SecondaryCurrentValid = 1U << 4,
    Known = Delete | Active | TrustedSuppressed | CurrentValid | SecondaryCurrentValid
}

[Flags]
internal enum NativeReportPlanFlags : ulong
{
    None = 0,
    ReportsAvailable = 1UL << 0,
    PersistenceAvailable = 1UL << 1,
    NextWakeValid = 1UL << 2,
    HasMoreReports = 1UL << 3,
    HasMorePersistence = 1UL << 4,
    ClockRollbackObserved = 1UL << 5,
    Known = ReportsAvailable | PersistenceAvailable | NextWakeValid |
        HasMoreReports | HasMorePersistence | ClockRollbackObserved
}

[Flags]
internal enum NativeReportSourceSnapshotValidity : ulong
{
    None = 0,
    CommandMonotonic = 1UL << 0,
    CommandUtc = 1UL << 1,
    ObservedUtc = 1UL << 2,
    SourceIdentity = 1UL << 3,
    SourceGeneration = 1UL << 4,
    CoverageScope = 1UL << 5,
    SnapshotIdentity = 1UL << 6,
    Facts = 1UL << 7,
    Required = CommandMonotonic | CommandUtc | ObservedUtc | SourceIdentity |
        SourceGeneration | CoverageScope | SnapshotIdentity | Facts,
    Known = Required
}

[Flags]
internal enum NativeReportRuleReplaceValidity : ulong
{
    None = 0,
    CommandMonotonic = 1UL << 0,
    CommandUtc = 1UL << 1,
    Rules = 1UL << 2,
    Required = CommandMonotonic | CommandUtc | Rules,
    Known = Required
}

[Flags]
internal enum NativeReportTrustCommandValidity : ulong
{
    None = 0,
    CommandMonotonic = 1UL << 0,
    CommandUtc = 1UL << 1,
    Target = 1UL << 2,
    Family = 1UL << 3,
    Required = CommandMonotonic | CommandUtc | Target | Family,
    Known = Required
}

[Flags]
internal enum NativeReportImportValidity : ulong
{
    None = 0,
    CommandMonotonic = 1UL << 0,
    CommandUtc = 1UL << 1,
    Rows = 1UL << 2,
    Required = CommandMonotonic | CommandUtc | Rows,
    Known = Required
}

[Flags]
internal enum NativeReportPlanValidity : ulong
{
    None = 0,
    CommandMonotonic = 1UL << 0,
    CommandUtc = 1UL << 1,
    OutputLimits = 1UL << 2,
    Required = CommandMonotonic | CommandUtc | OutputLimits,
    Known = Required
}

[Flags]
internal enum NativeReportPersistenceFeedbackValidity : ulong
{
    None = 0,
    CommandMonotonic = 1UL << 0,
    CommandUtc = 1UL << 1,
    Rows = 1UL << 2,
    Required = CommandMonotonic | CommandUtc | Rows,
    Known = Required
}

[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 216)]
internal unsafe struct NativeReportCoordinatorConfiguration
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong Generation;
    public ulong SessionInstanceLow;
    public ulong SessionInstanceHigh;
    public uint MaximumSourceCount;
    public uint MaximumRuleCount;
    public uint MaximumObservationCount;
    public uint MaximumReportCount;
    public uint MaximumTrustCount;
    public uint MaximumBucketCount;
    public uint MaximumPersistenceOperationCount;
    public uint MaximumReportOutputCount;
    public uint SourceIndexCapacity;
    public uint RuleIndexCapacity;
    public uint ObservationIndexCapacity;
    public uint ReportIndexCapacity;
    public uint TrustIndexCapacity;
    public uint BucketIndexCapacity;
    public ulong BucketWidthMilliseconds;
    public ulong Window24HoursMilliseconds;
    public ulong Window7DaysMilliseconds;
    public ulong MaximumFutureSkewMilliseconds;
    public ulong DefaultStaleAfterMilliseconds;
    public ulong DefaultRetentionMilliseconds;
    public ulong ResidentByteBudget;
    public ulong Flags;
    public uint MaximumRollingObservationCount;
    public uint PlannedPersistenceIndexCapacity;
    public ulong MetadataCheckpointIntervalMilliseconds;
    public fixed ulong Reserved[6];
}

[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 96)]
internal unsafe struct NativeReportCoordinatorCapacity
{
    public uint StructSize;
    public uint SourceCapacity;
    public uint RuleCapacity;
    public uint ObservationCapacity;
    public uint ReportCapacity;
    public uint TrustCapacity;
    public uint BucketCapacity;
    public uint PersistenceOperationCapacity;
    public uint ReportOutputCapacity;
    public uint SourceIndexCapacity;
    public uint RuleIndexCapacity;
    public uint ObservationIndexCapacity;
    public uint ReportIndexCapacity;
    public uint TrustIndexCapacity;
    public uint BucketIndexCapacity;
    public uint RollingObservationCapacity;
    public ulong ResidentByteCount;
    public uint PlannedPersistenceIndexCapacity;
    public uint ReservedU32;
    public fixed ulong Reserved[2];
}

[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 160)]
internal unsafe struct NativeReportRuleInput
{
    public uint StructSize;
    public uint Flags;
    public ulong RuleHandle;
    public ulong RuleGeneration;
    public ulong SourceHandle;
    public ulong CoverageScopeHandle;
    public ulong FamilyHandle;
    public ulong ReportTypeHandle;
    public ulong ResourceKindHandle;
    public ulong PayloadHandle;
    public uint MetricSelector;
    public uint Comparison;
    public uint Priority;
    public uint Severity;
    public uint RequiredConsecutiveHits;
    public uint RequiredConsecutiveMisses;
    public ulong MinimumSampleDurationMilliseconds;
    public double ActivationThreshold;
    public double ClearThreshold;
    public ulong StaleAfterMilliseconds;
    public ulong RetentionMilliseconds;
    public ulong PredicateGroupHandle;
    public uint PredicateIndex;
    public uint PredicateCount;
    public fixed ulong Reserved[1];
}

[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 72)]
internal unsafe struct NativeReportRuleReplaceInput
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong ConfigurationGeneration;
    public ulong OperationEpoch;
    public ulong CommandMonotonicMilliseconds;
    public long CommandUtcMilliseconds;
    public ulong ValidMask;
    public uint RuleCount;
    public uint Flags;
    public fixed ulong Reserved[2];
}

[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 128)]
internal unsafe struct NativeReportFactInput
{
    public uint StructSize;
    public uint Flags;
    public ulong RuleHandle;
    public ulong RuleGeneration;
    public ulong TargetHandle;
    public ulong EvidencePayloadHandle;
    public ulong FactSequence;
    public double CurrentValue;
    public double DeltaValue;
    public double PeakValue;
    public ulong EventCount;
    public ulong RequestEventCount;
    public ulong ConnectionEventCount;
    public ulong SampleHitCount;
    public ulong SampleDurationMilliseconds;
    public double SecondaryCurrentValue;
    public fixed ulong Reserved[1];
}

[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 112)]
internal unsafe struct NativeReportSourceSnapshotInput
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong ConfigurationGeneration;
    public ulong OperationEpoch;
    public ulong CommandMonotonicMilliseconds;
    public long CommandUtcMilliseconds;
    public long ObservedAtUtcMilliseconds;
    public ulong SourceHandle;
    public ulong SourceGeneration;
    public ulong CoverageScopeHandle;
    public ulong SourceSnapshotEpoch;
    public ulong ValidMask;
    public uint FactCount;
    public uint Status;
    public ulong Flags;
    public fixed ulong Reserved[1];
}

[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 88)]
internal unsafe struct NativeReportTrustCommandInput
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong ConfigurationGeneration;
    public ulong OperationEpoch;
    public ulong CommandMonotonicMilliseconds;
    public long CommandUtcMilliseconds;
    public ulong TargetHandle;
    public ulong FamilyHandle;
    public ulong PayloadHandle;
    public ulong ValidMask;
    public uint CommandKind;
    public uint Flags;
    public fixed ulong Reserved[1];
}

[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 72)]
internal unsafe struct NativeReportImportInput
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong ConfigurationGeneration;
    public ulong ImportGeneration;
    public ulong OperationEpoch;
    public ulong CommandMonotonicMilliseconds;
    public long CommandUtcMilliseconds;
    public ulong ValidMask;
    public uint RowCount;
    public uint Flags;
    public fixed ulong Reserved[1];
}

[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 80)]
internal unsafe struct NativeReportPlanInput
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong ConfigurationGeneration;
    public ulong OperationEpoch;
    public ulong PlanEpoch;
    public ulong CommandMonotonicMilliseconds;
    public long CommandUtcMilliseconds;
    public ulong ValidMask;
    public uint ReportLimit;
    public uint PersistenceLimit;
    public ulong Flags;
    public fixed ulong Reserved[1];
}

[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 192)]
internal unsafe struct NativeReportOutput
{
    public uint StructSize;
    public uint Flags;
    public ulong ReportHandle;
    public ulong SlotGeneration;
    public ulong TargetHandle;
    public ulong FamilyHandle;
    public ulong RuleHandle;
    public ulong ReportTypeHandle;
    public ulong ResourceKindHandle;
    public ulong PayloadHandle;
    public ulong EvidencePayloadHandle;
    public long CreatedAtUtcMilliseconds;
    public long UpdatedAtUtcMilliseconds;
    public long LastObservedAtUtcMilliseconds;
    public double CurrentValue;
    public double AverageValue;
    public double PeakValue;
    public double Window24HoursValue;
    public double Window7DaysValue;
    public ulong SampleCount;
    public ulong ActiveSampleCount;
    public uint Priority;
    public uint Severity;
    public uint SlotIndex;
    public uint ReservedU32;
    public fixed ulong Reserved[2];
}

[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 352)]
internal struct NativeReportPersistenceOperation
{
    public uint StructSize;
    public uint Flags;
    public ulong SessionInstanceLow;
    public ulong SessionInstanceHigh;
    public ulong PlanEpoch;
    public ulong MutationVersion;
    public ulong IdentityHandle;
    public ulong SlotGeneration;
    public ulong SourceHandle;
    public ulong SourceGeneration;
    public ulong SourceSnapshotEpoch;
    public long SourceLastCompleteAtUtcMilliseconds;
    public uint SourceStatus;
    public uint SourceReservedU32;
    public ulong CoverageScopeHandle;
    public ulong RuleHandle;
    public ulong RuleGeneration;
    public ulong TargetHandle;
    public ulong FamilyHandle;
    public ulong ReportHandle;
    public ulong ReportTypeHandle;
    public ulong ResourceKindHandle;
    public ulong PayloadHandle;
    public ulong EvidencePayloadHandle;
    public long FirstObservedAtUtcMilliseconds;
    public long LastObservedAtUtcMilliseconds;
    public long BucketStartUtcMilliseconds;
    public double CurrentValue;
    public double SecondaryCurrentValue;
    public double ValueSum;
    public double PeakValue;
    public double WindowDeltaValue;
    public ulong SampleDurationMilliseconds;
    public ulong SampleCount;
    public ulong ActiveSampleCount;
    public ulong EventCount;
    public ulong RequestEventCount;
    public ulong ConnectionEventCount;
    public ulong SampleHitCount;
    public uint ConsecutiveHits;
    public uint ConsecutiveMisses;
    public uint Priority;
    public uint Severity;
    public uint OperationKind;
    public uint SlotIndex;
    public ulong FactSequence;
    public long ReportObservedAtUtcMilliseconds;
    public uint CheckpointSchemaVersion;
    public uint CheckpointReservedU32;
    public long CheckpointLogicalUtcMilliseconds;
}

[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 72)]
internal unsafe struct NativeReportPersistenceFeedbackInput
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong ConfigurationGeneration;
    public ulong OperationEpoch;
    public ulong FeedbackEpoch;
    public ulong CommandMonotonicMilliseconds;
    public long CommandUtcMilliseconds;
    public ulong ValidMask;
    public uint FeedbackCount;
    public uint Flags;
    public fixed ulong Reserved[1];
}

[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 80)]
internal unsafe struct NativeReportPersistenceFeedback
{
    public uint StructSize;
    public uint Status;
    public ulong SessionInstanceLow;
    public ulong SessionInstanceHigh;
    public ulong PlanEpoch;
    public ulong MutationVersion;
    public ulong IdentityHandle;
    public ulong SlotGeneration;
    public uint OperationKind;
    public uint SlotIndex;
    public uint Flags;
    public uint ReservedU32;
    public fixed ulong Reserved[1];
}

[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 176)]
internal unsafe struct NativeReportPlanOutput
{
    public uint StructSize;
    public uint ReportCount;
    public uint PersistenceOperationCount;
    public uint ActiveReportCount;
    public uint TrustedReportCount;
    public uint SourceCount;
    public uint ObservationCount;
    public uint BucketCount;
    public uint ReportTotalCount;
    public uint TrustCount;
    public uint ReservedU32;
    public ulong FirstMutationVersion;
    public ulong LastMutationVersion;
    public ulong SessionInstanceLow;
    public ulong SessionInstanceHigh;
    public ulong LastOperationEpoch;
    public ulong LastPlanEpoch;
    public ulong LastFeedbackEpoch;
    public ulong LastCommandMonotonicMilliseconds;
    public long LogicalUtcMilliseconds;
    public long NextWakeUtcMilliseconds;
    public ulong StateRevision;
    public ulong ImportGeneration;
    public ulong Flags;
    public ulong ResidentByteCount;
    public fixed ulong Reserved[2];
}
