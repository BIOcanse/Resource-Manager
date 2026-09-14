using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ResourceManager.App.Infrastructure.NativeCore;

internal sealed partial class NativeDisplayCoordinatorSession : IDisposable
{
    private readonly object sync = new();
    private IntPtr handle;
    private NativeDisplayCoordinatorCapacity capacity;

    internal NativeDisplayCoordinatorSession(in NativeDisplayCoordinatorConfiguration configuration)
    {
        var actualVersion = NativeMethods.GetAbiVersion();
        if (actualVersion != NativeDisplayCoordinatorAbi.Version)
        {
            throw new InvalidOperationException(
                $"Native display-coordinator ABI mismatch: expected 0x{NativeDisplayCoordinatorAbi.Version:X8}, actual 0x{actualVersion:X8}.");
        }

        var status = (NativeDisplayCoordinatorStatus)NativeMethods.Create(
            in configuration,
            out handle);
        if (status != NativeDisplayCoordinatorStatus.Ok || handle == IntPtr.Zero)
        {
            handle = IntPtr.Zero;
            throw new InvalidOperationException(
                $"Native display-coordinator session creation failed with {status}.");
        }

        try
        {
            capacity = QueryAndValidateCapacity(handle, in configuration);
        }
        catch
        {
            NativeMethods.Destroy(Interlocked.Exchange(ref handle, IntPtr.Zero));
            throw;
        }
    }

    ~NativeDisplayCoordinatorSession() => Dispose(false);

    internal NativeDisplayCoordinatorCapacity Capacity
    {
        get
        {
            lock (sync)
            {
                _ = RequireHandle();
                return capacity;
            }
        }
    }

    internal static uint GetAbiVersion() => NativeMethods.GetAbiVersion();

    internal NativeDisplayCoordinatorStatus BeginRefresh(in NativeDisplayRefreshInput input)
    {
        lock (sync)
        {
            return (NativeDisplayCoordinatorStatus)NativeMethods.BeginRefresh(
                RequireHandle(),
                in input);
        }
    }

    internal unsafe NativeDisplayCoordinatorStatus SubmitSource(
        in NativeDisplaySourceBatchHeader header,
        ReadOnlySpan<NativeDisplayFact> facts,
        ReadOnlySpan<NativeDisplayTextInput> texts,
        ReadOnlySpan<byte> bytes)
    {
        lock (sync)
        {
            fixed (NativeDisplayFact* factPointer = facts)
            fixed (NativeDisplayTextInput* textPointer = texts)
            fixed (byte* bytePointer = bytes)
            {
                return (NativeDisplayCoordinatorStatus)NativeMethods.SubmitSource(
                    RequireHandle(),
                    in header,
                    factPointer,
                    checked((uint)facts.Length),
                    textPointer,
                    checked((uint)texts.Length),
                    bytePointer,
                    checked((uint)bytes.Length));
            }
        }
    }

    internal NativeDisplayCoordinatorStatus FinalizeRefresh(in NativeDisplayFinalizeInput input)
    {
        lock (sync)
        {
            return (NativeDisplayCoordinatorStatus)NativeMethods.Finalize(
                RequireHandle(),
                in input);
        }
    }

    internal NativeDisplayCoordinatorStatus AbortRefresh(in NativeDisplayAbortInput input)
    {
        lock (sync)
        {
            return (NativeDisplayCoordinatorStatus)NativeMethods.AbortRefresh(
                RequireHandle(),
                in input);
        }
    }

    internal NativeDisplayCoordinatorStatus Snapshot(out NativeDisplaySnapshotOutput output)
    {
        lock (sync)
        {
            output = default;
            return (NativeDisplayCoordinatorStatus)NativeMethods.Snapshot(
                RequireHandle(),
                ref output);
        }
    }

    internal unsafe NativeDisplayCoordinatorStatus ReadNodes(
        in NativeDisplayReadInput input,
        Span<NativeDisplayNodeOutput> output)
    {
        lock (sync)
        {
            output.Clear();
            fixed (NativeDisplayNodeOutput* pointer = output)
            {
                return (NativeDisplayCoordinatorStatus)NativeMethods.ReadNodes(
                    RequireHandle(),
                    in input,
                    pointer,
                    checked((uint)output.Length));
            }
        }
    }

    internal unsafe NativeDisplayCoordinatorStatus ReadEdges(
        in NativeDisplayReadInput input,
        Span<NativeDisplayEdgeOutput> output)
    {
        lock (sync)
        {
            output.Clear();
            fixed (NativeDisplayEdgeOutput* pointer = output)
            {
                return (NativeDisplayCoordinatorStatus)NativeMethods.ReadEdges(
                    RequireHandle(),
                    in input,
                    pointer,
                    checked((uint)output.Length));
            }
        }
    }

    internal unsafe NativeDisplayCoordinatorStatus ReadCapabilities(
        in NativeDisplayReadInput input,
        Span<NativeDisplayCapabilityOutput> output)
    {
        lock (sync)
        {
            output.Clear();
            fixed (NativeDisplayCapabilityOutput* pointer = output)
            {
                return (NativeDisplayCoordinatorStatus)NativeMethods.ReadCapabilities(
                    RequireHandle(),
                    in input,
                    pointer,
                    checked((uint)output.Length));
            }
        }
    }

    internal unsafe NativeDisplayCoordinatorStatus ReadTexts(
        in NativeDisplayReadInput input,
        Span<NativeDisplayTextOutput> texts,
        Span<byte> bytes)
    {
        lock (sync)
        {
            texts.Clear();
            bytes.Clear();
            fixed (NativeDisplayTextOutput* textPointer = texts)
            fixed (byte* bytePointer = bytes)
            {
                return (NativeDisplayCoordinatorStatus)NativeMethods.ReadTexts(
                    RequireHandle(),
                    in input,
                    textPointer,
                    checked((uint)texts.Length),
                    bytePointer,
                    checked((uint)bytes.Length));
            }
        }
    }

    internal unsafe NativeDisplayCoordinatorStatus ReadDiff(
        in NativeDisplayReadInput input,
        Span<NativeDisplayDiffEntry> output)
    {
        lock (sync)
        {
            output.Clear();
            fixed (NativeDisplayDiffEntry* pointer = output)
            {
                return (NativeDisplayCoordinatorStatus)NativeMethods.ReadDiff(
                    RequireHandle(),
                    in input,
                    pointer,
                    checked((uint)output.Length));
            }
        }
    }

    internal unsafe NativeDisplayCoordinatorStatus ReadUnresolved(
        in NativeDisplayReadInput input,
        Span<NativeDisplayUnresolvedOutput> output)
    {
        lock (sync)
        {
            output.Clear();
            fixed (NativeDisplayUnresolvedOutput* pointer = output)
            {
                return (NativeDisplayCoordinatorStatus)NativeMethods.ReadUnresolved(
                    RequireHandle(),
                    in input,
                    pointer,
                    checked((uint)output.Length));
            }
        }
    }

    internal unsafe NativeDisplayCoordinatorStatus ExportPersistence(
        in NativeDisplayPersistenceInput input,
        out NativeDisplayPersistenceHeader header,
        Span<NativeDisplayFact> facts,
        Span<NativeDisplayTextInput> texts,
        Span<byte> bytes)
    {
        lock (sync)
        {
            header = default;
            facts.Clear();
            texts.Clear();
            bytes.Clear();
            fixed (NativeDisplayFact* factPointer = facts)
            fixed (NativeDisplayTextInput* textPointer = texts)
            fixed (byte* bytePointer = bytes)
            {
                return (NativeDisplayCoordinatorStatus)NativeMethods.ExportPersistence(
                    RequireHandle(),
                    in input,
                    ref header,
                    factPointer,
                    checked((uint)facts.Length),
                    textPointer,
                    checked((uint)texts.Length),
                    bytePointer,
                    checked((uint)bytes.Length));
            }
        }
    }

    internal unsafe NativeDisplayCoordinatorStatus ImportPersistence(
        in NativeDisplayPersistenceInput input,
        in NativeDisplayPersistenceHeader header,
        ReadOnlySpan<NativeDisplayFact> facts,
        ReadOnlySpan<NativeDisplayTextInput> texts,
        ReadOnlySpan<byte> bytes)
    {
        lock (sync)
        {
            fixed (NativeDisplayFact* factPointer = facts)
            fixed (NativeDisplayTextInput* textPointer = texts)
            fixed (byte* bytePointer = bytes)
            {
                return (NativeDisplayCoordinatorStatus)NativeMethods.ImportPersistence(
                    RequireHandle(),
                    in input,
                    in header,
                    factPointer,
                    checked((uint)facts.Length),
                    textPointer,
                    checked((uint)texts.Length),
                    bytePointer,
                    checked((uint)bytes.Length));
            }
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    private void Dispose(bool disposing)
    {
        _ = disposing;
        lock (sync)
        {
            var current = Interlocked.Exchange(ref handle, IntPtr.Zero);
            if (current != IntPtr.Zero)
            {
                NativeMethods.Destroy(current);
            }
        }
    }

    private IntPtr RequireHandle()
    {
        var current = Volatile.Read(ref handle);
        return current != IntPtr.Zero
            ? current
            : throw new ObjectDisposedException(nameof(NativeDisplayCoordinatorSession));
    }

    private static NativeDisplayCoordinatorCapacity QueryAndValidateCapacity(
        IntPtr session,
        in NativeDisplayCoordinatorConfiguration configuration)
    {
        var status = (NativeDisplayCoordinatorStatus)NativeMethods.QueryCapacity(
            session,
            out var result,
            SizeOf<NativeDisplayCoordinatorCapacity>());
        if (status != NativeDisplayCoordinatorStatus.Ok)
        {
            throw new InvalidOperationException(
                $"Native display-coordinator capacity query failed with {status}.");
        }

        unsafe
        {
            if (result.StructSize != SizeOf<NativeDisplayCoordinatorCapacity>()
                || result.MaximumSourceCount != configuration.MaximumSourceCount
                || result.MaximumObservationCount != configuration.MaximumObservationCount
                || result.MaximumNodeCount != configuration.MaximumNodeCount
                || result.MaximumEdgeCount != configuration.MaximumEdgeCount
                || result.MaximumCapabilityCount != configuration.MaximumCapabilityCount
                || result.MaximumDiffEntryCount != configuration.MaximumDiffEntryCount
                || result.MaximumTextBindingCount != configuration.MaximumTextBindingCount
                || result.MaximumTextByteCount != configuration.MaximumTextByteCount
                || result.MaximumUnresolvedCount != configuration.MaximumUnresolvedCount
                || result.MaximumSourceBatchCount != configuration.MaximumSourceBatchCount
                || result.IdentityIndexCapacity != configuration.IdentityIndexCapacity
                || result.TextIndexCapacity != configuration.TextIndexCapacity
                || result.ReservedU32 != 0
                || result.ResidentByteCount == 0
                || result.ResidentByteCount > configuration.ResidentByteBudget
                || result.Reserved[0] != 0
                || result.Reserved[1] != 0
                || result.Reserved[2] != 0
                || result.Reserved[3] != 0
                || result.Reserved[4] != 0
                || result.Reserved[5] != 0
                || result.Reserved[6] != 0
                || result.Reserved[7] != 0)
            {
                throw new InvalidOperationException(
                    "Native display-coordinator capacity contract mismatch.");
            }
        }

        return result;
    }

    private static uint SizeOf<T>() where T : unmanaged
        => checked((uint)Unsafe.SizeOf<T>());

    private static partial class NativeMethods
    {
        private const string LibraryName = "ResourceManager.NativeCore";

        [LibraryImport(LibraryName, EntryPoint = "rm_display_coordinator_abi_version")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial uint GetAbiVersion();

        [LibraryImport(LibraryName, EntryPoint = "rm_display_coordinator_create")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int Create(
            in NativeDisplayCoordinatorConfiguration configuration,
            out IntPtr handle);

        [LibraryImport(LibraryName, EntryPoint = "rm_display_coordinator_destroy")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial void Destroy(IntPtr handle);

        [LibraryImport(LibraryName, EntryPoint = "rm_display_coordinator_query_capacity")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int QueryCapacity(
            IntPtr handle,
            out NativeDisplayCoordinatorCapacity output,
            uint outputSize);

        [LibraryImport(LibraryName, EntryPoint = "rm_display_coordinator_begin_refresh")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int BeginRefresh(
            IntPtr handle,
            in NativeDisplayRefreshInput input);

        [LibraryImport(LibraryName, EntryPoint = "rm_display_coordinator_submit_source")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial int SubmitSource(
            IntPtr handle,
            in NativeDisplaySourceBatchHeader header,
            NativeDisplayFact* factPointer,
            uint factCount,
            NativeDisplayTextInput* textPointer,
            uint textCount,
            byte* bytePointer,
            uint byteCount);

        [LibraryImport(LibraryName, EntryPoint = "rm_display_coordinator_finalize")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int Finalize(
            IntPtr handle,
            in NativeDisplayFinalizeInput input);

        [LibraryImport(LibraryName, EntryPoint = "rm_display_coordinator_abort_refresh")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int AbortRefresh(
            IntPtr handle,
            in NativeDisplayAbortInput input);

        [LibraryImport(LibraryName, EntryPoint = "rm_display_coordinator_snapshot")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int Snapshot(
            IntPtr handle,
            ref NativeDisplaySnapshotOutput output);

        [LibraryImport(LibraryName, EntryPoint = "rm_display_coordinator_read_nodes")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial int ReadNodes(
            IntPtr handle,
            in NativeDisplayReadInput input,
            NativeDisplayNodeOutput* pointer,
            uint count);

        [LibraryImport(LibraryName, EntryPoint = "rm_display_coordinator_read_edges")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial int ReadEdges(
            IntPtr handle,
            in NativeDisplayReadInput input,
            NativeDisplayEdgeOutput* pointer,
            uint count);

        [LibraryImport(LibraryName, EntryPoint = "rm_display_coordinator_read_capabilities")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial int ReadCapabilities(
            IntPtr handle,
            in NativeDisplayReadInput input,
            NativeDisplayCapabilityOutput* pointer,
            uint count);

        [LibraryImport(LibraryName, EntryPoint = "rm_display_coordinator_read_texts")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial int ReadTexts(
            IntPtr handle,
            in NativeDisplayReadInput input,
            NativeDisplayTextOutput* textPointer,
            uint textCount,
            byte* bytePointer,
            uint byteCount);

        [LibraryImport(LibraryName, EntryPoint = "rm_display_coordinator_read_diff")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial int ReadDiff(
            IntPtr handle,
            in NativeDisplayReadInput input,
            NativeDisplayDiffEntry* pointer,
            uint count);

        [LibraryImport(LibraryName, EntryPoint = "rm_display_coordinator_read_unresolved")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial int ReadUnresolved(
            IntPtr handle,
            in NativeDisplayReadInput input,
            NativeDisplayUnresolvedOutput* pointer,
            uint count);

        [LibraryImport(LibraryName, EntryPoint = "rm_display_coordinator_export_persistence")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial int ExportPersistence(
            IntPtr handle,
            in NativeDisplayPersistenceInput input,
            ref NativeDisplayPersistenceHeader header,
            NativeDisplayFact* factPointer,
            uint factCount,
            NativeDisplayTextInput* textPointer,
            uint textCount,
            byte* bytePointer,
            uint byteCount);

        [LibraryImport(LibraryName, EntryPoint = "rm_display_coordinator_import_persistence")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial int ImportPersistence(
            IntPtr handle,
            in NativeDisplayPersistenceInput input,
            in NativeDisplayPersistenceHeader header,
            NativeDisplayFact* factPointer,
            uint factCount,
            NativeDisplayTextInput* textPointer,
            uint textCount,
            byte* bytePointer,
            uint byteCount);
    }
}
