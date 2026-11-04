using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ResourceManager.ReadWrite.Native.SharedSection;

/// <summary>Thin binding to SharedSection's current C ABI. All memory belongs to the caller.</summary>
public static unsafe partial class SharedSectionAbi
{
    public const string LibraryName = "ResourceManager.NativeCore";
    public const uint Magic = 0x53534D52;
    public const uint Version = 1;
    public const uint None = uint.MaxValue;
    public const int OwnerNameCapacity = 64;
    public const uint NotWrittenReason = 0;
    public const ulong EndedBit = 1UL << 63;
    public const uint DefaultRetryLimit = 1000;

    [LibraryImport(LibraryName, EntryPoint = "rm_shared_section_required_size")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial ulong RequiredSize(uint sourceCapacity, uint positionCapacity, uint dataCapacity);

    [LibraryImport(LibraryName, EntryPoint = "rm_shared_section_initialize")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial SharedSectionFailure Initialize(byte* region, ulong length, SharedSectionLayoutSpec* spec);

    [LibraryImport(LibraryName, EntryPoint = "rm_shared_section_describe")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial SharedSectionFailure Describe(byte* region, ulong length, SharedSectionHeader* header);

    [LibraryImport(LibraryName, EntryPoint = "rm_shared_section_source")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial SharedSectionFailure Source(byte* region, ulong length, uint index, SharedSectionSourceIdentity* identity);

    [LibraryImport(LibraryName, EntryPoint = "rm_shared_section_position")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial SharedSectionFailure Position(byte* region, ulong length, uint index, SharedSectionPositionSpec* spec);

    [LibraryImport(LibraryName, EntryPoint = "rm_shared_section_write_begin")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial SharedSectionFailure WriteBegin(byte* region, ulong length, uint source, SharedSectionWriter* writer);

    [LibraryImport(LibraryName, EntryPoint = "rm_shared_section_write_elements")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial SharedSectionFailure WriteElements(SharedSectionWriter* writer, uint position, uint first, byte* values, ulong byteLength);

    [LibraryImport(LibraryName, EntryPoint = "rm_shared_section_write_length")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial SharedSectionFailure WriteLength(SharedSectionWriter* writer, uint position, uint length, ulong identitySequence);

    [LibraryImport(LibraryName, EntryPoint = "rm_shared_section_write_end")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial SharedSectionFailure WriteEnd(SharedSectionWriter* writer, SharedSectionSourceState state, uint reason, long writeTimeUnixMs);

    [LibraryImport(LibraryName, EntryPoint = "rm_shared_section_selection_size")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial SharedSectionFailure SelectionSize(byte* region, ulong length, uint* sources, uint sourceCount, SharedSectionSelectionSize* size);

    [LibraryImport(LibraryName, EntryPoint = "rm_shared_section_select")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial SharedSectionFailure Select(byte* region, ulong length, uint* sources, uint sourceCount, SharedSectionSelection* selection);

    [LibraryImport(LibraryName, EntryPoint = "rm_shared_section_read")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial SharedSectionReadStatus Read(byte* region, ulong length, SharedSectionSelection* selection, uint source, uint retryLimit);

    [LibraryImport(LibraryName, EntryPoint = "rm_shared_section_position_value")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial SharedSectionPositionStatus PositionValue(SharedSectionSelection* selection, uint position, SharedSectionValueView* value);

    [LibraryImport(LibraryName, EntryPoint = "rm_shared_section_end")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial SharedSectionFailure End(byte* region, ulong length, long writeTimeUnixMs);
}

public enum SharedSectionSourceState : uint { HasValue = 1, Unavailable = 2, Ended = 3 }
public enum SharedSectionPositionKind : uint { Scalar = 1, Column = 2, StringColumn = 3, StringTable = 4 }
public enum SharedSectionCopyStatus : uint { None = 0, HasValue = 1, Unavailable = 2, Ended = 3, Failed = 4 }
public enum SharedSectionReadStatus : uint { Unchanged = 0, HasValue = 1, Unavailable = 2, Ended = 3, Failed = 4 }
public enum SharedSectionPositionStatus : uint { HasValue = 1, Unavailable = 2, Ended = 3, Failed = 4, NotArrived = 5 }
public enum SharedSectionFailure : uint
{
    Success = 0, RegionTooSmall = 1, RegionMisaligned = 2, BadMagic = 3, BadVersion = 4,
    CountOverCapacity = 5, DataCapacity = 6, SourceIndex = 7, PositionIndex = 8,
    PositionSource = 9, PositionKind = 10, PositionWidth = 11, PositionRange = 12,
    PositionIdentity = 13, PositionStrings = 14, PositionOverlap = 15, OwnerName = 16,
    SourceState = 17, LengthOverCapacity = 18, StringRange = 19, RowsMismatch = 20,
    RetryLimit = 21, CopyTooSmall = 22, NotOwner = 23, WrongKind = 24,
    ElementWidth = 25, ElementRange = 26, WriteInProgress = 27, NotWriting = 28,
    Ended = 29, InvalidArgument = 30, NotSelected = 31, IdentityNotSelected = 32,
    LayoutChanged = 33, SelectionOrder = 34
}

[StructLayout(LayoutKind.Sequential)]
public struct SharedSectionHeader
{
    public uint Magic, Version, DataKind, SourceCapacity, SourceCount, PositionCapacity, PositionCount, DataCapacity;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct SharedSectionSourceIdentity
{
    public uint Kind, ProcessId;
    public ulong ProcessStartTime;
    public uint OwnerNameLength;
    public fixed byte OwnerName[SharedSectionAbi.OwnerNameCapacity];
}

[StructLayout(LayoutKind.Sequential)]
public struct SharedSectionSourceEntry
{
    public ulong Sequence;
    public long WriteTimeUnixMs;
    public SharedSectionSourceState State;
    public uint Reason;
    public SharedSectionSourceIdentity Identity;
}

[StructLayout(LayoutKind.Sequential)]
public struct SharedSectionPositionSpec
{
    public uint Source;
    public SharedSectionPositionKind Kind;
    public uint Offset, Size, ElementWidth, Identity, Strings;
}

[StructLayout(LayoutKind.Sequential)]
public struct SharedSectionPositionEntry
{
    public SharedSectionPositionSpec Spec;
    public uint Length;
    public ulong IdentitySequence;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct SharedSectionLayoutSpec
{
    public SharedSectionSourceIdentity* Sources;
    public SharedSectionPositionSpec* Positions;
    public uint DataKind, SourceCapacity, PositionCapacity, DataCapacity, SourceCount, PositionCount;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct SharedSectionWriter
{
    public byte* Region;
    public ulong RegionLength, Sequence;
    public uint Source;
}

[StructLayout(LayoutKind.Sequential)]
public struct SharedSectionSourceCopy
{
    public ulong Sequence;
    public long WriteTimeUnixMs;
    public SharedSectionCopyStatus Status;
    public uint Reason;
    public uint Source;
}

[StructLayout(LayoutKind.Sequential)]
public struct SharedSectionSelectedPosition
{
    public SharedSectionPositionEntry Entry;
    public ulong Offset;
    public uint Index;
}

[StructLayout(LayoutKind.Sequential)]
public struct SharedSectionSelectionSize
{
    public ulong DataBytes;
    public uint PositionCount;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct SharedSectionSelection
{
    public SharedSectionHeader Header;
    public SharedSectionSourceCopy* Held;
    public SharedSectionSelectedPosition* Positions;
    public byte* Data;
    public ulong DataLength;
    public uint HeldLength, PositionLength;
}

[StructLayout(LayoutKind.Sequential)]
public struct SharedSectionValueView
{
    public ulong Offset, ByteLength;
    public uint Length, Reason;
    public SharedSectionPositionStatus Status;
}
