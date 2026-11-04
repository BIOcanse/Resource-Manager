using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ResourceManager.App.Infrastructure.ResourceBreakdown;
using ResourceManager.ReadWrite.Windows.Processes;

namespace ResourceManager.ReadWrite.Native.Processes;

/// <summary>Dedicated, synchronous borrowed-input bridge. No fallback, current, permission or handle owner.
/// The serial integrator registers the exports before the original source switches to these entry points.</summary>
internal static unsafe class NativeProcessFacts
{
    internal enum InventoryStatus { Current, InvalidArgument, InvalidTable, VisitorRejected }
    internal enum ImagePathStatus : uint { Current, QueryFailed, InvalidResult, InvalidArgument }
    [StructLayout(LayoutKind.Sequential)]
    internal struct ImagePathResult
    {
        internal ImagePathStatus Status;
        internal uint Win32Error;
        internal nuint Characters;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Limits
    {
        internal nuint MaximumBytes, MaximumProcesses;
        internal uint MaximumThreadsPerProcess;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Row
    {
        internal int ProcessId, ParentProcessId;
        internal uint SessionId, Flags;
        internal long CreationFileTime100Ns;
        internal char* Name;
        internal nuint NameLength;
        internal ulong KernelTime, UserTime, WorkingSet, PrivateCommit, PeakPrivateCommit, PrivateWorkingSet;
        internal ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
        internal uint HandleCount, ThreadCount;
    }

    private sealed class Candidate(ProcessIoQueryTiming? timing)
    {
        internal readonly HashSet<int> Seen = [];
        internal readonly List<WindowsProcessInventoryEntry> Rows = [];
        internal ProcessIoQueryTiming? Timing { get; } = timing;
    }

    [RuntimeRead(ReadVolatilityKind.Sampled)]
    internal static InventoryStatus ReadInventory(ReadOnlySpan<byte> table, WindowsProcessDetailsOptions options,
        ProcessIoQueryTiming? timing, out WindowsProcessInventoryEntry[] rows)
    {
        ArgumentNullException.ThrowIfNull(options);
        ReadWriteNativeLibraryResolver.EnsureRegistered();
        rows = [];
        var candidate = new Candidate(timing);
        var context = GCHandle.Alloc(candidate);
        try
        {
            var limits = new Limits { MaximumBytes = (nuint)options.MaximumSystemInformationBufferBytes,
                MaximumProcesses = (nuint)options.MaximumProcesses,
                MaximumThreadsPerProcess = (uint)options.MaximumThreadsPerProcess };
            fixed (byte* data = table)
            {
                var status = Visit(data, (nuint)table.Length, &limits, GCHandle.ToIntPtr(context), &ReceiveRow, out var count);
                if (status != InventoryStatus.Current) return status;
                if (count != (nuint)candidate.Rows.Count) return InventoryStatus.InvalidTable;
                rows = candidate.Rows.ToArray();
                return InventoryStatus.Current;
            }
        }
        finally { context.Free(); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int ReceiveRow(nint context, Row* row)
    {
        // This callback only builds an unpublished candidate. Managed exceptions never cross the ABI.
        try
        {
            var candidate = (Candidate)GCHandle.FromIntPtr(context).Target!;
            if (!candidate.Seen.Add(row->ProcessId)) return 1;
            long? creation = (row->Flags & 1) != 0 ? row->CreationFileTime100Ns : null;
            var counters = new WindowsProcessTableCounters(row->KernelTime, row->UserTime, row->WorkingSet,
                row->PrivateCommit, row->PeakPrivateCommit, row->PrivateWorkingSet,
                row->ReadOperations, row->WriteOperations, row->OtherOperations, row->ReadBytes, row->WriteBytes,
                row->OtherBytes, row->HandleCount, (row->Flags & 8) != 0 ? row->ThreadCount : null, candidate.Timing);
            candidate.Rows.Add(new(row->ProcessId, new(creation is null ? ProcessReadStatus.Unavailable : ProcessReadStatus.Current, creation),
                (row->Flags & 2) != 0 ? row->ParentProcessId : null,
                (row->Flags & 4) != 0 ? new string(row->Name, 0, checked((int)row->NameLength)) : null,
                Counters: counters) { SessionId = row->SessionId });
            return 0;
        }
        catch { return 1; }
    }

    /// <summary>The caller owns the buffer and its original growth/bounds policy. No allocation or sampling.</summary>
    [RuntimeRead(ReadVolatilityKind.Fixed)]
    internal static ImagePathResult ReadImagePath(nint target, Span<char> buffer)
    {
        ReadWriteNativeLibraryResolver.EnsureRegistered();
        fixed (char* data = buffer)
        {
            var status = ImagePath(target, data, (nuint)buffer.Length, out var result);
            return status == 0 ? result : new ImagePathResult { Status = ImagePathStatus.InvalidArgument };
        }
    }

    [DllImport("ResourceManager.NativeCore", EntryPoint = "rm_process_facts_inventory_visit", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern InventoryStatus Visit(byte* data, nuint length, Limits* limits, nint context,
        delegate* unmanaged[Cdecl]<nint, Row*, int> visitor, out nuint count);

    [DllImport("ResourceManager.NativeCore", EntryPoint = "rm_process_facts_image_path_read", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern int ImagePath(nint target, char* buffer, nuint capacity, out ImagePathResult result);
}
