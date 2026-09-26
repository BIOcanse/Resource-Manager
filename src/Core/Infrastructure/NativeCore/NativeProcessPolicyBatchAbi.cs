using System.Runtime.InteropServices;

namespace ResourceManager.App.Infrastructure.NativeCore;

internal static class NativeProcessPolicyBatchAbi
{
    public const uint Version = 0x0002_0000;
    public const uint LittleEndianFlag = 1 << 0;
    public const uint MemoryPriorityConflictError = 0xE001_0001;
}

[Flags]
internal enum NativeProcessPolicyFields : uint
{
    None = 0,
    PriorityClass = 1 << 0,
    AffinityMask = 1 << 1,
    MemoryPriority = 1 << 2,
    PowerThrottling = 1 << 3,
    TrimWorkingSet = 1 << 4
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeProcessPolicyBatchHeader
{
    public uint AbiVersion;
    public uint StructSize;
    public uint ItemStructSize;
    public uint ResultStructSize;
    public uint ItemCount;
    public uint Flags;
    public uint Reserved0;
    public uint Reserved1;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeProcessPolicyBatchItem
{
    public uint StructSize;
    public uint ProcessId;
    public NativeProcessPolicyFields Fields;
    public uint PriorityClass;
    public uint MemoryPriority;
    public uint PowerControlMask;
    public uint PowerStateMask;
    public uint ExpectedMemoryPriority;
    public ulong AffinityMask;
    public ulong ExpectedStartFileTime;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeProcessPolicyBatchItemResult
{
    public uint StructSize;
    public uint ProcessId;
    public NativeProcessPolicyFields RequestedFields;
    public NativeProcessPolicyFields SucceededFields;
    public uint OpenError;
    public uint IdentityError;
    public uint PriorityError;
    public uint AffinityError;
    public uint MemoryError;
    public uint PowerError;
    public uint TrimError;
    public uint ObservedMemoryPriority;
}
