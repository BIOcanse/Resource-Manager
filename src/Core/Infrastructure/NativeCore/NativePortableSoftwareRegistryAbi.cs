using System.Runtime.InteropServices;

namespace ResourceManager.App.Infrastructure.NativeCore;

internal static class NativePortableSoftwareRegistryAbi
{
    public const uint Version = 0x0001_0000;
}

internal enum NativePortableSoftwareRegistryStatus : int
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

[Flags]
internal enum NativePortableSoftwarePathFlags : uint
{
    None = 0,
    IdentityConfirmed = 1U << 0,
    RootConfirmed = 1U << 1,
    Known = IdentityConfirmed | RootConfirmed
}

[Flags]
internal enum NativePortableSoftwarePersistenceFlags : uint
{
    None = 0,
    Delete = 1U << 0,
    IdentityConfirmed = 1U << 1,
    RootConfirmed = 1U << 2,
    Known = Delete | IdentityConfirmed | RootConfirmed
}

[Flags]
internal enum NativePortableSoftwareSnapshotFlags : uint
{
    None = 0,
    IdentityConfirmed = 1U << 0,
    RequiresRootConfirmation = 1U << 1,
    RootConfirmed = 1U << 2,
    Dirty = 1U << 3,
    Known = IdentityConfirmed | RequiresRootConfirmation | RootConfirmed | Dirty
}

[Flags]
internal enum NativePortableSoftwarePlanOutputFlags : uint
{
    None = 0,
    HasMore = 1U << 0,
    MutationExhausted = 1U << 1,
    Known = HasMore | MutationExhausted
}

[Flags]
internal enum NativePortableSoftwareSnapshotOutputFlags : uint
{
    None = 0,
    RegistrationHasMore = 1U << 0,
    PathHasMore = 1U << 1,
    MutationExhausted = 1U << 2,
    Known = RegistrationHasMore | PathHasMore | MutationExhausted
}

[Flags]
internal enum NativePortableSoftwareImportValidity : ulong
{
    None = 0,
    ImportGeneration = 1UL << 0,
    OperationEpoch = 1UL << 1,
    CommandUtcMilliseconds = 1UL << 2,
    Rows = 1UL << 3,
    KeyBytes = 1UL << 4,
    Required = ImportGeneration | OperationEpoch | CommandUtcMilliseconds | Rows | KeyBytes,
    Known = Required
}

[Flags]
internal enum NativePortableSoftwareObserveValidity : ulong
{
    None = 0,
    OperationEpoch = 1UL << 0,
    CommandUtcMilliseconds = 1UL << 1,
    ObservedAtUtcMilliseconds = 1UL << 2,
    Identity = 1UL << 3,
    ExecutablePath = 1UL << 4,
    SuggestedRoot = 1UL << 5,
    Required = OperationEpoch | CommandUtcMilliseconds | ObservedAtUtcMilliseconds | Identity |
        ExecutablePath | SuggestedRoot,
    Known = Required
}

[Flags]
internal enum NativePortableSoftwareConfirmRootValidity : ulong
{
    None = 0,
    OperationEpoch = 1UL << 0,
    CommandUtcMilliseconds = 1UL << 1,
    SoftwareIdentity = 1UL << 2,
    RootPath = 1UL << 3,
    Required = OperationEpoch | CommandUtcMilliseconds | SoftwareIdentity | RootPath,
    Known = Required
}

[Flags]
internal enum NativePortableSoftwareMarkMissingValidity : ulong
{
    None = 0,
    OperationEpoch = 1UL << 0,
    CommandUtcMilliseconds = 1UL << 1,
    SoftwareIdentity = 1UL << 2,
    PathIdentity = 1UL << 3,
    Required = OperationEpoch | CommandUtcMilliseconds | SoftwareIdentity | PathIdentity,
    Known = Required
}

[Flags]
internal enum NativePortableSoftwarePlanValidity : ulong
{
    None = 0,
    PlanEpoch = 1UL << 0,
    OutputLimit = 1UL << 1,
    Required = PlanEpoch | OutputLimit,
    Known = Required
}

[Flags]
internal enum NativePortableSoftwareFeedbackValidity : ulong
{
    None = 0,
    FeedbackEpoch = 1UL << 0,
    Rows = 1UL << 1,
    Required = FeedbackEpoch | Rows,
    Known = Required
}

[Flags]
internal enum NativePortableSoftwareSnapshotValidity : ulong
{
    None = 0,
    SnapshotEpoch = 1UL << 0,
    Cursors = 1UL << 1,
    OutputLimits = 1UL << 2,
    Required = SnapshotEpoch | Cursors | OutputLimits,
    Known = Required
}

[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 112)]
internal unsafe struct NativePortableSoftwareRegistryConfiguration
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong Generation;
    public uint MaximumRegistrationCount;
    public uint MaximumPathCount;
    public uint MaximumPersistenceOperationCount;
    public uint MaximumRegistrationSnapshotCount;
    public uint MaximumPathSnapshotCount;
    public uint MaximumExecutablePathByteCount;
    public uint MaximumRootPathByteCount;
    public uint RegistrationIndexCapacity;
    public uint PathIndexCapacity;
    public uint MaximumFutureSkewMilliseconds;
    public uint Flags;
    public ulong ResidentByteBudget;
    public fixed ulong Reserved[5];
}

[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 88)]
internal unsafe struct NativePortableSoftwareRegistryCapacity
{
    public uint StructSize;
    public uint RegistrationCapacity;
    public uint PathCapacity;
    public uint PersistenceOperationCapacity;
    public uint RegistrationSnapshotCapacity;
    public uint PathSnapshotCapacity;
    public uint ExecutablePathByteCapacityPerPath;
    public uint RootPathByteCapacityPerPath;
    public uint RegistrationIndexCapacity;
    public uint PathIndexCapacity;
    public uint ReservedU32;
    public ulong ResidentByteCount;
    public fixed ulong Reserved[4];
}

[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 80)]
internal unsafe struct NativePortableSoftwareImportInput
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong ConfigurationGeneration;
    public ulong ImportGeneration;
    public ulong OperationEpoch;
    public long CommandUtcMilliseconds;
    public uint RowCount;
    public uint KeyByteCount;
    public ulong ValidMask;
    public ulong Flags;
    public fixed ulong Reserved[2];
}

[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 96)]
internal unsafe struct NativePortableSoftwarePersistedPathInput
{
    public uint StructSize;
    public uint Flags;
    public ulong SoftwareHandle;
    public ulong CatalogEntryHandle;
    public ulong DisplayNameHandle;
    public ulong SoftwareKindHandle;
    public ulong PathHandle;
    public ulong RootHandle;
    public long FirstObservedUtcMilliseconds;
    public uint ExecutablePathOffset;
    public uint ExecutablePathLength;
    public uint RootPathOffset;
    public uint RootPathLength;
    public fixed ulong Reserved[2];
}

[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 144)]
internal unsafe struct NativePortableSoftwareObserveInput
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong ConfigurationGeneration;
    public ulong OperationEpoch;
    public long CommandUtcMilliseconds;
    public long ObservedAtUtcMilliseconds;
    public ulong SoftwareHandle;
    public ulong CatalogEntryHandle;
    public ulong DisplayNameHandle;
    public ulong SoftwareKindHandle;
    public ulong PathHandle;
    public ulong RootHandle;
    public uint ExecutablePathOffset;
    public uint ExecutablePathLength;
    public uint RootPathOffset;
    public uint RootPathLength;
    public uint PathFlags;
    public uint ReservedU32;
    public ulong ValidMask;
    public ulong Flags;
    public fixed ulong Reserved[2];
}

[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 88)]
internal unsafe struct NativePortableSoftwareConfirmRootInput
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong ConfigurationGeneration;
    public ulong OperationEpoch;
    public long CommandUtcMilliseconds;
    public ulong SoftwareHandle;
    public ulong RootHandle;
    public uint RootPathOffset;
    public uint RootPathLength;
    public ulong ValidMask;
    public ulong Flags;
    public fixed ulong Reserved[2];
}

[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 80)]
internal unsafe struct NativePortableSoftwareMarkMissingInput
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong ConfigurationGeneration;
    public ulong OperationEpoch;
    public long CommandUtcMilliseconds;
    public ulong SoftwareHandle;
    public ulong PathHandle;
    public ulong ValidMask;
    public ulong Flags;
    public fixed ulong Reserved[2];
}

[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 64)]
internal unsafe struct NativePortableSoftwarePlanPersistenceInput
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong ConfigurationGeneration;
    public ulong PlanEpoch;
    public uint MaximumOperationCount;
    public uint ReservedU32;
    public ulong ValidMask;
    public ulong Flags;
    public fixed ulong Reserved[2];
}

[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 88)]
internal unsafe struct NativePortableSoftwarePersistenceOperation
{
    public uint StructSize;
    public uint Flags;
    public ulong MutationVersion;
    public ulong SoftwareHandle;
    public ulong CatalogEntryHandle;
    public ulong DisplayNameHandle;
    public ulong SoftwareKindHandle;
    public ulong PathHandle;
    public ulong RootHandle;
    public long FirstObservedUtcMilliseconds;
    public fixed ulong Reserved[2];
}

[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 96)]
internal unsafe struct NativePortableSoftwarePersistencePlanOutput
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong ConfigurationGeneration;
    public ulong StateRevision;
    public ulong PlanEpoch;
    public uint OperationCount;
    public uint TotalDirtyCount;
    public uint Flags;
    public uint ReservedU32;
    public ulong FirstMutationVersion;
    public ulong LastMutationVersion;
    public ulong NextMutationVersion;
    public fixed ulong Reserved[3];
}

[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 64)]
internal unsafe struct NativePortableSoftwarePersistenceFeedbackInput
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong ConfigurationGeneration;
    public ulong FeedbackEpoch;
    public uint FeedbackCount;
    public uint ReservedU32;
    public ulong ValidMask;
    public ulong Flags;
    public fixed ulong Reserved[2];
}

[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 48)]
internal unsafe struct NativePortableSoftwarePersistenceFeedback
{
    public uint StructSize;
    public uint ReservedU32;
    public ulong MutationVersion;
    public ulong SoftwareHandle;
    public ulong PathHandle;
    public fixed ulong Reserved[2];
}

[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 72)]
internal unsafe struct NativePortableSoftwareSnapshotInput
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong ConfigurationGeneration;
    public ulong SnapshotEpoch;
    public uint RegistrationCursor;
    public uint PathCursor;
    public uint MaximumRegistrationCount;
    public uint MaximumPathCount;
    public ulong ValidMask;
    public ulong Flags;
    public fixed ulong Reserved[2];
}

[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 80)]
internal unsafe struct NativePortableSoftwareRegistrationSnapshot
{
    public uint StructSize;
    public uint Flags;
    public ulong SoftwareHandle;
    public ulong CatalogEntryHandle;
    public ulong DisplayNameHandle;
    public ulong SoftwareKindHandle;
    public long FirstObservedUtcMilliseconds;
    public uint ActivePathCount;
    public uint ConfirmedRootCount;
    public uint DirtyPathCount;
    public uint ReservedU32;
    public fixed ulong Reserved[2];
}

[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 80)]
internal unsafe struct NativePortableSoftwarePathSnapshot
{
    public uint StructSize;
    public uint Flags;
    public ulong MutationVersion;
    public ulong SoftwareHandle;
    public ulong PathHandle;
    public ulong RootHandle;
    public long FirstObservedUtcMilliseconds;
    public uint ExecutablePathLength;
    public uint RootPathLength;
    public fixed ulong Reserved[3];
}

[StructLayout(LayoutKind.Sequential, Pack = 8, Size = 144)]
internal unsafe struct NativePortableSoftwareSnapshotOutput
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong ConfigurationGeneration;
    public ulong ImportGeneration;
    public ulong StateRevision;
    public ulong LastOperationEpoch;
    public ulong LastPlanEpoch;
    public ulong LastFeedbackEpoch;
    public ulong LastSnapshotEpoch;
    public long LastCommandUtcMilliseconds;
    public ulong NextMutationVersion;
    public ulong CommittedMutationVersion;
    public uint RegistrationCount;
    public uint PathCount;
    public uint DirtyPathCount;
    public uint RegistrationOutputCount;
    public uint PathOutputCount;
    public uint NextRegistrationCursor;
    public uint NextPathCursor;
    public uint Flags;
    public ulong ResidentByteCount;
    public fixed ulong Reserved[2];
}
