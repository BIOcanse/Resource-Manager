using System.Runtime.InteropServices;

namespace ResourceManager.App.Infrastructure.Telemetry.Etw;

/// <summary>Copies a closed IO publication once per version while retaining its native bank.</summary>
internal static unsafe class KernelEtwNativeIoReaders
{
    internal static DiskConfiguration GetDiskConfiguration(NativeKernelEtwSession session)
    {
        using var owner = session.AcquireReadLease();
        KernelEtwNativeReaders.Check(QueryDiskConfiguration(owner.OwnerHandle, out var configuration, (uint)sizeof(DiskConfiguration)), "query disk configuration");
        return configuration;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct DiskConfiguration
    {
        public long BucketMilliseconds;
        public uint MaximumPairs, Reserved;
        public ulong ByteCap;
    }
    internal static ulong Reset(NativeKernelEtwSession session, uint mask, long tick)
    {
        using var owner = session.AcquireReadLease();
        KernelEtwNativeReaders.Check(ResetNative(owner.OwnerHandle, mask, tick, out var ticket), "reset IO binding");
        return ticket;
    }
    internal static bool Read<T>(NativeKernelEtwSession session, ref ulong version, Func<View, T> copy, out T? publication, out int failure)
    {
        using var owner = session.AcquireReadLease();
        publication = default;
        failure = QueryFailure(owner.OwnerHandle);
        if (failure != 0) return false;
        var status = Acquire(owner.OwnerHandle, out var view, out var next, out var token);
        if (status == 3) { failure = QueryFailure(owner.OwnerHandle); return false; }
        KernelEtwNativeReaders.Check(status, "acquire IO publication");
        try
        {
            failure = QueryFailure(owner.OwnerHandle);
            if (failure != 0 || next == version) return false;
            publication = copy(view);
            failure = QueryFailure(owner.OwnerHandle);
            if (failure != 0) { publication = default; return false; }
            version = next;
            return true;
        }
        finally { KernelEtwNativeReaders.Check(Release(owner.OwnerHandle, token), "release IO publication"); }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct View
    {
        public long ThroughTick, WindowMilliseconds;
        public uint Flags, ProcessDiskCount;
        public nint ProcessDisks;
        public uint DiskCount, KnownDiskCount;
        public nint Disks, KnownDisks;
        public uint NetworkCount, Reserved;
        public nint Networks;
        public ulong NetworkTransitionVersion;
        public ulong Reserved2;
        public ulong EventsLost;
        public ulong DiskTransitionVersion;

        internal readonly ReadOnlySpan<ProcessDisk> ProcessDiskRows => Rows<ProcessDisk>(ProcessDisks, ProcessDiskCount);
        internal readonly ReadOnlySpan<Disk> DiskRows => Rows<Disk>(Disks, DiskCount);
        internal readonly ReadOnlySpan<uint> KnownDiskNumbers => Rows<uint>(KnownDisks, KnownDiskCount);
        internal readonly ReadOnlySpan<Network> NetworkRows => Rows<Network>(Networks, NetworkCount);
    }
    private static ReadOnlySpan<T> Rows<T>(nint pointer, uint count) where T : unmanaged
    {
        if (count != 0 && pointer == 0) throw new InvalidDataException("Native IO publication rows are absent.");
        return new ReadOnlySpan<T>((void*)pointer, checked((int)count));
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct ProcessDisk
    {
        public uint Pid, Flags;
        public ulong Birth, ReadBytes, WriteBytes;
        public double ReadRate, WriteRate;
        public uint ReadCount, WriteCount;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Disk
    {
        public uint Pid, Number, Flags, Reserved;
        public ulong Birth, ReadBytes, WriteBytes;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Network
    {
        public uint Pid, Flags;
        public ulong Birth, ReceiveBytes, SendBytes;
        public double ReceiveRate, SendRate;
        public uint Count, ExternalCount;
        public ulong ExternalReceiveBytes, ExternalSendBytes;
        public double ExternalReceiveRate, ExternalSendRate;
    }
    [DllImport("ResourceManager.NativeCore", EntryPoint = "rm_kernel_etw_shared_acquire_io", CallingConvention = CallingConvention.Cdecl)]
    private static extern int Acquire(nint owner, out View view, out ulong version, out ulong token);
    [DllImport("ResourceManager.NativeCore", EntryPoint = "rm_kernel_etw_shared_release", CallingConvention = CallingConvention.Cdecl)]
    private static extern int Release(nint owner, ulong token);
    [DllImport("ResourceManager.NativeCore", EntryPoint = "rm_kernel_etw_shared_reset_io_at", CallingConvention = CallingConvention.Cdecl)]
    private static extern int ResetNative(nint owner, uint mask, long tick, out ulong ticket);
    [DllImport("ResourceManager.NativeCore", EntryPoint = "rm_kernel_etw_shared_query_failure", CallingConvention = CallingConvention.Cdecl)]
    private static extern int QueryFailure(nint owner);
    [DllImport("ResourceManager.NativeCore", EntryPoint = "rm_kernel_etw_shared_query_disk_config", CallingConvention = CallingConvention.Cdecl)]
    private static extern int QueryDiskConfiguration(nint owner, out DiskConfiguration configuration, uint size);
}
