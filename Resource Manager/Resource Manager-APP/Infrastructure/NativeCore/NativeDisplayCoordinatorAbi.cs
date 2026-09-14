using System.Runtime.InteropServices;

namespace ResourceManager.App.Infrastructure.NativeCore;

internal static class NativeDisplayCoordinatorAbi
{
    public const uint Version = 0x0003_0000;
    public const uint IdentityContractVersion = 0x0001_0000;
    public const uint CapabilityContractVersion = 0x0001_0000;
    public const uint SourceCount = 5;
}

internal enum NativeDisplayCoordinatorStatus : int
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

internal enum NativeDisplaySource : uint
{
    DisplayConfig = 1,
    Dxgi = 2,
    Edid = 3,
    SetupApiMonitor = 4,
    OemConnectorProfile = 5
}

internal enum NativeDisplaySourceStatus : uint
{
    Complete = 1,
    Retained = 2,
    Unavailable = 3,
    Unsupported = 5
}

internal enum NativeDisplayTextKind : uint
{
    MonitorDevicePath = 1,
    SourceDeviceName = 2,
    FriendlyName = 3,
    IdentityToken = 4,
    Evidence = 5,
    AdapterDevicePath = 6,
    AdapterMatchToken = 7
}

internal enum NativeDisplayCoordinatorPhase : uint
{
    Warming = 1,
    Collecting = 2,
    Ready = 3,
    FailedRetained = 4,
    FailedNoData = 5
}

internal enum NativeDisplayNodeKind : uint
{
    Connector = 1,
    Monitor = 2
}

[Flags]
internal enum NativeDisplaySourceMask : ulong
{
    None = 0,
    DisplayConfig = 1UL << 0,
    Dxgi = 1UL << 1,
    Edid = 1UL << 2,
    SetupApiMonitor = 1UL << 3,
    OemConnectorProfile = 1UL << 4,
    Known = DisplayConfig | Dxgi | Edid | SetupApiMonitor | OemConnectorProfile
}

[Flags]
internal enum NativeDisplayRefreshValidity : ulong
{
    RefreshEpoch = 1UL << 0,
    CapturedUtc = 1UL << 1,
    Monotonic = 1UL << 2,
    SourceMasks = 1UL << 3,
    Required = RefreshEpoch | CapturedUtc | Monotonic | SourceMasks
}

[Flags]
internal enum NativeDisplayBatchValidity : ulong
{
    SourceGeneration = 1UL << 0,
    CapturedUtc = 1UL << 1,
    Status = 1UL << 2,
    Facts = 1UL << 3,
    TextBindings = 1UL << 4,
    Required = SourceGeneration | CapturedUtc | Status | Facts | TextBindings
}

[Flags]
internal enum NativeDisplayTextValidity : ulong
{
    Bytes = 1UL << 0,
    Required = Bytes
}

[Flags]
internal enum NativeDisplayFactValidity : ulong
{
    AdapterLuid = 1UL << 0,
    TargetId = 1UL << 1,
    ConnectorInstance = 1UL << 2,
    OutputTechnology = 1UL << 3,
    StatusFlags = 1UL << 4,
    MonitorPath = 1UL << 5,
    SourceDeviceName = 1UL << 6,
    FriendlyName = 1UL << 7,
    EdidSerial = 1UL << 8,
    Evidence = 1UL << 9,
    Bounds = 1UL << 10,
    RefreshRate = 1UL << 11,
    BitsPerColorChannel = 1UL << 12,
    MinimumLuminance = 1UL << 13,
    MaximumLuminance = 1UL << 14,
    FullFrameLuminance = 1UL << 15,
    CapabilityFlags = 1UL << 16,
    ObservedAtUtc = 1UL << 17,
    SourceObjectKey = 1UL << 18,
    PayloadHandle = 1UL << 19,
    AdapterDevicePath = 1UL << 20,
    PreferredAdapterToken = 1UL << 21,
    OemMatchRule = 1UL << 22,
    Known = (1UL << 23) - 1
}

[Flags]
internal enum NativeDisplayIdentityMask : ulong
{
    None = 0,
    Target = 1UL << 0,
    MonitorPath = 1UL << 1,
    SourceDeviceName = 1UL << 2,
    EdidSerial = 1UL << 3,
    SourceObjectKey = 1UL << 4,
    Known = (1UL << 5) - 1
}

[Flags]
internal enum NativeDisplayCapabilityFlags : ulong
{
    None = 0,
    AdvancedColorSupported = 1UL << 0,
    AdvancedColorEnabled = 1UL << 1,
    HighDynamicRangeSupported = 1UL << 2,
    HighDynamicRangeUserEnabled = 1UL << 3,
    HighDynamicRangeActive = 1UL << 4,
    AbsoluteLuminanceAvailable = 1UL << 5,
    Known = (1UL << 6) - 1
}

[Flags]
internal enum NativeDisplayStatusFlags : uint
{
    None = 0,
    Active = 1U << 0,
    Connected = 1U << 1,
    Primary = 1U << 2,
    Internal = 1U << 3,
    Known = Active | Connected | Primary | Internal
}

[Flags]
internal enum NativeDisplayOemMatchFlags : uint
{
    None = 0,
    AllowAnyActiveAdapter = 1U << 0,
    Known = AllowAnyActiveAdapter
}

[Flags]
internal enum NativeDisplaySnapshotFlags : uint
{
    None = 0,
    HasLastGood = 1U << 0,
    RequiredSourceIncomplete = 1U << 1,
    HasUnresolved = 1U << 2,
    ContentChanged = 1U << 3,
    Known = HasLastGood | RequiredSourceIncomplete | HasUnresolved | ContentChanged
}

[Flags]
internal enum NativeDisplayPersistenceValidity : ulong
{
    OperationEpoch = 1UL << 0,
    Payload = 1UL << 1,
    Required = OperationEpoch | Payload
}

[Flags]
internal enum NativeDisplayReadValidity : ulong
{
    StateRevision = 1UL << 0,
    ContentGeneration = 1UL << 1,
    Required = StateRevision | ContentGeneration
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeDisplayHandle128 : IEquatable<NativeDisplayHandle128>
{
    public ulong High;
    public ulong Low;

    public readonly bool IsZero => High == 0 && Low == 0;

    public readonly bool Equals(NativeDisplayHandle128 other)
        => High == other.High && Low == other.Low;

    public override readonly bool Equals(object? obj)
        => obj is NativeDisplayHandle128 other && Equals(other);

    public override readonly int GetHashCode() => HashCode.Combine(High, Low);
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeDisplayCoordinatorConfiguration
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong Generation;
    public uint MaximumSourceCount;
    public uint MaximumObservationCount;
    public uint MaximumNodeCount;
    public uint MaximumEdgeCount;
    public uint MaximumCapabilityCount;
    public uint MaximumDiffEntryCount;
    public uint MaximumTextBindingCount;
    public uint MaximumTextByteCount;
    public uint MaximumUnresolvedCount;
    public uint MaximumSourceBatchCount;
    public uint IdentityIndexCapacity;
    public uint TextIndexCapacity;
    public ulong ResidentByteBudget;
    public ulong RequiredSourceMask;
    public ulong OptionalSourceMask;
    public ulong IdentitySourcePriorityOrder;
    public ulong FriendlyNameSourcePriorityOrder;
    public ulong CapabilitySourcePriorityOrder;
    public ulong ProjectionSourcePriorityOrder;
    public uint IdentityContractVersion;
    public uint CapabilityContractVersion;
    public uint MaximumFutureSkewMilliseconds;
    public uint Flags;
    public fixed ulong Reserved[3];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeDisplayCoordinatorCapacity
{
    public uint StructSize;
    public uint MaximumSourceCount;
    public uint MaximumObservationCount;
    public uint MaximumNodeCount;
    public uint MaximumEdgeCount;
    public uint MaximumCapabilityCount;
    public uint MaximumDiffEntryCount;
    public uint MaximumTextBindingCount;
    public uint MaximumTextByteCount;
    public uint MaximumUnresolvedCount;
    public uint MaximumSourceBatchCount;
    public uint IdentityIndexCapacity;
    public uint TextIndexCapacity;
    public uint ReservedU32;
    public ulong ResidentByteCount;
    public fixed ulong Reserved[8];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeDisplayRefreshInput
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong ConfigurationGeneration;
    public ulong RefreshEpoch;
    public long CapturedUtcMilliseconds;
    public ulong MonotonicMilliseconds;
    public ulong RequiredSourceMask;
    public ulong RequestedSourceMask;
    public ulong ValidMask;
    public ulong Flags;
    public fixed ulong Reserved[1];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeDisplaySourceBatchHeader
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong ConfigurationGeneration;
    public ulong RefreshEpoch;
    public ulong SourceGeneration;
    public long CapturedUtcMilliseconds;
    public uint SourceId;
    public uint Status;
    public uint FactCount;
    public uint TextBindingCount;
    public uint TextByteCount;
    public uint ReservedU32;
    public ulong ValidMask;
    public ulong Flags;
    public fixed ulong Reserved[2];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeDisplayTextInput
{
    public uint StructSize;
    public uint NormalizationKind;
    public uint ByteOffset;
    public uint ByteLength;
    public ulong ValidMask;
    public ulong Flags;
    public fixed ulong Reserved[2];
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeDisplayFact
{
    public uint StructSize;
    public uint SourceId;
    public ulong ValidMask;
    public ulong IdentityMask;
    public ulong SourceRecordOrdinal;
    public ulong AdapterLuid;
    public uint TargetId;
    public uint ConnectorInstance;
    public uint OutputTechnology;
    public uint StatusFlags;
    public uint MonitorPathTextIndex;
    public uint SourceDeviceTextIndex;
    public uint FriendlyNameTextIndex;
    public uint EdidSerialTextIndex;
    public uint EvidenceTextIndex;
    public uint MatchTextIndex;
    public uint MatchFlags;
    public uint ReservedU32;
    public int PositionX;
    public int PositionY;
    public int Width;
    public int Height;
    public uint RefreshNumerator;
    public uint RefreshDenominator;
    public uint BitsPerColorChannel;
    public uint MinimumLuminanceMilliNits;
    public uint MaximumLuminanceMilliNits;
    public uint MaximumFullFrameLuminanceMilliNits;
    public ulong CapabilityFlags;
    public long ObservedAtUtcMilliseconds;
    public ulong SourceObjectKey;
    public NativeDisplayHandle128 PayloadHandle;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeDisplayFinalizeInput
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong ConfigurationGeneration;
    public ulong RefreshEpoch;
    public ulong ExpectedSourceMask;
    public ulong ValidMask;
    public ulong Flags;
    public fixed ulong Reserved[2];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeDisplayAbortInput
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong ConfigurationGeneration;
    public ulong RefreshEpoch;
    public ulong ValidMask;
    public ulong Flags;
    public fixed ulong Reserved[3];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeDisplayReadInput
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong ConfigurationGeneration;
    public ulong StateRevision;
    public ulong ContentGeneration;
    public ulong ValidMask;
    public ulong Flags;
    public fixed ulong Reserved[1];
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeDisplayNodeOutput
{
    public uint StructSize;
    public uint Kind;
    public NativeDisplayHandle128 NodeHandle;
    public ulong Generation;
    public NativeDisplayHandle128 CanonicalIdentityHandle;
    public NativeDisplayHandle128 DisplayNameHandle;
    public uint ConnectorKind;
    public uint ConnectorInstance;
    public ulong StatusFlags;
    public ulong SourceMask;
    public ulong EvidenceMask;
    public ulong ValidMask;
    public ulong IdentityMask;
    public uint PrimarySourceId;
    public uint ReservedU32;
    public ulong PrimarySourceRecordOrdinal;
    public NativeDisplayHandle128 PrimaryPayloadHandle;
    public NativeDisplayHandle128 OemProfilePayloadHandle;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeDisplayEdgeOutput
{
    public uint StructSize;
    public uint Kind;
    public NativeDisplayHandle128 ParentHandle;
    public NativeDisplayHandle128 ChildHandle;
    public ulong SourceMask;
    public ulong Flags;
    public fixed ulong Reserved[1];
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeDisplayCapabilityOutput
{
    public uint StructSize;
    public uint Flags;
    public NativeDisplayHandle128 NodeHandle;
    public ulong Generation;
    public NativeDisplayHandle128 DisplayIdentityHandle;
    public NativeDisplayHandle128 SourceDeviceHandle;
    public NativeDisplayHandle128 FriendlyNameHandle;
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
    public uint BitsPerColorChannel;
    public uint MinimumLuminanceMilliNits;
    public uint MaximumLuminanceMilliNits;
    public uint MaximumFullFrameLuminanceMilliNits;
    public uint OutputTechnology;
    public uint CapabilitySourceMask;
    public uint RefreshNumerator;
    public uint RefreshDenominator;
    public ulong ValidMask;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeDisplayTextOutput
{
    public uint StructSize;
    public uint NormalizationKind;
    public NativeDisplayHandle128 Handle;
    public uint ByteOffset;
    public uint ByteLength;
    public ulong Flags;
    public fixed ulong Reserved[3];
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeDisplayDiffEntry
{
    public uint StructSize;
    public uint EntityKind;
    public uint ChangeKind;
    public uint ReservedU32;
    public NativeDisplayHandle128 EntityHandle;
    public ulong OldGeneration;
    public ulong NewGeneration;
    public ulong ChangedFieldMask;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeDisplayUnresolvedOutput
{
    public uint StructSize;
    public uint Reason;
    public ulong SourceRecordOrdinal;
    public ulong SourceMask;
    public NativeDisplayHandle128 FirstIdentity;
    public NativeDisplayHandle128 SecondIdentity;
    public uint IdentityKind;
    public uint ReservedU32;
    public fixed ulong Reserved[2];
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeDisplaySnapshotOutput
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong ConfigurationGeneration;
    public ulong StateRevision;
    public ulong RefreshEpoch;
    public ulong ContentGeneration;
    public long LastSuccessUtcMilliseconds;
    public long LastAttemptUtcMilliseconds;
    public uint Phase;
    public uint Flags;
    public ulong CurrentSourceMask;
    public ulong RetainedSourceMask;
    public ulong FailedSourceMask;
    public ulong UnsupportedSourceMask;
    public uint NodeCount;
    public uint EdgeCount;
    public uint CapabilityCount;
    public uint TextCount;
    public uint UnresolvedCount;
    public uint DiffCount;
    public ulong ResidentByteCount;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeDisplayPersistenceInput
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong ConfigurationGeneration;
    public ulong OperationEpoch;
    public ulong ValidMask;
    public ulong Flags;
    public fixed ulong Reserved[3];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeDisplayPersistenceHeader
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong ConfigurationGeneration;
    public ulong OperationEpoch;
    public ulong ContentGeneration;
    public ulong StateRevision;
    public ulong LastRefreshEpoch;
    public long LastSuccessUtcMilliseconds;
    public long LastAttemptUtcMilliseconds;
    public ulong CurrentSourceMask;
    public ulong RetainedSourceMask;
    public ulong FailedSourceMask;
    public ulong UnsupportedSourceMask;
    public fixed ulong SourceGenerations[5];
    public uint FactCount;
    public uint TextCount;
    public uint TextByteCount;
    public uint Phase;
    public NativeDisplayHandle128 Checksum;
    public ulong LastMonotonicMilliseconds;
    public fixed ulong Reserved[1];
}
