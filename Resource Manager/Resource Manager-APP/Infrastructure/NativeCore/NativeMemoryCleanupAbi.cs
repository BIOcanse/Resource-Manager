using System.Runtime.InteropServices;

namespace ResourceManager.App.Infrastructure.NativeCore;

internal static class NativeMemoryCleanupAbi
{
    public const uint Version = 0x0001_0004;
}

internal enum NativeMemoryCleanupMode : uint
{
    Normal = 0,
    Emergency = 1
}

[Flags]
internal enum NativeMemoryCleanupRequestFlags : uint
{
    None = 0,
    AllowNormal = 1 << 0,
    EvaluateEmergency = 1 << 1,
    ForceEmergency = 1 << 2
}

[Flags]
internal enum NativeMemoryCleanupInputFlags : uint
{
    None = 0,
    CanApply = 1 << 0
}

[Flags]
internal enum NativeMemoryCleanupFeedbackFlags : uint
{
    None = 0,
    Attempted = 1 << 0,
    Succeeded = 1 << 1
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeMemoryCleanupConfig
{
    public uint AbiVersion;
    public uint StructSize;
    public ulong Generation;
    public uint StateCapacity;
    public uint Reserved0;
    public double CriticalFreeRatio;
    public double VeryLowFreeRatio;
    public double LowFreeRatio;
    public double GuardedFreeRatio;
    public double PhysicalEmergencyFreeRatio;
    public double VirtualEmergencyFreeRatio;
    public double HighTierMinimumBaseScore;
    public uint CriticalBatchCount;
    public uint VeryLowBatchCount;
    public uint LowBatchCount;
    public uint GuardedBatchCount;
    public uint EmergencyBatchCount;
    public uint Reserved1;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeMemoryCleanupPlanHeader
{
    public uint AbiVersion;
    public uint StructSize;
    public uint InputStructSize;
    public uint OutputStructSize;
    public uint InputCount;
    public uint OutputCapacity;
    public uint OutputCount;
    public NativeMemoryCleanupRequestFlags RequestFlags;
    public double OrdinaryMemoryFreeRatio;
    public double PhysicalMemoryFreeRatio;
    public double VirtualMemoryFreeRatio;
    public ulong ConfigGeneration;
    public ulong StateRevision;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeMemoryCleanupCandidateInput
{
    public uint StructSize;
    public NativeMemoryCleanupInputFlags Flags;
    public uint ProcessId;
    public uint RuntimeState;
    public ulong ProcessStartKey;
    public ulong TargetKey;
    public double BaseScore;
    public double CpuScore;
    public double MemoryUsedPercent;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeMemoryCleanupDecisionOutput
{
    public uint StructSize;
    public uint SourceInputIndex;
    public uint ProcessId;
    public uint StateSlot;
    public uint StateGeneration;
    public NativeMemoryCleanupMode Mode;
    public uint Reserved0;
    public uint Reserved1;
    public ulong ProcessStartKey;
    public ulong TargetKey;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeMemoryCleanupFeedbackInput
{
    public uint StructSize;
    public uint StateSlot;
    public uint StateGeneration;
    public NativeMemoryCleanupFeedbackFlags Flags;
}
