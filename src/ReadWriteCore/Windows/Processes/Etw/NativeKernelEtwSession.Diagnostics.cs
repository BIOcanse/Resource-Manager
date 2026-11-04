using System.Runtime.InteropServices;
using System.Text;

namespace ResourceManager.App.Infrastructure.Telemetry.Etw;

public enum KernelEtwNativeOwnerKind : uint
{
    Shared = 1, Residency = 2, Interrupts = 3, Disk = 4, Network = 5, File = 6, History = 7
}

/// <summary>Shared includes the child owners; its total must not be added to the child rows.</summary>
public readonly record struct KernelEtwNativeOwnerUsage(KernelEtwNativeOwnerKind Kind,
    uint Flags, ulong CurrentBytes, ulong PeakBytes, ulong ByteCap, ulong FailedAllocations, ulong CapacityFailures);

public readonly record struct KernelEtwNativeRegionUsage(ulong ReservedBytes, ulong UsableBytes,
    ulong OccupiedBytes, ulong PeakOccupiedBytes, ulong RequestedBytes, ulong PeakRequestedBytes,
    ulong LiveAllocations, ulong FailedAllocations);

public readonly record struct KernelEtwNativeFailureDetail(int Result, string? ErrorName, ulong Records, long Qpc);

public sealed unsafe partial class NativeKernelEtwSession
{
    /// <summary>The first native failure, including the input position; null means no failure has been recorded.</summary>
    public KernelEtwNativeFailureDetail? ReadFailureDetail()
    {
        using var lease = AcquireReadLease();
        Check(DiagnosticsNative.FailureDetail(lease.OwnerHandle, out var row, checked((uint)sizeof(NativeFailureDetail))), "read first failure detail");
        if (row.Result == 0) return null;
        if (row.ErrorNameLength != 0 && row.ErrorName == 0)
            throw new InvalidDataException("Native kernel ETW failure detail has no error-name storage.");
        var name = row.ErrorNameLength == 0 ? null
            : Encoding.UTF8.GetString(new ReadOnlySpan<byte>((void*)row.ErrorName, checked((int)row.ErrorNameLength)));
        return new(row.Result, name, row.Records, row.Qpc);
    }

    /// <summary>Reads the latest immutable published diagnostics; null means no diagnostic bank is available.</summary>
    public IReadOnlyList<KernelEtwNativeOwnerUsage>? ReadOwnerUsage()
    {
        using var lease = AcquireReadLease();
        NativeOwnerUsage* rows = stackalloc NativeOwnerUsage[7];
        var result = DiagnosticsNative.OwnerUsage(lease.OwnerHandle, rows, 7, out var count);
        if (result is 3 or 4) return null;
        Check(result, "read owner usage");
        if (count != 7) throw new InvalidDataException("Native kernel ETW returned an unexpected owner count.");
        var output = new KernelEtwNativeOwnerUsage[7];
        uint seen = 0;
        for (var i = 0; i < output.Length; i++)
        {
            var row = rows[i];
            if (row.Kind is < 1 or > 7 || (seen & (1u << (int)row.Kind)) != 0)
                throw new InvalidDataException("Native kernel ETW returned an invalid owner identity.");
            seen |= 1u << (int)row.Kind;
            output[i] = new((KernelEtwNativeOwnerKind)row.Kind, row.Flags, row.CurrentBytes, row.PeakBytes,
                row.ByteCap, row.FailedAllocations, row.CapacityFailures);
        }
        return Array.AsReadOnly(output);
    }

    /// <summary>Reads allocation usage from the latest immutable bank; never queries mutable owner state.</summary>
    public KernelEtwNativeRegionUsage? ReadRegionUsage()
    {
        using var lease = AcquireReadLease();
        var result = DiagnosticsNative.RegionUsage(lease.OwnerHandle, out var row, checked((uint)sizeof(NativeRegionUsage)));
        if (result is 3 or 4) return null;
        Check(result, "read region usage");
        return new(row.ReservedBytes, row.UsableBytes, row.OccupiedBytes, row.PeakOccupiedBytes,
            row.RequestedBytes, row.PeakRequestedBytes, row.LiveAllocations, row.FailedAllocations);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFailureDetail
    {
        public int Result;
        public uint Reserved;
        public nint ErrorName;
        public uint ErrorNameLength, Reserved2;
        public ulong Records;
        public long Qpc;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeOwnerUsage
    {
        public uint Kind, Flags;
        public ulong CurrentBytes, PeakBytes, ByteCap, FailedAllocations, CapacityFailures;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRegionUsage
    {
        public ulong ReservedBytes, UsableBytes, OccupiedBytes, PeakOccupiedBytes,
            RequestedBytes, PeakRequestedBytes, LiveAllocations, FailedAllocations;
    }

    private static class DiagnosticsNative
    {
        [DllImport("ResourceManager.NativeCore", EntryPoint = "rm_kernel_etw_shared_query_failure_detail", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int FailureDetail(nint owner, out NativeFailureDetail failure, uint size);
        [DllImport("ResourceManager.NativeCore", EntryPoint = "rm_kernel_etw_shared_query_owner_usage", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int OwnerUsage(nint owner, NativeOwnerUsage* rows, uint capacity, out uint count);
        [DllImport("ResourceManager.NativeCore", EntryPoint = "rm_kernel_etw_shared_query_region_usage", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int RegionUsage(nint owner, out NativeRegionUsage usage, uint size);
    }
}
