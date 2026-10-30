using System.Runtime.InteropServices;

namespace ResourceManager.ReadWrite.Native;

public static class NativeMetricSnapshotAbi
{
    public const uint Version = 0x000E_0000;
    public const uint CatalogContractVersion = 0x0006_0000;
    public const uint ValueContractVersion = 0x0004_0000;
    public const uint ObservationContractVersion = 0x0009_0000;
    public const uint InventoryContractVersion = 0x0002_0000;
    public const uint PersistenceContractVersion = 0x0005_0000;
    public const uint CpuCounterContractVersion = 0x0001_0000;
    public const int MaximumValueBytes = 512;
    public const ulong LayoutFingerprint = 0xf686e3d8fabe568f;
    public const ulong WireContractFingerprint = 0xd416d753e946524;
}

public enum NativeMetricSnapshotStatus : int
{
    Ok = 0,
    InvalidArgument = 1,
    AbiMismatch = 2,
    Unavailable = 3,
    NoData = 4,
    BufferTooSmall = 5,
    StaleFrame = 6,
    OutOfMemory = 7,
    PdhError = 8
}

public enum NativeMetricSnapshotPhase : uint
{
    Empty = 1,
    CatalogReady = 2,
    CompletionOpen = 3,
    Ready = 4
}

public enum NativeMetricSnapshotSourceRole : uint
{
    Metrics = 1,
    GpuInventory = 2
}

public enum NativeMetricSnapshotSourceStatus : uint
{
    Complete = 1,
    Partial = 2,
    Unavailable = 3,
    Unsupported = 4,
    Skipped = 5
}

public enum NativeMetricSnapshotSourceAvailability : uint
{
    Available = 1,
    Unavailable = 2,
    Unsupported = 3
}

public enum NativeMetricSnapshotObservationStatus : uint
{
    Current = 1,
    Unavailable = 2,
    Unsupported = 3,
    Skipped = 4
}

public enum NativeMetricSnapshotMetricStatus : uint
{
    Current = 1,
    Unavailable = 3,
    Unsupported = 4,
    Skipped = 5
}

public enum NativeMetricSnapshotInventoryStatus : uint
{
    Current = 1,
    Unavailable = 3,
    Unsupported = 4
}

public enum NativeMetricSnapshotMetricKind : uint
{
    CpuUsage = 1,
    CpuFrequency = 2,
    CpuSensor = 3,
    MemoryUsed = 4,
    MemoryTotal = 5,
    VirtualMemoryUsed = 6,
    VirtualMemoryTotal = 7,
    GpuUsage = 8,
    GpuClock = 9,
    GpuVramUsed = 10,
    GpuVramTotal = 11,
    GpuSensor = 12,
    CustomNumeric = 13,
    CounterRatio = 14,
    TimedCounterRate = 15,
    FloatingCounterRatio = 16,
    FloatingCounterPercentage = 17,
    TimedMicrosecondCounterPercentage = 18,
    CounterFrequency = 19
}

public enum NativeMetricSnapshotScopeKind : uint
{
    Host = 1,
    Cpu = 2,
    Memory = 3,
    GpuAdapter = 4
}

public enum NativeMetricSnapshotValueKind : uint
{
    Float64 = 1,
    Signed64 = 2,
    Unsigned64 = 3,
    Unsigned128 = 4,
    Bytes = 5,
    Boolean = 6
}

[Flags]
public enum NativeMetricSnapshotMetricFlags : uint
{
    None = 0,
    Percentage = 1U << 0,
    Nonnegative = 1U << 1,
    UsedValue = 1U << 2,
    TotalValue = 1U << 3,
    Known = Percentage | Nonnegative | UsedValue | TotalValue
}

[Flags]
public enum NativeMetricSnapshotSourceFlags : uint
{
    None = 0,
    Required = 1U << 0,
    Known = Required
}

[Flags]
public enum NativeMetricSnapshotSourceResetReason : uint
{
    None = 0,
    IncarnationChanged = 1U << 0,
    CounterRegressed = 1U << 1,
    MonotonicRegressed = 1U << 2,
    TickFrequencyChanged = 1U << 3,
    ArithmeticOverflow = 1U << 4,
    Known = IncarnationChanged
        | CounterRegressed
        | MonotonicRegressed
        | TickFrequencyChanged
        | ArithmeticOverflow
}

[Flags]
public enum NativeMetricSnapshotSnapshotFlags : uint
{
    None = 0,
    CatalogLoaded = 1U << 0,
    CommittedData = 1U << 1,
    GpuInventoryPresent = 1U << 3,
    CompletionOpen = 1U << 4,
    Known = CatalogLoaded
        | CommittedData
        | GpuInventoryPresent
        | CompletionOpen
}

[Flags]
public enum NativeMetricSnapshotPlanFlags : ulong
{
    None = 0,
    IncludeGpuInventory = 1UL << 0,
    Known = IncludeGpuInventory
}

[Flags]
public enum NativeMetricSnapshotPlanOutputFlags : uint
{
    None = 0,
    GpuInventorySelected = 1U << 0,
    GpuInventoryUnavailable = 1U << 1,
    Known = GpuInventorySelected | GpuInventoryUnavailable
}

[Flags]
public enum NativeMetricSnapshotObservationFlags : uint
{
    None = 0,
    CounterPair = 1,
    TimedCounter = 2,
    FloatingCounterPair = 4,
    Known = CounterPair | TimedCounter | FloatingCounterPair
}

[Flags]
public enum NativeMetricSnapshotObservationValidity : ulong
{
    None = 0,
    Value = 1UL << 0,
    ObservedAt = 1UL << 1,
    Capability = 1UL << 2,
    Required = ObservedAt | Capability,
    Known = Required | Value
}

[Flags]
public enum NativeMetricSnapshotCpuCounterValidity : ulong
{
    None = 0,
    Counters = 1UL << 0,
    MonotonicTime = 1UL << 1,
    ObservedAt = 1UL << 2,
    Capability = 1UL << 3,
    Required = Counters | MonotonicTime | ObservedAt | Capability,
    Known = Required
}

[Flags]
public enum NativeMetricSnapshotMetricPlanFlags : uint
{
    None = 0,
    Selected = 1U << 0,
    Unavailable = 1U << 1,
    Known = Selected | Unavailable
}

[Flags]
public enum NativeMetricSnapshotSourcePlanFlags : uint
{
    None = 0,
    Selected = 1U << 0,
    Known = Selected
}

[Flags]
public enum NativeMetricSnapshotInventoryValidity : ulong
{
    None = 0,
    AdapterIdentity = 1UL << 0,
    ObservedAt = 1UL << 1,
    Capability = 1UL << 2,
    Topology = 1UL << 3,
    Required = AdapterIdentity | ObservedAt | Capability | Topology,
    Known = Required
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct NativeMetricSnapshotConfiguration
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong Generation;
    public uint MaximumSourceCount;
    public uint MaximumMetricCount;
    public uint MaximumRuleCount;
    public uint MaximumRequestedCount;
    public uint MaximumObservationCount;
    public uint MaximumGpuAdapterCount;
    public uint MaximumPersistenceSourceCount;
    public uint MaximumPersistenceRuleCount;
    public uint MaximumPersistenceGpuCount;
    public uint SourceIndexCapacity;
    public uint MetricIndexCapacity;
    public uint RuleIndexCapacity;
    public uint GpuIndexCapacity;
    public ulong MaximumFutureSkewMilliseconds;
    public ulong ResidentByteBudget;
    public uint CatalogContractVersion;
    public uint ValueContractVersion;
    public uint ObservationContractVersion;
    public uint InventoryContractVersion;
    public uint PersistenceContractVersion;
    public uint Flags;
    public uint MaximumPlanMetricCount;
    public uint MaximumSourceModeCount;
    public uint MaximumSourcePlanCount;
    public uint MaximumMetricPlanCount;
    public uint GpuLuidIndexCapacity;
    public uint GpuKeyIndexCapacity;
    public uint ReservedU32;
    public fixed ulong Reserved[2];
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct NativeMetricSnapshotCapacity
{
    public uint StructSize;
    public uint SourceCapacity;
    public uint MetricCapacity;
    public uint RuleCapacity;
    public uint RequestedCapacity;
    public uint ObservationCapacity;
    public uint GpuAdapterCapacity;
    public uint PersistenceSourceCapacity;
    public uint PersistenceRuleCapacity;
    public uint PersistenceGpuCapacity;
    public uint SourceIndexCapacity;
    public uint MetricIndexCapacity;
    public uint RuleIndexCapacity;
    public uint GpuIndexCapacity;
    public uint GpuLuidIndexCapacity;
    public uint GpuKeyIndexCapacity;
    public uint PlanMetricCapacity;
    public uint SourceModeCapacity;
    public uint SourcePlanCapacity;
    public uint MetricPlanCapacity;
    public ulong ResidentByteCount;
    public ulong ScratchByteCount;
    public uint ScratchAlignment;
    public uint ReservedU32;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct NativeMetricSnapshotSourcePolicyInput
{
    public uint StructSize;
    public uint Flags;
    public ulong SourceHandle;
    public uint SourceRole;
    public uint Priority;
    public uint ReservedU32;
    public uint AlignmentReserved;
    public ulong CapabilityMask;
    public ulong SemanticFingerprint;
    public fixed ulong Reserved[2];
}

[StructLayout(LayoutKind.Sequential)]
public struct NativeMetricSnapshotMetricDefinitionInput
{
    public uint StructSize;
    public uint Flags;
    public ulong RuleHandle;
    public ulong MetricHandle;
    public ulong SourceHandle;
    public ulong ScopeHandle;
    public ulong CapabilityMask;
    public uint MetricKind;
    public uint ScopeKind;
    public uint ValueKind;
    public uint SourcePriority;
    public uint ReservedU32;
    public uint AlignmentReserved;
    public ulong MinimumValueBits;
    public ulong MaximumValueBits;
    public ulong SemanticFingerprint;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct NativeMetricSnapshotCatalogReplaceInput
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong ConfigurationGeneration;
    public ulong CatalogGeneration;
    public ulong OperationEpoch;
    public ulong CommandAtMilliseconds;
    public uint SourceCount;
    public uint RuleCount;
    public uint MetricCount;
    public uint GpuInventorySourceCount;
    public uint SourceIndexCapacity;
    public uint MetricIndexCapacity;
    public uint RuleIndexCapacity;
    public uint Flags;
    public ulong SemanticFingerprint;
    public fixed ulong Reserved[2];
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct NativeMetricSnapshotCompletionHeader
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong ConfigurationGeneration;
    public ulong CatalogGeneration;
    public ulong OperationEpoch;
    public ulong PlanEpoch;
    public ulong SourceHandle;
    public ulong SourceIncarnation;
    public ulong SourceObservationSequence;
    public ulong CapabilityGeneration;
    public ulong PlanTokenFingerprint;
    public ulong CommandAtMilliseconds;
    public ulong CapturedAtMilliseconds;
    public uint RequestedRuleCount;
    public uint ObservationCount;
    public uint GpuInventoryCount;
    public uint ExpectedGpuInventoryCount;
    public uint CpuCounterCount;
    public uint OverflowCount;
    public uint Status;
    public uint Flags;
    public fixed ulong Reserved[2];
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct NativeMetricSnapshotPlanInput
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong ConfigurationGeneration;
    public ulong CatalogGeneration;
    public ulong OperationEpoch;
    public ulong PlanEpoch;
    public ulong CommandAtMilliseconds;
    public uint RequestedMetricCount;
    public uint SourceModeCount;
    public uint SourcePlanCapacity;
    public uint MetricPlanCapacity;
    public ulong Flags;
    public fixed ulong Reserved[2];
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct NativeMetricSnapshotPlanOutput
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong ConfigurationGeneration;
    public ulong CatalogGeneration;
    public ulong OperationEpoch;
    public ulong PlanEpoch;
    public ulong StateRevision;
    public uint SourcePlanCount;
    public uint MetricPlanCount;
    public uint UnavailableMetricCount;
    public uint Flags;
    public ulong SemanticFingerprint;
    public fixed ulong Reserved[2];
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct NativeMetricSnapshotPlanMetricInput
{
    public uint StructSize;
    public uint Flags;
    public ulong MetricHandle;
    public fixed ulong Reserved[2];
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct NativeMetricSnapshotSourceStateInput
{
    public uint StructSize;
    public uint Flags;
    public ulong SourceHandle;
    public ulong CapabilityGeneration;
    public uint Availability;
    public fixed ulong Reserved[2];
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct NativeMetricSnapshotSourcePlanOutput
{
    public uint StructSize;
    public uint Flags;
    public ulong SourceHandle;
    public ulong CapabilityGeneration;
    public ulong PlanEpoch;
    public ulong PlanTokenFingerprint;
    public uint RequestedRuleCount;
    public uint Availability;
    public uint SourceRole;
    public fixed ulong Reserved[2];
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct NativeMetricSnapshotMetricPlanOutput
{
    public uint StructSize;
    public uint Flags;
    public ulong RuleHandle;
    public ulong MetricHandle;
    public ulong SourceHandle;
    public ulong ScopeHandle;
    public ulong PlanEpoch;
    public uint MetricKind;
    public uint ValueKind;
    public fixed ulong Reserved[2];
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct NativeMetricSnapshotRequestedMetricInput
{
    public uint StructSize;
    public uint Flags;
    public ulong RuleHandle;
    public fixed ulong Reserved[2];
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct NativeMetricSnapshotObservationInput
{
    public uint StructSize;
    public uint Flags;
    public ulong RuleHandle;
    public ulong MetricHandle;
    public ulong SourceHandle;
    public ulong ScopeHandle;
    public ulong SourceObservationSequence;
    public ulong ObservedAtMilliseconds;
    public ulong ValueBits;
    public ulong CapabilityMask;
    public ulong ValidMask;
    public uint Status;
    public uint ValueKind;
    public uint Quality;
    // Observation contract v8: declared CPUID.16H base MHz for current CounterFrequency observations; zero otherwise.
    public uint ReservedU32;
    public ulong SampleDurationMilliseconds;
    public ulong ValueHighBits;
    public uint ByteCount;
    public uint PayloadReserved;
    public ulong CounterTicksPerSecond;
    public fixed byte ValueBytes[NativeMetricSnapshotAbi.MaximumValueBytes];
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct NativeMetricSnapshotCpuCounterInput
{
    public uint StructSize;
    public uint Flags;
    public ulong RuleHandle;
    public ulong MetricHandle;
    public ulong SourceHandle;
    public ulong SourceIncarnation;
    public ulong SourceObservationSequence;
    public ulong ObservedAtMilliseconds;
    public ulong MonotonicTicks;
    public ulong MonotonicTicksPerSecond;
    public ulong IdleTicks;
    public ulong KernelTicks;
    public ulong UserTicks;
    public ulong CapabilityMask;
    public ulong ValidMask;
    public uint Status;
    public uint CounterContractVersion;
    public fixed ulong Reserved[2];
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct NativeMetricSnapshotGpuInventoryInput
{
    public uint StructSize;
    public uint Flags;
    public ulong AdapterHandle;
    public ulong SourceHandle;
    public ulong SourceObservationSequence;
    public ulong ObservedAtMilliseconds;
    public ulong AdapterLuidLow;
    public ulong AdapterLuidHigh;
    public ulong StableKeyHandle;
    public ulong CapabilityMask;
    public ulong TopologyFingerprint;
    public ulong ValidMask;
    public uint Status;
    public uint ReservedU32;
    public fixed ulong Reserved[2];
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct NativeMetricSnapshotFinalizeInput
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong ConfigurationGeneration;
    public ulong CatalogGeneration;
    public ulong OperationEpoch;
    public ulong PlanEpoch;
    public ulong SourceHandle;
    public ulong CommandAtMilliseconds;
    public ulong Flags;
    public fixed ulong Reserved[2];
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct NativeMetricSnapshotControlInput
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong ConfigurationGeneration;
    public ulong OperationEpoch;
    public ulong CommandAtMilliseconds;
    public ulong Flags;
    public fixed ulong Reserved[2];
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct NativeMetricSnapshotReadInput
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong ConfigurationGeneration;
    public ulong CatalogGeneration;
    public ulong CatalogFingerprint;
    public ulong StateRevision;
    public ulong CommittedGeneration;
    public ulong Flags;
    public fixed ulong Reserved[2];
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct NativeMetricSnapshotSnapshotHeader
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong ConfigurationGeneration;
    public ulong CatalogGeneration;
    public ulong CatalogFingerprint;
    public ulong StateRevision;
    public ulong CommittedGeneration;
    public ulong LastOperationEpoch;
    public ulong LastPlanEpoch;
    public ulong LastCommandAtMilliseconds;
    public ulong CapturedAtMilliseconds;
    public uint SourceCount;
    public uint MetricCount;
    public uint RuleCount;
    public uint GpuAdapterCount;
    public uint CurrentMetricCount;
    public uint UnavailableMetricCount;
    public uint UnsupportedMetricCount;
    public uint SkippedMetricCount;
    public uint Flags;
    public uint Phase;
    public uint ReservedU32;
    public uint AlignmentReserved;
    public ulong SemanticFingerprint;
    public fixed ulong Reserved[2];
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct NativeMetricSnapshotMetricOutput
{
    public uint StructSize;
    public uint Flags;
    public ulong MetricHandle;
    public ulong WinningRuleHandle;
    public ulong SourceHandle;
    public ulong ScopeHandle;
    public ulong SourceGeneration;
    public ulong ObservedAtMilliseconds;
    public ulong ValueBits;
    public ulong CapabilityMask;
    public uint MetricKind;
    public uint ScopeKind;
    public uint ValueKind;
    public uint Status;
    public uint Quality;
    public uint ReservedU32;
    public ulong SemanticFingerprint;
    public ulong SampleDurationMilliseconds;
    public ulong ValueHighBits;
    public uint ByteCount;
    public uint PayloadReserved;
    public fixed byte ValueBytes[NativeMetricSnapshotAbi.MaximumValueBytes];
}

[StructLayout(LayoutKind.Sequential)]
public struct NativeMetricSnapshotSourceOutput
{
    public uint StructSize;
    public uint Flags;
    public ulong SourceHandle;
    public ulong SourceIncarnation;
    public ulong SourceGeneration;
    public ulong SourceObservationSequence;
    public ulong CapabilityGeneration;
    public ulong LastAttemptAtMilliseconds;
    public ulong LastCurrentAtMilliseconds;
    public ulong CapabilityMask;
    public uint SourceRole;
    public uint Priority;
    public uint Status;
    public uint RequestedCount;
    public uint ObservedCount;
    public uint SkippedCount;
    public uint OverflowCount;
    public uint ResetCount;
    public uint LastResetReasonMask;
    public uint AlignmentReserved;
    public ulong DefinitionFingerprint;
    public ulong StateFingerprint;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct NativeMetricSnapshotGpuInventoryOutput
{
    public uint StructSize;
    public uint Flags;
    public ulong AdapterHandle;
    public ulong SourceHandle;
    public ulong SourceGeneration;
    public ulong ObservedAtMilliseconds;
    public ulong AdapterLuidLow;
    public ulong AdapterLuidHigh;
    public ulong StableKeyHandle;
    public ulong CapabilityMask;
    public ulong TopologyFingerprint;
    public ulong ValidMask;
    public uint Status;
    public uint ReservedU32;
    public ulong SemanticFingerprint;
    public fixed ulong Reserved[2];
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct NativeMetricSnapshotRuleStateOutput
{
    public uint StructSize;
    public uint Flags;
    public ulong RuleHandle;
    public ulong MetricHandle;
    public ulong SourceHandle;
    public ulong SourceGeneration;
    public ulong ObservedAtMilliseconds;
    public ulong ValueBits;
    public ulong CapabilityMask;
    public uint Status;
    public uint ValueKind;
    public uint Quality;
    public uint AlignmentReserved;
    public ulong SampleDurationMilliseconds;
    public ulong DefinitionFingerprint;
    public ulong StateFingerprint;
    public ulong CapabilityGeneration;
    public ulong UnsupportedUntilCapabilityGeneration;
    public ulong ValueHighBits;
    public uint ByteCount;
    public uint PayloadReserved;
    public fixed byte ValueBytes[NativeMetricSnapshotAbi.MaximumValueBytes];
}

[StructLayout(LayoutKind.Sequential)]
public struct NativeMetricSnapshotSourcePersistenceOutput
{
    public uint StructSize;
    public uint Flags;
    public ulong SourceHandle;
    public ulong DefinitionFingerprint;
    public ulong SourceIncarnation;
    public ulong SourceGeneration;
    public ulong SourceObservationSequence;
    public ulong LastAttemptAtMilliseconds;
    public ulong LastCurrentAtMilliseconds;
    public ulong CapabilityMask;
    public ulong CpuIdleTicks;
    public ulong CpuKernelTicks;
    public ulong CpuUserTicks;
    public ulong CpuMonotonicTicks;
    public ulong CpuMonotonicTicksPerSecond;
    public ulong CapabilityGeneration;
    public uint RequestedCount;
    public uint ObservedCount;
    public uint SkippedCount;
    public uint OverflowCount;
    public uint ResetCount;
    public uint LastResetReasonMask;
    public uint Status;
    public uint CpuBaselineValid;
    public uint CpuCounterContractVersion;
    public uint ReservedU32;
    public ulong StateFingerprint;
    public uint GpuInventoryCount;
    public uint AlignmentReserved;
    public ulong Reserved;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct NativeMetricSnapshotPersistenceInput
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong ConfigurationGeneration;
    public ulong CatalogGeneration;
    public ulong OperationEpoch;
    public ulong CommandAtMilliseconds;
    public ulong Flags;
    public fixed ulong Reserved[2];
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct NativeMetricSnapshotPersistenceHeader
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong ConfigurationGeneration;
    public ulong CatalogGeneration;
    public ulong StateRevision;
    public ulong CommittedGeneration;
    public ulong LastOperationEpoch;
    public ulong LastPlanEpoch;
    public ulong LastCommandAtMilliseconds;
    public ulong CapturedAtMilliseconds;
    public ulong CatalogFingerprint;
    public uint RuleStateCount;
    public uint SourceStateCount;
    public uint GpuAdapterCount;
    public uint Phase;
    public ulong SemanticFingerprint;
    public ulong Checksum;
    public ulong Flags;
    public fixed ulong Reserved[3];
}
