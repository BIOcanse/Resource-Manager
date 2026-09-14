using System.Runtime.InteropServices;

namespace ResourceManager.App.Infrastructure.NativeCore;

internal static class NativeSoftwareIdentityCatalogAbi
{
    public const uint Version = 0x0005_0000;
    public const int SignalCount = 6;
}

internal enum NativeSoftwareIdentityStatus : int
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

internal enum NativeSoftwareIdentityAliasKind : uint
{
    SteamAppId = 1,
    PackageIdentifier = 2,
    InstalledName = 3,
    ExecutableName = 4,
    ProductName = 5,
    LauncherToken = 6,
    ManagedChildSegment = 7
}

internal enum NativeSoftwareIdentityMatchMode : uint
{
    InstalledSoftware = 1,
    Process = 2,
    PortableProcess = 3
}

internal enum NativeSoftwareIdentityMatchStatus : uint
{
    NoMatch = 1,
    Matched = 2,
    Conflict = 3
}

internal enum NativeSoftwareIdentityMatchConfidence : uint
{
    None = 0,
    Candidate = 1,
    Confirmed = 2
}

internal enum NativeSoftwareIdentityKnownMatchMode : uint
{
    None = 0,
    Identity = 1,
    Root = 2,
    LauncherManagedChild = 3
}

internal enum NativeSoftwareIdentitySignalKind : uint
{
    ProcessName = 1,
    ProductName = 2,
    FileDescription = 3,
    WindowApplicationUserModelId = 4,
    ApplicationUserModelId = 5,
    WindowTitle = 6
}

[Flags]
internal enum NativeSoftwareIdentityEvidence : ulong
{
    None = 0,
    SteamAppId = 1UL << 0,
    PackageIdentifier = 1UL << 1,
    InstalledName = 1UL << 2,
    ExecutableName = 1UL << 3,
    ProductName = 1UL << 4,
    Known = SteamAppId | PackageIdentifier | InstalledName | ExecutableName | ProductName
}

[Flags]
internal enum NativeSoftwareIdentitySignalEvidence : ulong
{
    None = 0,
    ProcessName = 1UL << 0,
    ProductName = 1UL << 1,
    FileDescription = 1UL << 2,
    WindowApplicationUserModelId = 1UL << 3,
    ApplicationUserModelId = 1UL << 4,
    WindowTitle = 1UL << 5,
    Known = ProcessName | ProductName | FileDescription | WindowApplicationUserModelId |
        ApplicationUserModelId | WindowTitle
}

[Flags]
internal enum NativeSoftwareIdentityAliasFlags : uint
{
    None = 0,
    Prohibited = 1U << 0,
    Known = Prohibited
}

[Flags]
internal enum NativeSoftwareIdentityCatalogConfigurationFlags : ulong
{
    None = 0,
    LauncherNonEntryRequiresRoot = 1UL << 0,
    Known = LauncherNonEntryRequiresRoot
}

[Flags]
internal enum NativeSoftwareIdentityKnownOutputFlags : ulong
{
    None = 0,
    QueryLauncher = 1UL << 0,
    EntryLauncher = 1UL << 1,
    RootHit = 1UL << 2,
    Known = QueryLauncher | EntryLauncher | RootHit
}

[Flags]
internal enum NativeSoftwareIdentityCatalogReplaceValidity : ulong
{
    None = 0,
    CatalogGeneration = 1UL << 0,
    OperationEpoch = 1UL << 1,
    Entries = 1UL << 2,
    Aliases = 1UL << 3,
    Roots = 1UL << 4,
    KeyBytes = 1UL << 5,
    Required = CatalogGeneration | OperationEpoch | Entries | Aliases | Roots | KeyBytes,
    Known = Required
}

[Flags]
internal enum NativeSoftwareIdentityCatalogQueryValidity : ulong
{
    None = 0,
    CatalogGeneration = 1UL << 0,
    QueryEpoch = 1UL << 1,
    Facts = 1UL << 2,
    KeyBytes = 1UL << 3,
    Required = CatalogGeneration | QueryEpoch | Facts | KeyBytes,
    Known = Required
}

[Flags]
internal enum NativeSoftwareIdentityKnownQueryValidity : ulong
{
    None = 0,
    CatalogGeneration = 1UL << 0,
    QueryEpoch = 1UL << 1,
    Signals = 1UL << 2,
    KeyBytes = 1UL << 3,
    ExecutablePath = 1UL << 4,
    Required = CatalogGeneration | QueryEpoch | Signals | KeyBytes,
    Known = Required | ExecutablePath
}

[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 224)]
internal unsafe struct NativeSoftwareIdentityCatalogConfiguration
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong Generation;
    public uint MaximumEntryCount;
    public uint MaximumAliasCount;
    public uint MaximumRootCount;
    public uint MaximumCatalogKeyByteCount;
    public uint MaximumQueryFactCount;
    public uint MaximumQuerySignalCount;
    public uint MaximumQueryKeyByteCount;
    public uint EntryIndexCapacity;
    public uint AliasIndexCapacity;
    public uint IdentityIndexCapacity;
    public uint RootIndexCapacity;
    public uint RequiredProhibitedAliasCount;
    public uint RequiredLauncherTokenCount;
    public uint RequiredManagedChildRuleCount;
    public uint MinimumContainsKeyLength;
    public uint ReservedCapacity;
    public ulong ResidentByteBudget;
    public int ExactTextScore;
    public int ContainsTextScore;
    public int IdentityMinimumScore;
    public int StrongEvidenceMinimumScore;
    public int RootHitBonus;
    public int QueryLauncherMatchBonus;
    public int QueryLauncherNonmatchPenalty;
    public int RootLauncherMatchBonus;
    public int RootLauncherNonmatchPenalty;
    public int RootRejectScore;
    public fixed int SignalWeights[NativeSoftwareIdentityCatalogAbi.SignalCount];
    public fixed int RootSignalWeights[NativeSoftwareIdentityCatalogAbi.SignalCount];
    public uint StrongSignalMask;
    public uint RootSignalMask;
    public ulong Flags;
    public fixed ulong Reserved[4];
}

[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 80)]
internal unsafe struct NativeSoftwareIdentityCatalogCapacity
{
    public uint StructSize;
    public uint EntryCapacity;
    public uint AliasCapacity;
    public uint RootCapacity;
    public uint CatalogKeyByteCapacity;
    public uint QueryFactCapacity;
    public uint QuerySignalCapacity;
    public uint QueryKeyByteCapacity;
    public uint EntryIndexCapacity;
    public uint AliasIndexCapacity;
    public uint IdentityIndexCapacity;
    public uint RootIndexCapacity;
    public ulong ResidentByteCount;
    public fixed ulong Reserved[3];
}

[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 72)]
internal unsafe struct NativeSoftwareIdentityCatalogEntryInput
{
    public uint StructSize;
    public uint Flags;
    public ulong EntryHandle;
    public ulong AttributionIdHandle;
    public ulong DisplayNameHandle;
    public uint Source;
    public uint SoftwareKind;
    public uint PrimaryNameOffset;
    public uint PrimaryNameLength;
    public fixed ulong Reserved[3];
}

[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 56)]
internal unsafe struct NativeSoftwareIdentityCatalogAliasInput
{
    public uint StructSize;
    public uint Kind;
    public ulong EntryHandle;
    public uint KeyOffset;
    public uint KeyLength;
    public ulong NumericValue;
    public uint Flags;
    public uint ReservedU32;
    public fixed ulong Reserved[2];
}

[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 56)]
internal unsafe struct NativeSoftwareIdentityCatalogRootInput
{
    public uint StructSize;
    public uint Flags;
    public ulong RootHandle;
    public ulong EntryHandle;
    public uint KeyOffset;
    public uint KeyLength;
    public fixed ulong Reserved[3];
}

[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 88)]
internal unsafe struct NativeSoftwareIdentityCatalogReplaceInput
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong ConfigurationGeneration;
    public ulong CatalogGeneration;
    public ulong OperationEpoch;
    public uint EntryCount;
    public uint AliasCount;
    public uint RootCount;
    public uint KeyByteCount;
    public uint ProhibitedAliasCount;
    public uint LauncherTokenCount;
    public uint ManagedChildRuleCount;
    public uint ReservedU32;
    public ulong ValidMask;
    public fixed ulong Reserved[2];
}

[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 80)]
internal unsafe struct NativeSoftwareIdentityCatalogQueryInput
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong ConfigurationGeneration;
    public ulong CatalogGeneration;
    public ulong QueryEpoch;
    public uint Mode;
    public uint FactCount;
    public uint KeyByteCount;
    public uint Flags;
    public ulong ValidMask;
    public fixed ulong Reserved[3];
}

[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 48)]
internal unsafe struct NativeSoftwareIdentityCatalogFactInput
{
    public uint StructSize;
    public uint Kind;
    public uint KeyOffset;
    public uint KeyLength;
    public ulong NumericValue;
    public uint Flags;
    public uint ReservedU32;
    public fixed ulong Reserved[2];
}

[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 88)]
internal unsafe struct NativeSoftwareIdentityKnownQueryInput
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong ConfigurationGeneration;
    public ulong CatalogGeneration;
    public ulong QueryEpoch;
    public uint SignalCount;
    public uint KeyByteCount;
    public uint ExecutablePathOffset;
    public uint ExecutablePathLength;
    public ulong ValidMask;
    public ulong Flags;
    public fixed ulong Reserved[3];
}

[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 40)]
internal unsafe struct NativeSoftwareIdentityKnownSignalInput
{
    public uint StructSize;
    public uint Kind;
    public uint KeyOffset;
    public uint KeyLength;
    public uint Flags;
    public uint ReservedU32;
    public fixed ulong Reserved[2];
}

[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 128)]
internal unsafe struct NativeSoftwareIdentityCatalogMatchOutput
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong ConfigurationGeneration;
    public ulong CatalogGeneration;
    public ulong CatalogFingerprintLow;
    public ulong CatalogFingerprintHigh;
    public ulong QueryEpoch;
    public ulong EntryHandle;
    public ulong AttributionIdHandle;
    public ulong DisplayNameHandle;
    public uint Source;
    public uint SoftwareKind;
    public uint Status;
    public uint Confidence;
    public ulong EvidenceMask;
    public uint ConflictCount;
    public uint MatchedFactCount;
    public ulong Flags;
    public fixed ulong Reserved[2];
}

[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 168)]
internal unsafe struct NativeSoftwareIdentityKnownMatchOutput
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong ConfigurationGeneration;
    public ulong CatalogGeneration;
    public ulong CatalogFingerprintLow;
    public ulong CatalogFingerprintHigh;
    public ulong QueryEpoch;
    public ulong EntryHandle;
    public ulong AttributionIdHandle;
    public ulong DisplayNameHandle;
    public uint Source;
    public uint SoftwareKind;
    public uint Status;
    public uint Mode;
    public int Score;
    public uint ConflictCount;
    public ulong MatchedRootHandle;
    public uint DerivedRootByteLength;
    public uint ReservedU32;
    public ulong DerivedIdentityFingerprintLow;
    public ulong DerivedIdentityFingerprintHigh;
    public ulong EvidenceMask;
    public ulong Flags;
    public fixed ulong Reserved[3];
}

[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 128)]
internal unsafe struct NativeSoftwareIdentityCatalogSummary
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong ConfigurationGeneration;
    public ulong CatalogGeneration;
    public ulong StateRevision;
    public ulong LastOperationEpoch;
    public ulong LastQueryEpoch;
    public uint EntryCount;
    public uint AliasCount;
    public uint RootCount;
    public uint KeyByteCount;
    public uint ProhibitedAliasCount;
    public uint LauncherTokenCount;
    public uint ManagedChildRuleCount;
    public uint Flags;
    public ulong CatalogFingerprintLow;
    public ulong CatalogFingerprintHigh;
    public ulong ResidentByteCount;
    public fixed ulong Reserved[3];
}
