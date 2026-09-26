using System.Runtime.InteropServices;

namespace ResourceManager.App.Infrastructure.NativeCore;

internal static class NativeSoftwareIdentityResolutionAbi
{
    public const uint Version = 0x0001_0000;
    public const uint MaximumSourceCount = 64;
}

internal enum NativeSoftwareIdentityObservationStatus : uint
{
    NoMatch = 1,
    Unavailable = 2,
    Matched = 3
}

internal enum NativeSoftwareIdentityResolutionStatus : uint
{
    Unattributed = 1,
    Matched = 2,
    Conflict = 3,
    Unavailable = 4
}

[Flags]
internal enum NativeSoftwareIdentityPolicyReplaceValidity : ulong
{
    None = 0,
    PolicyGeneration = 1UL << 0,
    OperationEpoch = 1UL << 1,
    Policies = 1UL << 2,
    Required = PolicyGeneration | OperationEpoch | Policies,
    Known = Required
}

[Flags]
internal enum NativeSoftwareIdentityResolveValidity : ulong
{
    None = 0,
    PolicyGeneration = 1UL << 0,
    FrameEpoch = 1UL << 1,
    CommandUtcMilliseconds = 1UL << 2,
    ObservedAtUtcMilliseconds = 1UL << 3,
    Observations = 1UL << 4,
    Required = PolicyGeneration | FrameEpoch | CommandUtcMilliseconds |
        ObservedAtUtcMilliseconds | Observations,
    Known = Required
}

[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 104)]
internal unsafe struct NativeSoftwareIdentityResolutionConfiguration
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong Generation;
    public uint MaximumPolicyCount;
    public uint MaximumObservationCount;
    public uint PolicyIndexCapacity;
    public uint ReservedU32;
    public ulong RequiredSourceMask;
    public ulong StopOnUnavailableSourceMask;
    public ulong MaximumFutureSkewMilliseconds;
    public ulong ResidentByteBudget;
    public ulong Flags;
    public fixed ulong Reserved[4];
}

[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 64)]
internal unsafe struct NativeSoftwareIdentityResolutionCapacity
{
    public uint StructSize;
    public uint PolicyCapacity;
    public uint ObservationCapacity;
    public uint PolicyIndexCapacity;
    public ulong ResidentByteCount;
    public fixed ulong Reserved[5];
}

[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 48)]
internal unsafe struct NativeSoftwareIdentitySourcePolicyInput
{
    public uint StructSize;
    public uint SourceId;
    public uint Priority;
    public uint Flags;
    public fixed ulong Reserved[4];
}

[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 72)]
internal unsafe struct NativeSoftwareIdentityPolicyReplaceInput
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong ConfigurationGeneration;
    public ulong PolicyGeneration;
    public ulong OperationEpoch;
    public uint PolicyCount;
    public uint ReservedU32;
    public ulong ValidMask;
    public ulong Flags;
    public fixed ulong Reserved[2];
}

[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 96)]
internal unsafe struct NativeSoftwareIdentityResolveInput
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong ConfigurationGeneration;
    public ulong PolicyGeneration;
    public ulong FrameEpoch;
    public long CommandUtcMilliseconds;
    public long ObservedAtUtcMilliseconds;
    public uint ObservationCount;
    public uint ReservedU32;
    public ulong ValidMask;
    public ulong Flags;
    public fixed ulong Reserved[3];
}

[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 80)]
internal unsafe struct NativeSoftwareIdentityObservationInput
{
    public uint StructSize;
    public uint SourceId;
    public uint Status;
    public uint Flags;
    public ulong IdentityHandle;
    public ulong DisplayNameHandle;
    public uint SoftwareKind;
    public uint ReservedU32;
    public ulong RootHandle;
    public ulong EvidenceMask;
    public ulong ObservationGeneration;
    public fixed ulong Reserved[2];
}

[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 176)]
internal unsafe struct NativeSoftwareIdentityResolutionOutput
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong ConfigurationGeneration;
    public ulong PolicyGeneration;
    public ulong PolicyFingerprintLow;
    public ulong PolicyFingerprintHigh;
    public ulong FrameEpoch;
    public ulong StateRevision;
    public uint SelectedSourceId;
    public uint DecisionSourceId;
    public uint Status;
    public uint Flags;
    public ulong IdentityHandle;
    public ulong DisplayNameHandle;
    public uint SoftwareKind;
    public uint ConflictCount;
    public ulong RootHandle;
    public ulong EvidenceMask;
    public ulong ObservedSourceMask;
    public ulong UnavailableSourceMask;
    public ulong NoMatchSourceMask;
    public ulong MatchedSourceMask;
    public uint NextRequiredSourceId;
    public uint ReservedU32;
    public fixed ulong Reserved[3];
}

[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 128)]
internal unsafe struct NativeSoftwareIdentityResolutionSummary
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong ConfigurationGeneration;
    public ulong PolicyGeneration;
    public ulong StateRevision;
    public ulong LastOperationEpoch;
    public ulong LastFrameEpoch;
    public long LastCommandUtcMilliseconds;
    public ulong RequiredSourceMask;
    public ulong StopOnUnavailableSourceMask;
    public ulong PolicyFingerprintLow;
    public ulong PolicyFingerprintHigh;
    public ulong ResidentByteCount;
    public uint PolicyCount;
    public uint Flags;
    public fixed ulong Reserved[3];
}
