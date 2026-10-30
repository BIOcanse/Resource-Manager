using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ResourceManager.ReadWrite.Native.DataArea;

// Thin entry over the region owner in NativeCore (src/Core/NativeCore/src/data_area,
// backend_data_area §12 step 1). The owner, its layout and its checks are in Zig;
// this file only declares the C ABI. There is no HTTP entry yet (step 2).
internal static unsafe partial class DataAreaAbi
{
    internal const uint Version = 0x0001_0000;
    // An absent index (SharedSection `none`).
    internal const uint None = uint.MaxValue;
    // The owner's default when DataAreaSettings is not passed.
    internal const uint DefaultBorrowDrainTimeoutMilliseconds = 5000;
    // SharedSection data_kind of each region, for a reader's decoder check.
    internal const uint NormalDataKind = 0x4441_0001;
    internal const uint AdministratorDataKind = 0x4441_0002;

    private const string LibraryName = "ResourceManager.NativeCore";

    static DataAreaAbi() => ReadWriteNativeLibraryResolver.EnsureRegistered();

    [LibraryImport(LibraryName, EntryPoint = "rm_data_area_abi_version")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint AbiVersion();

    // `settings` may be null for the defaults; `settingsSize` is sizeof(DataAreaSettings) otherwise.
    [LibraryImport(LibraryName, EntryPoint = "rm_data_area_create")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial DataAreaStatus Create(DataAreaSettings* settings, uint settingsSize, IntPtr* handle);

    // Compiles the run plan's declarations into both regions. Atomic: on any
    // failure the regions stay as they were and `failure` names the cause.
    [LibraryImport(LibraryName, EntryPoint = "rm_data_area_apply")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial DataAreaStatus Apply(IntPtr handle, DataAreaDeclaration* declarations, uint count,
        long nowUnixMilliseconds, DataAreaApplyFailure* failure);

    [LibraryImport(LibraryName, EntryPoint = "rm_data_area_binding")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial DataAreaStatus Binding(IntPtr handle, uint declaration, DataAreaBinding* binding);

    // Fills `sources` with the declarations' global source indexes, ascending.
    // BufferTooSmall still sets view.SourceCount to the needed capacity.
    [LibraryImport(LibraryName, EntryPoint = "rm_data_area_read_view")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial DataAreaStatus ReadView(IntPtr handle, uint* declarations, uint count,
        uint* sources, uint capacity, DataAreaReadView* view);

    // Borrows the region's current mapping; every success needs one Release.
    [LibraryImport(LibraryName, EntryPoint = "rm_data_area_acquire")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial DataAreaStatus Acquire(IntPtr handle, DataAreaRegion region, ulong layoutGeneration,
        DataAreaBorrow* borrow);

    [LibraryImport(LibraryName, EntryPoint = "rm_data_area_release")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial DataAreaStatus Release(IntPtr handle, DataAreaBorrow* borrow);

    // Service stop: writes every source ended, then unmaps once borrows end.
    [LibraryImport(LibraryName, EntryPoint = "rm_data_area_stop")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial DataAreaStatus Stop(IntPtr handle, long nowUnixMilliseconds);

    // Refuses, and keeps the handle valid, while a region is still mapped.
    [LibraryImport(LibraryName, EntryPoint = "rm_data_area_destroy")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial DataAreaStatus Destroy(IntPtr handle);
}

internal enum DataAreaRegion : uint
{
    Normal = 0,
    Administrator = 1,
}

internal enum DataAreaStatus : uint
{
    Ok = 0,
    InvalidArgument = 1,
    AbiMismatch = 2,
    OutOfMemory = 3,
    DeclarationInvalid = 4,
    RegionTooLarge = 5,
    SectionFailed = 6,
    Borrowed = 7,
    Stopped = 8,
    StaleBinding = 9,
    Replacing = 10,
    Absent = 11,
    MixedRegions = 12,
    BufferTooSmall = 13,
    NotBorrowed = 14,
    NotStopped = 15,
}

// SharedSection PositionKind.
internal enum DataAreaPositionKind : uint
{
    Scalar = 1,
    Column = 2,
    StringColumn = 3,
    StringTable = 4,
}

// SharedSection SourceIdentity, 88 bytes.
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct DataAreaSourceIdentity
{
    internal const int OwnerNameCapacity = 64;
    internal uint Kind;
    internal uint ProcessId;
    internal ulong ProcessStartTime;
    internal uint OwnerNameLength;
    internal fixed byte OwnerName[OwnerNameCapacity];
}

// One position of a declaration; Source, Identity and Strings are local to it.
[StructLayout(LayoutKind.Sequential)]
internal struct DataAreaPositionDeclaration
{
    internal uint Source;
    internal DataAreaPositionKind Kind;
    internal uint ElementWidth;
    internal uint Capacity;
    internal uint Identity;
    internal uint Strings;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct DataAreaDeclaration
{
    internal DataAreaRegion Region;
    internal uint SourceCount;
    internal uint PositionCount;
    internal uint Reserved;
    internal DataAreaSourceIdentity* Sources;
    internal DataAreaPositionDeclaration* Positions;
}

// Position is local to Declaration; SharedSectionFailure is the package's code.
[StructLayout(LayoutKind.Sequential)]
internal struct DataAreaApplyFailure
{
    internal uint Declaration;
    internal uint Position;
    internal uint SharedSectionFailure;
    internal uint Win32Error;
    internal uint Region;
    internal uint Reserved;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DataAreaSettings
{
    // 0 does not wait: a replacement or stop with a live borrow returns Borrowed.
    internal uint BorrowDrainTimeoutMilliseconds;
    internal uint Reserved;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DataAreaBinding
{
    internal DataAreaRegion Region;
    internal uint SourceBase;
    internal uint SourceCount;
    internal uint PositionBase;
    internal uint PositionCount;
    internal uint Reserved;
    internal ulong LayoutGeneration;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DataAreaReadView
{
    internal DataAreaRegion Region;
    internal uint SourceCount;
    internal ulong LayoutGeneration;
}

// The region address is taken at each round's begin and is not kept past Release.
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct DataAreaBorrow
{
    internal byte* RegionAddress;
    internal ulong ByteLength;
    internal ulong Epoch;
    internal ulong LayoutGeneration;
    internal DataAreaRegion Region;
    internal uint Reserved;
}
