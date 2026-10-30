using System.Runtime.InteropServices;

namespace ResourceManager.App.Infrastructure.Telemetry.Etw;

[StructLayout(LayoutKind.Sequential)]
public struct NativeKernelEtwResidencyConfig
{
    public uint Size, Version;
    public long QpcFrequency;
    public uint WindowMilliseconds, BucketMilliseconds, MaximumPairs, MaximumCells,
        MaximumProcesses, MaximumThreads, MaximumProcessors, MaximumNameBytes;
    public ulong ByteCap;
}

[StructLayout(LayoutKind.Sequential)]
public struct NativeKernelEtwInterruptConfig
{
    public uint Size, Version, MaximumModules, MaximumPathUnits, MaximumIdentities,
        MaximumSlices, MaximumSliceDrivers, Reserved;
    public ulong ByteCap, Generation;
    public long StartedTickMilliseconds;
    public ulong EventsLost;
}

/// <summary>All capacities come from host configuration. The ABI supplies no implicit growth.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct NativeKernelEtwSessionConfig
{
    public uint Size, Version;
    public NativeKernelEtwResidencyConfig Residency;
    public NativeKernelEtwInterruptConfig Interrupts;
    public ulong ByteCap, DiskByteCap, NetworkByteCap, FileByteCap, HistoryByteCap;
    public uint MaximumDiskPairs, MaximumDisks, MaximumCachedProcesses,
        MaximumNetworkEvents, MaximumNetworkProcesses, MaximumFileEntries, MaximumFileVersions,
        MaximumHistoryEntries, MaximumPathUnits, MaximumNamespaceEntries,
        MaximumCommands, MaximumCommandNameBytes, PublicationBanks, Reserved;
    public long WindowMilliseconds, RetentionMilliseconds, DiskBucketMilliseconds, StartedTickMilliseconds;
    public uint MaximumPendingEvents, MaximumPendingPathUnits;
    public uint MaximumRendererProcesses, Reserved3;

    internal void StampAbi(KernelEtwSessionContext context)
    {
        Size = checked((uint)Marshal.SizeOf<NativeKernelEtwSessionConfig>());
        Version = 0x00010000;
        Residency.Size = checked((uint)Marshal.SizeOf<NativeKernelEtwResidencyConfig>());
        Residency.Version = Version;
        Interrupts.Size = checked((uint)Marshal.SizeOf<NativeKernelEtwInterruptConfig>());
        Interrupts.Version = Version;
        Interrupts.Generation = checked((ulong)context.Generation);
        Interrupts.StartedTickMilliseconds = StartedTickMilliseconds;
    }
}
