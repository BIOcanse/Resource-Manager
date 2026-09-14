using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ResourceManager.App.Infrastructure.NativeCore;

internal sealed partial class NativeFileQuerySession : IDisposable
{
    private readonly object sync = new();
    private IntPtr handle;
    private NativeFileQueryCapacity capacity;
    private ulong configurationGeneration;

    internal NativeFileQuerySession(in NativeFileQueryConfiguration configuration)
    {
        var actualVersion = NativeMethods.GetAbiVersion();
        if (actualVersion != NativeFileQueryAbi.Version)
        {
            throw new InvalidOperationException(
                $"Native file-query ABI mismatch: expected 0x{NativeFileQueryAbi.Version:X8}, actual 0x{actualVersion:X8}.");
        }

        var status = (NativeFileQueryStatus)NativeMethods.Create(in configuration, out handle);
        if (status != NativeFileQueryStatus.Ok || handle == IntPtr.Zero)
        {
            handle = IntPtr.Zero;
            throw new InvalidOperationException(
                $"Native file-query session creation failed with {status}.");
        }

        try
        {
            capacity = QueryAndValidateCapacity(handle, in configuration);
            configurationGeneration = configuration.Generation;
        }
        catch
        {
            NativeMethods.Destroy(Interlocked.Exchange(ref handle, IntPtr.Zero));
            throw;
        }
    }

    ~NativeFileQuerySession() => Dispose(false);

    internal NativeFileQueryCapacity Capacity
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

    internal ulong ConfigurationGeneration
    {
        get
        {
            lock (sync)
            {
                _ = RequireHandle();
                return configurationGeneration;
            }
        }
    }

    internal static uint GetAbiVersion() => NativeMethods.GetAbiVersion();

    internal unsafe NativeFileQueryStatus Begin(
        in NativeFileQueryBeginInput input,
        ReadOnlySpan<byte> query,
        Span<NativeFileQuerySourcePlan> sources,
        Span<byte> planBytes,
        out NativeFileQueryPlanOutput output)
    {
        lock (sync)
        {
            output = default;
            sources.Clear();
            planBytes.Clear();
            fixed (byte* queryPointer = query)
            fixed (NativeFileQuerySourcePlan* sourcePointer = sources)
            fixed (byte* planPointer = planBytes)
            {
                return (NativeFileQueryStatus)NativeMethods.Begin(
                    RequireHandle(),
                    in input,
                    queryPointer,
                    checked((uint)query.Length),
                    ref output,
                    sourcePointer,
                    checked((uint)sources.Length),
                    planPointer,
                    checked((uint)planBytes.Length));
            }
        }
    }

    internal unsafe NativeFileQueryStatus SubmitCandidates(
        in NativeFileQuerySubmitInput input,
        ReadOnlySpan<NativeFileQueryCandidateInput> candidates,
        ReadOnlySpan<byte> candidateBytes,
        out NativeFileQuerySubmitOutput output)
    {
        lock (sync)
        {
            output = default;
            fixed (NativeFileQueryCandidateInput* candidatePointer = candidates)
            fixed (byte* bytePointer = candidateBytes)
            {
                return (NativeFileQueryStatus)NativeMethods.SubmitCandidates(
                    RequireHandle(),
                    in input,
                    candidatePointer,
                    checked((uint)candidates.Length),
                    bytePointer,
                    checked((uint)candidateBytes.Length),
                    ref output);
            }
        }
    }

    internal unsafe NativeFileQueryStatus Finalize(
        in NativeFileQueryFinalizeInput input,
        Span<NativeFileQueryResult> results,
        out NativeFileQueryFinalizeOutput output)
    {
        lock (sync)
        {
            output = default;
            results.Clear();
            fixed (NativeFileQueryResult* resultPointer = results)
            {
                return (NativeFileQueryStatus)NativeMethods.Finalize(
                    RequireHandle(),
                    in input,
                    resultPointer,
                    checked((uint)results.Length),
                    ref output);
            }
        }
    }

    internal NativeFileQueryStatus Reset(in NativeFileQueryResetInput input)
    {
        lock (sync)
        {
            return (NativeFileQueryStatus)NativeMethods.Reset(RequireHandle(), in input);
        }
    }

    internal NativeFileQueryStatus Snapshot(out NativeFileQuerySnapshot output)
    {
        lock (sync)
        {
            output = default;
            return (NativeFileQueryStatus)NativeMethods.Snapshot(
                RequireHandle(),
                ref output);
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
            : throw new ObjectDisposedException(nameof(NativeFileQuerySession));
    }

    private static NativeFileQueryCapacity QueryAndValidateCapacity(
        IntPtr session,
        in NativeFileQueryConfiguration configuration)
    {
        var status = (NativeFileQueryStatus)NativeMethods.QueryCapacity(
            session,
            out var result,
            SizeOf<NativeFileQueryCapacity>());
        if (status != NativeFileQueryStatus.Ok)
        {
            throw new InvalidOperationException(
                $"Native file-query capacity query failed with {status}.");
        }

        unsafe
        {
            if (result.StructSize != SizeOf<NativeFileQueryCapacity>()
                || result.QueryUtf8ByteCapacity != configuration.MaximumQueryUtf8ByteCount
                || result.QueryRuneCapacity != configuration.MaximumQueryRuneCount
                || result.PlanUtf8ByteCapacity != configuration.MaximumPlanUtf8ByteCount
                || result.SourcePlanCapacity != configuration.MaximumSourcePlanCount
                || result.CandidateCapacityPerSource != configuration.MaximumCandidateCountPerSource
                || result.SubmittedCandidateCapacity != configuration.MaximumSubmittedCandidateCount
                || result.UniqueCandidateCapacity != configuration.MaximumUniqueCandidateCount
                || result.CandidateSubmitBatchCapacity != configuration.MaximumCandidateSubmitBatchCount
                || result.CandidateSubmitUtf8ByteCapacity != configuration.MaximumCandidateSubmitUtf8ByteCount
                || result.CandidateTextArenaByteCapacity != configuration.CandidateTextArenaByteCount
                || result.FileNameUtf8ByteCapacity != configuration.MaximumFileNameUtf8ByteCount
                || result.ResultCapacity != configuration.MaximumResultCount
                || result.EntryIndexCapacity != configuration.EntryIndexCapacity
                || result.OrdinalIndexCapacity != configuration.OrdinalIndexCapacity
                || result.ReservedU32 != 0
                || result.ResidentByteCount == 0
                || result.ResidentByteCount > configuration.ResidentByteBudget
                || result.Reserved[0] != 0
                || result.Reserved[1] != 0
                || result.Reserved[2] != 0
                || result.Reserved[3] != 0)
            {
                throw new InvalidOperationException(
                    "Native file-query capacity contract mismatch.");
            }
        }

        return result;
    }

    private static uint SizeOf<T>() where T : unmanaged
        => checked((uint)Unsafe.SizeOf<T>());

    private static partial class NativeMethods
    {
        private const string LibraryName = "ResourceManager.NativeCore";

        [LibraryImport(LibraryName, EntryPoint = "rm_file_query_abi_version")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial uint GetAbiVersion();

        [LibraryImport(LibraryName, EntryPoint = "rm_file_query_create")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int Create(
            in NativeFileQueryConfiguration configuration,
            out IntPtr handle);

        [LibraryImport(LibraryName, EntryPoint = "rm_file_query_destroy")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial void Destroy(IntPtr handle);

        [LibraryImport(LibraryName, EntryPoint = "rm_file_query_reconfigure")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int Reconfigure(
            IntPtr handle,
            in NativeFileQueryConfiguration configuration);

        [LibraryImport(LibraryName, EntryPoint = "rm_file_query_query_capacity")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int QueryCapacity(
            IntPtr handle,
            out NativeFileQueryCapacity output,
            uint outputSize);

        [LibraryImport(LibraryName, EntryPoint = "rm_file_query_begin")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial int Begin(
            IntPtr handle,
            in NativeFileQueryBeginInput input,
            byte* queryPointer,
            uint queryByteCount,
            ref NativeFileQueryPlanOutput output,
            NativeFileQuerySourcePlan* sourcePointer,
            uint sourceCapacity,
            byte* planPointer,
            uint planCapacity);

        [LibraryImport(LibraryName, EntryPoint = "rm_file_query_submit_candidates")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial int SubmitCandidates(
            IntPtr handle,
            in NativeFileQuerySubmitInput input,
            NativeFileQueryCandidateInput* candidatePointer,
            uint candidateCount,
            byte* bytePointer,
            uint byteCount,
            ref NativeFileQuerySubmitOutput output);

        [LibraryImport(LibraryName, EntryPoint = "rm_file_query_finalize")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial int Finalize(
            IntPtr handle,
            in NativeFileQueryFinalizeInput input,
            NativeFileQueryResult* resultPointer,
            uint resultCapacity,
            ref NativeFileQueryFinalizeOutput output);

        [LibraryImport(LibraryName, EntryPoint = "rm_file_query_reset")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int Reset(
            IntPtr handle,
            in NativeFileQueryResetInput input);

        [LibraryImport(LibraryName, EntryPoint = "rm_file_query_snapshot")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int Snapshot(
            IntPtr handle,
            ref NativeFileQuerySnapshot output);
    }
}
