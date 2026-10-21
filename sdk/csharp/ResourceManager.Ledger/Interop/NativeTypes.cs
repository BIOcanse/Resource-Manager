using System.Runtime.InteropServices;

namespace ResourceManager.Ledger.Interop;

[StructLayout(LayoutKind.Sequential)]
internal struct NativeLease
{
    internal uint Slot;
    internal uint Reserved;
    internal ulong AllocationNumber;
    internal ulong UseGeneration;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeBorrow
{
    internal NativeLease Lease;
    internal void* Data;
    internal ulong Length;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeAttributes
{
    internal uint Recoverability;
    internal uint RecoveryCostCoefficient;
    internal uint AccessLossImpact;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeUpdate
{
    internal uint PresentMask;
    internal uint Recoverability;
    internal uint RecoveryCostCoefficient;
    internal uint AccessLossImpact;
    internal ulong AllocationBytes;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeTarget
{
    internal uint Kind;
    internal uint Table;
    internal byte* ParentUid;
    internal ulong ParentUidLength;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativePartitionResult
{
    internal ulong AllocationNumber;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeSubscriptionHandle
{
    internal uint Table;
    internal uint Slot;
    internal ulong Generation;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeTableView
{
    internal ulong RegisteredBytes;
    internal ulong SystemPageBytes;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeTickResult
{
    internal ulong Released;
    internal ulong Scanned;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeDevelopmentStatistics
{
    internal ulong NewAllocations;
    internal ulong SameEpochReleases;
    internal ulong SameEpochAllocationBytes;
    internal ulong SubpageAllocations;
    internal ulong SubpageAllocationBytes;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeTypeLayout
{
    internal uint Size;
    internal uint Alignment;
    internal uint FieldCount;
    internal uint Reserved;
    internal fixed uint FieldOffsets[8];

    internal uint GetFieldOffset(int index)
    {
        if ((uint)index >= 8) throw new ArgumentOutOfRangeException(nameof(index));
        fixed (uint* offsets = FieldOffsets) return offsets[index];
    }
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeDescriptor
{
    internal uint DescriptorSize;
    internal uint AbiVersion;
    internal uint UidWidthBits;
    internal uint CandidateId;
    internal uint TableCount;
    internal uint FixedCount;
    internal uint MaxSubscribersPerTable;
    internal uint ResourceCapacity;
    internal uint PartitionCapacity;
    internal uint IndexNodeCount;
    internal uint IndexHomeCount;
    internal uint Reserved;
    internal ulong NativePageSize;
    internal ulong InstanceSize;
    internal ulong InstanceAlignment;
    internal ulong OperationWorkspaceSize;
    internal ulong OperationWorkspaceAlignment;
    internal ulong TickWorkspaceSize;
    internal ulong TickWorkspaceAlignment;
    internal ulong DestroyWorkspaceSize;
    internal ulong DestroyWorkspaceAlignment;
    internal fixed byte StructureHash[32];
    internal fixed byte PolicyHash[32];
    internal NativeTypeLayout LeaseLayout;
    internal NativeTypeLayout BorrowLayout;
    internal NativeTypeLayout AttributesLayout;
    internal NativeTypeLayout UpdateLayout;
    internal NativeTypeLayout TargetLayout;
    internal NativeTypeLayout PartitionResultLayout;
    internal NativeTypeLayout SubscriptionLayout;
    internal NativeTypeLayout TableViewLayout;
    internal NativeTypeLayout TickResultLayout;
}
