using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ResourceManager.App.Infrastructure.NativeCore;

internal sealed partial class NativeTransactionJournalSession : IDisposable
{
    private readonly object sync = new();
    private readonly NativeTransactionJournalHandle handle;
    private readonly NativeTransactionJournalCapacity capacity;

    public NativeTransactionJournalSession(in NativeTransactionJournalCreateConfiguration configuration)
    {
        EnsureAbi();
        var result = (NativeTransactionJournalStatus)NativeMethods.CreateNew(in configuration, out var nativeHandle);
        if (result != NativeTransactionJournalStatus.Ok || nativeHandle == IntPtr.Zero)
        {
            throw new InvalidOperationException($"Native transaction journal creation failed with {result}.");
        }

        handle = new NativeTransactionJournalHandle(nativeHandle);
        try
        {
            capacity = QueryAndValidateCapacity(
                handle.DangerousGetHandle(),
                configuration.RecordCapacity,
                configuration.MaximumResidentBytes);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private NativeTransactionJournalSession(
        IntPtr nativeHandle,
        uint maximumRecordCapacity,
        ulong maximumResidentBytes)
    {
        handle = new NativeTransactionJournalHandle(nativeHandle);
        try
        {
            capacity = QueryAndValidateCapacity(
                handle.DangerousGetHandle(),
                maximumRecordCapacity,
                maximumResidentBytes);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public NativeTransactionJournalCapacity Capacity
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

    public static uint GetAbiVersion() => NativeMethods.GetAbiVersion();

    public static unsafe NativeTransactionJournalStatus TryOpenExisting(
        in NativeTransactionJournalOpenConfiguration configuration,
        ReadOnlySpan<byte> image,
        out NativeTransactionJournalSession? session)
    {
        EnsureAbi();
        session = null;
        fixed (byte* imagePointer = image)
        {
            var result = (NativeTransactionJournalStatus)NativeMethods.OpenExisting(
                in configuration,
                imagePointer,
                checked((ulong)image.Length),
                out var nativeHandle);
            if (result != NativeTransactionJournalStatus.Ok)
            {
                if (nativeHandle != IntPtr.Zero)
                {
                    NativeMethods.Destroy(nativeHandle);
                    throw new InvalidOperationException("Native transaction journal returned a handle on failed open.");
                }
                return result;
            }
            if (nativeHandle == IntPtr.Zero)
            {
                throw new InvalidOperationException("Native transaction journal returned no handle on successful open.");
            }

            session = new NativeTransactionJournalSession(
                nativeHandle,
                configuration.MaximumRecordCapacity,
                configuration.MaximumResidentBytes);
            return NativeTransactionJournalStatus.Ok;
        }
    }

    public NativeTransactionJournalStatus Prepare(in NativeTransactionJournalPrepareInput input)
    {
        lock (sync)
        {
            return (NativeTransactionJournalStatus)NativeMethods.Prepare(RequireHandle(), in input);
        }
    }

    public unsafe NativeTransactionJournalStatus PrepareBatch(
        in NativeTransactionJournalPrepareBatchInput batch,
        ReadOnlySpan<NativeTransactionJournalPrepareInput> inputs)
    {
        lock (sync)
        {
            fixed (NativeTransactionJournalPrepareInput* inputPointer = inputs)
            {
                return (NativeTransactionJournalStatus)NativeMethods.PrepareBatch(
                    RequireHandle(),
                    in batch,
                    inputPointer,
                    checked((uint)inputs.Length));
            }
        }
    }

    public NativeTransactionJournalStatus Mutate(in NativeTransactionJournalMutationInput input)
    {
        lock (sync)
        {
            return (NativeTransactionJournalStatus)NativeMethods.Mutate(RequireHandle(), in input);
        }
    }

    public NativeTransactionJournalStatus StageFeedback(
        in NativeTransactionJournalStageFeedbackInput input)
    {
        lock (sync)
        {
            return (NativeTransactionJournalStatus)NativeMethods.StageFeedback(
                RequireHandle(),
                in input);
        }
    }

    public NativeTransactionJournalStatus ApplyRecoveryEvidence(
        in NativeTransactionJournalRecoveryEvidenceInput input)
    {
        lock (sync)
        {
            return (NativeTransactionJournalStatus)NativeMethods.ApplyRecoveryEvidence(
                RequireHandle(),
                in input);
        }
    }

    public NativeTransactionJournalStatus Acknowledge(in NativeTransactionJournalAckInput input)
    {
        lock (sync)
        {
            return (NativeTransactionJournalStatus)NativeMethods.Acknowledge(
                RequireHandle(),
                in input);
        }
    }

    public NativeTransactionJournalStatus Get(
        in NativeTransactionJournalIdentity identity,
        out NativeTransactionJournalRecord record)
    {
        lock (sync)
        {
            return (NativeTransactionJournalStatus)NativeMethods.Get(
                RequireHandle(),
                in identity,
                out record);
        }
    }

    public unsafe NativeTransactionJournalStatus GetSnapshot(
        ref NativeTransactionJournalSnapshotHeader header,
        Span<NativeTransactionJournalRecord> records)
    {
        lock (sync)
        {
            fixed (NativeTransactionJournalRecord* recordPointer = records)
            {
                return (NativeTransactionJournalStatus)NativeMethods.Snapshot(
                    RequireHandle(),
                    ref header,
                    recordPointer,
                    checked((uint)records.Length));
            }
        }
    }

    public unsafe NativeTransactionJournalStatus Encode(Span<byte> image, out ulong written)
    {
        lock (sync)
        {
            fixed (byte* imagePointer = image)
            {
                return (NativeTransactionJournalStatus)NativeMethods.Encode(
                    RequireHandle(),
                    imagePointer,
                    checked((ulong)image.Length),
                    out written);
            }
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            handle.Dispose();
        }
    }

    public static uint SizeOf<T>() where T : unmanaged
        => checked((uint)Unsafe.SizeOf<T>());

    private IntPtr RequireHandle()
    {
        return !handle.IsClosed && !handle.IsInvalid
            ? handle.DangerousGetHandle()
            : throw new ObjectDisposedException(nameof(NativeTransactionJournalSession));
    }

    private static void EnsureAbi()
    {
        var actualVersion = NativeMethods.GetAbiVersion();
        if (actualVersion != NativeTransactionJournalAbi.Version)
        {
            throw new InvalidOperationException(
                $"Native transaction journal ABI version mismatch: expected 0x{NativeTransactionJournalAbi.Version:X8}, actual 0x{actualVersion:X8}.");
        }
    }

    private static NativeTransactionJournalCapacity QueryAndValidateCapacity(
        IntPtr nativeHandle,
        uint maximumRecordCapacity,
        ulong maximumResidentBytes)
    {
        var result = (NativeTransactionJournalStatus)NativeMethods.QueryCapacity(
            nativeHandle,
            out var resultCapacity,
            SizeOf<NativeTransactionJournalCapacity>());
        if (result != NativeTransactionJournalStatus.Ok)
        {
            throw new InvalidOperationException(
                $"Native transaction journal capacity query failed with {result}.");
        }

        unsafe
        {
            if (resultCapacity.StructSize != SizeOf<NativeTransactionJournalCapacity>() ||
                resultCapacity.RecordCapacity == 0 ||
                resultCapacity.RecordCapacity > maximumRecordCapacity ||
                resultCapacity.MaximumImageLength < NativeTransactionJournalAbi.ImageHeaderSize ||
                resultCapacity.ResidentBytes == 0 ||
                resultCapacity.ResidentBytes > maximumResidentBytes ||
                resultCapacity.Reserved[0] != 0)
            {
                throw new InvalidOperationException("Native transaction journal capacity contract mismatch.");
            }
        }
        return resultCapacity;
    }

    private sealed class NativeTransactionJournalHandle
        : SafeHandleZeroOrMinusOneIsInvalid
    {
        public NativeTransactionJournalHandle(IntPtr nativeHandle)
            : base(ownsHandle: true)
        {
            SetHandle(nativeHandle);
        }

        protected override bool ReleaseHandle()
        {
            NativeMethods.Destroy(handle);
            return true;
        }

    }

    private static partial class NativeMethods
    {
        private const string LibraryName = "ResourceManager.NativeCore";

        [LibraryImport(LibraryName, EntryPoint = "rm_transaction_journal_abi_version")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial uint GetAbiVersion();

        [LibraryImport(LibraryName, EntryPoint = "rm_transaction_journal_create_new")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int CreateNew(
            in NativeTransactionJournalCreateConfiguration configuration,
            out IntPtr handle);

        [LibraryImport(LibraryName, EntryPoint = "rm_transaction_journal_open_existing")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial int OpenExisting(
            in NativeTransactionJournalOpenConfiguration configuration,
            byte* image,
            ulong imageLength,
            out IntPtr handle);

        [LibraryImport(LibraryName, EntryPoint = "rm_transaction_journal_destroy")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial void Destroy(IntPtr handle);

        [LibraryImport(LibraryName, EntryPoint = "rm_transaction_journal_query_capacity")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int QueryCapacity(
            IntPtr handle,
            out NativeTransactionJournalCapacity capacity,
            uint capacitySize);

        [LibraryImport(LibraryName, EntryPoint = "rm_transaction_journal_prepare")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int Prepare(
            IntPtr handle,
            in NativeTransactionJournalPrepareInput input);

        [LibraryImport(LibraryName, EntryPoint = "rm_transaction_journal_prepare_batch")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial int PrepareBatch(
            IntPtr handle,
            in NativeTransactionJournalPrepareBatchInput batch,
            NativeTransactionJournalPrepareInput* inputs,
            uint inputCount);

        [LibraryImport(LibraryName, EntryPoint = "rm_transaction_journal_mutate")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int Mutate(
            IntPtr handle,
            in NativeTransactionJournalMutationInput input);

        [LibraryImport(LibraryName, EntryPoint = "rm_transaction_journal_stage_feedback")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int StageFeedback(
            IntPtr handle,
            in NativeTransactionJournalStageFeedbackInput input);

        [LibraryImport(LibraryName, EntryPoint = "rm_transaction_journal_recovery_evidence")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int ApplyRecoveryEvidence(
            IntPtr handle,
            in NativeTransactionJournalRecoveryEvidenceInput input);

        [LibraryImport(LibraryName, EntryPoint = "rm_transaction_journal_acknowledge")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int Acknowledge(
            IntPtr handle,
            in NativeTransactionJournalAckInput input);

        [LibraryImport(LibraryName, EntryPoint = "rm_transaction_journal_get")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int Get(
            IntPtr handle,
            in NativeTransactionJournalIdentity identity,
            out NativeTransactionJournalRecord record);

        [LibraryImport(LibraryName, EntryPoint = "rm_transaction_journal_snapshot")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial int Snapshot(
            IntPtr handle,
            ref NativeTransactionJournalSnapshotHeader header,
            NativeTransactionJournalRecord* records,
            uint recordCapacity);

        [LibraryImport(LibraryName, EntryPoint = "rm_transaction_journal_encode")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial int Encode(
            IntPtr handle,
            byte* output,
            ulong outputCapacity,
            out ulong written);

    }
}
