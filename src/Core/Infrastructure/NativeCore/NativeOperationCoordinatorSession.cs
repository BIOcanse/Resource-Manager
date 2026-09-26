using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ResourceManager.App.Infrastructure.NativeCore;

internal sealed partial class NativeOperationCoordinatorSession : IDisposable
{
    private readonly object sync = new();
    private IntPtr handle;
    private NativeOperationCoordinatorCapacity capacity;

    internal NativeOperationCoordinatorSession(
        in NativeOperationCoordinatorConfiguration configuration)
    {
        var actualVersion = NativeMethods.GetAbiVersion();
        if (actualVersion != NativeOperationCoordinatorAbi.Version)
        {
            throw new InvalidOperationException(
                $"Native operation-coordinator ABI mismatch: expected 0x{NativeOperationCoordinatorAbi.Version:X8}, actual 0x{actualVersion:X8}.");
        }

        var status = (NativeOperationCoordinatorStatus)NativeMethods.Create(
            in configuration,
            out handle);
        if (status != NativeOperationCoordinatorStatus.Ok || handle == IntPtr.Zero)
        {
            handle = IntPtr.Zero;
            throw new InvalidOperationException(
                $"Native operation-coordinator session creation failed with {status}.");
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

    ~NativeOperationCoordinatorSession() => Dispose(false);

    internal NativeOperationCoordinatorCapacity Capacity
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

    internal NativeOperationCoordinatorStatus Reconfigure(
        in NativeOperationCoordinatorConfiguration configuration)
    {
        lock (sync)
        {
            var status = (NativeOperationCoordinatorStatus)NativeMethods.Reconfigure(
                RequireHandle(),
                in configuration);
            if (status == NativeOperationCoordinatorStatus.Ok)
            {
                capacity = QueryAndValidateCapacity(handle, in configuration);
            }
            return status;
        }
    }

    internal NativeOperationCoordinatorStatus Submit(
        in NativeOperationSubmitInput input,
        out NativeOperationSubmitOutput output)
    {
        lock (sync)
        {
            output = default;
            return (NativeOperationCoordinatorStatus)NativeMethods.Submit(
                RequireHandle(),
                in input,
                ref output);
        }
    }

    internal NativeOperationCoordinatorStatus Cancel(
        in NativeOperationCancelInput input,
        out NativeOperationCancelOutput output)
    {
        lock (sync)
        {
            output = default;
            return (NativeOperationCoordinatorStatus)NativeMethods.Cancel(
                RequireHandle(),
                in input,
                ref output);
        }
    }

    internal NativeOperationCoordinatorStatus Plan(
        in NativeOperationPlanInput input,
        out NativeOperationPlanOutput output)
    {
        lock (sync)
        {
            output = default;
            return (NativeOperationCoordinatorStatus)NativeMethods.Plan(
                RequireHandle(),
                in input,
                ref output);
        }
    }

    internal NativeOperationCoordinatorStatus Feedback(
        in NativeOperationActionFeedbackInput input)
    {
        lock (sync)
        {
            return (NativeOperationCoordinatorStatus)NativeMethods.Feedback(
                RequireHandle(),
                in input);
        }
    }

    internal NativeOperationCoordinatorStatus Complete(
        in NativeOperationCompletionInput input)
    {
        lock (sync)
        {
            return (NativeOperationCoordinatorStatus)NativeMethods.Complete(
                RequireHandle(),
                in input);
        }
    }

    internal NativeOperationCoordinatorStatus ReportProgress(
        in NativeOperationProgressInput input)
    {
        lock (sync)
        {
            return (NativeOperationCoordinatorStatus)NativeMethods.ReportProgress(
                RequireHandle(),
                in input);
        }
    }

    internal NativeOperationCoordinatorStatus Snapshot(
        out NativeOperationSnapshotOutput output)
    {
        lock (sync)
        {
            output = default;
            return (NativeOperationCoordinatorStatus)NativeMethods.Snapshot(
                RequireHandle(),
                ref output);
        }
    }

    internal unsafe NativeOperationCoordinatorStatus ReadOperations(
        in NativeOperationReadInput input,
        Span<NativeOperationOutput> output)
    {
        lock (sync)
        {
            output.Clear();
            fixed (NativeOperationOutput* pointer = output)
            {
                return (NativeOperationCoordinatorStatus)NativeMethods.ReadOperations(
                    RequireHandle(),
                    in input,
                    pointer,
                    checked((uint)output.Length));
            }
        }
    }

    internal unsafe NativeOperationCoordinatorStatus ReadActions(
        in NativeOperationReadInput input,
        Span<NativeOperationActionOutput> output)
    {
        lock (sync)
        {
            output.Clear();
            fixed (NativeOperationActionOutput* pointer = output)
            {
                return (NativeOperationCoordinatorStatus)NativeMethods.ReadActions(
                    RequireHandle(),
                    in input,
                    pointer,
                    checked((uint)output.Length));
            }
        }
    }

    internal unsafe NativeOperationCoordinatorStatus ExportPersistence(
        in NativeOperationPersistenceInput input,
        out NativeOperationPersistenceHeader header,
        Span<NativeOperationOutput> records)
    {
        lock (sync)
        {
            header = default;
            records.Clear();
            fixed (NativeOperationOutput* pointer = records)
            {
                return (NativeOperationCoordinatorStatus)NativeMethods.ExportPersistence(
                    RequireHandle(),
                    in input,
                    ref header,
                    pointer,
                    checked((uint)records.Length));
            }
        }
    }

    internal unsafe NativeOperationCoordinatorStatus ImportPersistence(
        in NativeOperationPersistenceImportInput input,
        in NativeOperationPersistenceHeader header,
        ReadOnlySpan<NativeOperationOutput> records)
    {
        lock (sync)
        {
            fixed (NativeOperationOutput* pointer = records)
            {
                return (NativeOperationCoordinatorStatus)NativeMethods.ImportPersistence(
                    RequireHandle(),
                    in input,
                    in header,
                    pointer,
                    checked((uint)records.Length));
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
        => handle != IntPtr.Zero
            ? handle
            : throw new ObjectDisposedException(nameof(NativeOperationCoordinatorSession));

    private static unsafe NativeOperationCoordinatorCapacity QueryAndValidateCapacity(
        IntPtr sessionHandle,
        in NativeOperationCoordinatorConfiguration configuration)
    {
        var status = (NativeOperationCoordinatorStatus)NativeMethods.QueryCapacity(
            sessionHandle,
            out var result,
            SizeOf<NativeOperationCoordinatorCapacity>());
        if (status != NativeOperationCoordinatorStatus.Ok
            || result.StructSize != SizeOf<NativeOperationCoordinatorCapacity>()
            || result.OperationCapacity != configuration.MaximumOperationCount
            || result.DomainCapacity != configuration.MaximumDomainCount
            || result.ActionCapacity != configuration.MaximumActionCount
            || result.OperationIndexCapacity != configuration.OperationIndexCapacity
            || result.DomainIndexCapacity != configuration.DomainIndexCapacity
            || result.OrderScratchCapacity != configuration.MaximumOperationCount
            || result.PersistenceCapacity != configuration.MaximumOperationCount
            || result.SessionInstanceId != configuration.SessionInstanceId
            || result.ClockInstanceId != configuration.ClockInstanceId
            || result.MaximumPersistenceByteCount
                != configuration.MaximumPersistenceByteCount
            || result.ResidentByteCount == 0
            || result.ResidentByteCount > configuration.ResidentByteBudget
            || result.Reserved[0] != 0
            || result.Reserved[1] != 0
            || result.Reserved[2] != 0
            || result.Reserved[3] != 0)
        {
            throw new InvalidOperationException(
                "Native operation-coordinator capacity contract mismatch.");
        }

        return result;
    }

    private static uint SizeOf<T>() where T : unmanaged
        => checked((uint)Unsafe.SizeOf<T>());

    private static partial class NativeMethods
    {
        private const string LibraryName = "ResourceManager.NativeCore";

        [LibraryImport(LibraryName, EntryPoint = "rm_operation_coordinator_abi_version")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial uint GetAbiVersion();

        [LibraryImport(LibraryName, EntryPoint = "rm_operation_coordinator_create")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int Create(
            in NativeOperationCoordinatorConfiguration configuration,
            out IntPtr handle);

        [LibraryImport(LibraryName, EntryPoint = "rm_operation_coordinator_destroy")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial void Destroy(IntPtr handle);

        [LibraryImport(LibraryName, EntryPoint = "rm_operation_coordinator_query_capacity")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int QueryCapacity(
            IntPtr handle,
            out NativeOperationCoordinatorCapacity output,
            uint outputSize);

        [LibraryImport(LibraryName, EntryPoint = "rm_operation_coordinator_reconfigure")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int Reconfigure(
            IntPtr handle,
            in NativeOperationCoordinatorConfiguration configuration);

        [LibraryImport(LibraryName, EntryPoint = "rm_operation_coordinator_submit")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int Submit(
            IntPtr handle,
            in NativeOperationSubmitInput input,
            ref NativeOperationSubmitOutput output);

        [LibraryImport(LibraryName, EntryPoint = "rm_operation_coordinator_cancel")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int Cancel(
            IntPtr handle,
            in NativeOperationCancelInput input,
            ref NativeOperationCancelOutput output);

        [LibraryImport(LibraryName, EntryPoint = "rm_operation_coordinator_plan")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int Plan(
            IntPtr handle,
            in NativeOperationPlanInput input,
            ref NativeOperationPlanOutput output);

        [LibraryImport(LibraryName, EntryPoint = "rm_operation_coordinator_feedback")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int Feedback(
            IntPtr handle,
            in NativeOperationActionFeedbackInput input);

        [LibraryImport(LibraryName, EntryPoint = "rm_operation_coordinator_complete")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int Complete(
            IntPtr handle,
            in NativeOperationCompletionInput input);

        [LibraryImport(LibraryName, EntryPoint = "rm_operation_coordinator_report_progress")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int ReportProgress(
            IntPtr handle,
            in NativeOperationProgressInput input);

        [LibraryImport(LibraryName, EntryPoint = "rm_operation_coordinator_snapshot")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int Snapshot(
            IntPtr handle,
            ref NativeOperationSnapshotOutput output);

        [LibraryImport(LibraryName, EntryPoint = "rm_operation_coordinator_read_operations")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial int ReadOperations(
            IntPtr handle,
            in NativeOperationReadInput input,
            NativeOperationOutput* pointer,
            uint count);

        [LibraryImport(LibraryName, EntryPoint = "rm_operation_coordinator_read_actions")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial int ReadActions(
            IntPtr handle,
            in NativeOperationReadInput input,
            NativeOperationActionOutput* pointer,
            uint count);

        [LibraryImport(LibraryName, EntryPoint = "rm_operation_coordinator_export_persistence")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial int ExportPersistence(
            IntPtr handle,
            in NativeOperationPersistenceInput input,
            ref NativeOperationPersistenceHeader header,
            NativeOperationOutput* pointer,
            uint count);

        [LibraryImport(LibraryName, EntryPoint = "rm_operation_coordinator_import_persistence")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial int ImportPersistence(
            IntPtr handle,
            in NativeOperationPersistenceImportInput input,
            in NativeOperationPersistenceHeader header,
            NativeOperationOutput* pointer,
            uint count);
    }
}
