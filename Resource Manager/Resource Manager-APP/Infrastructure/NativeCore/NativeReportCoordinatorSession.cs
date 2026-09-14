using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ResourceManager.App.Infrastructure.NativeCore;

internal sealed partial class NativeReportCoordinatorSession : IDisposable
{
    private readonly object sync = new();
    private IntPtr handle;
    private NativeReportCoordinatorCapacity capacity;
    private ulong configurationGeneration;

    public NativeReportCoordinatorSession(in NativeReportCoordinatorConfiguration configuration)
    {
        var actualVersion = NativeMethods.GetAbiVersion();
        if (actualVersion != NativeReportCoordinatorAbi.Version)
        {
            throw new InvalidOperationException(
                $"Native report coordinator ABI version mismatch: expected 0x{NativeReportCoordinatorAbi.Version:X8}, actual 0x{actualVersion:X8}.");
        }

        var status = (NativeReportCoordinatorStatus)NativeMethods.Create(
            in configuration,
            out handle);
        if (status != NativeReportCoordinatorStatus.Ok || handle == IntPtr.Zero)
        {
            handle = IntPtr.Zero;
            throw new InvalidOperationException(
                $"Native report coordinator session creation failed with {status}.");
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

    ~NativeReportCoordinatorSession() => Dispose(false);

    public NativeReportCoordinatorCapacity Capacity
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

    public NativeReportCoordinatorStatus Reconfigure(
        in NativeReportCoordinatorConfiguration configuration)
    {
        lock (sync)
        {
            var current = RequireHandle();
            var status = (NativeReportCoordinatorStatus)NativeMethods.Reconfigure(
                current,
                in configuration);
            if (status == NativeReportCoordinatorStatus.Ok)
            {
                capacity = QueryAndValidateCapacity(current, in configuration);
                configurationGeneration = configuration.Generation;
            }
            return status;
        }
    }

    public unsafe NativeReportCoordinatorStatus ReplaceRules(
        in NativeReportRuleReplaceInput input,
        ReadOnlySpan<NativeReportRuleInput> rules)
    {
        lock (sync)
        {
            fixed (NativeReportRuleInput* rulePointer = rules)
            {
                return (NativeReportCoordinatorStatus)NativeMethods.ReplaceRules(
                    RequireHandle(),
                    in input,
                    rulePointer,
                    checked((uint)rules.Length));
            }
        }
    }

    public unsafe NativeReportCoordinatorStatus Import(
        in NativeReportImportInput input,
        ReadOnlySpan<NativeReportPersistenceOperation> rows)
    {
        lock (sync)
        {
            fixed (NativeReportPersistenceOperation* rowPointer = rows)
            {
                return (NativeReportCoordinatorStatus)NativeMethods.Import(
                    RequireHandle(),
                    in input,
                    rowPointer,
                    checked((uint)rows.Length));
            }
        }
    }

    public unsafe NativeReportCoordinatorStatus Observe(
        in NativeReportSourceSnapshotInput input,
        ReadOnlySpan<NativeReportFactInput> facts)
    {
        lock (sync)
        {
            fixed (NativeReportFactInput* factPointer = facts)
            {
                return (NativeReportCoordinatorStatus)NativeMethods.Observe(
                    RequireHandle(),
                    in input,
                    factPointer,
                    checked((uint)facts.Length));
            }
        }
    }

    public NativeReportCoordinatorStatus CommandTrust(
        in NativeReportTrustCommandInput input)
    {
        lock (sync)
        {
            return (NativeReportCoordinatorStatus)NativeMethods.CommandTrust(
                RequireHandle(),
                in input);
        }
    }

    public unsafe NativeReportCoordinatorStatus Plan(
        in NativeReportPlanInput input,
        Span<NativeReportOutput> reports,
        Span<NativeReportPersistenceOperation> operations,
        out NativeReportPlanOutput output)
    {
        lock (sync)
        {
            output = default;
            fixed (NativeReportOutput* reportPointer = reports)
            fixed (NativeReportPersistenceOperation* operationPointer = operations)
            {
                return (NativeReportCoordinatorStatus)NativeMethods.Plan(
                    RequireHandle(),
                    in input,
                    reportPointer,
                    checked((uint)reports.Length),
                    operationPointer,
                    checked((uint)operations.Length),
                    ref output);
            }
        }
    }

    public unsafe NativeReportCoordinatorStatus ApplyFeedback(
        in NativeReportPersistenceFeedbackInput input,
        ReadOnlySpan<NativeReportPersistenceFeedback> feedback)
    {
        lock (sync)
        {
            fixed (NativeReportPersistenceFeedback* feedbackPointer = feedback)
            {
                return (NativeReportCoordinatorStatus)NativeMethods.ApplyFeedback(
                    RequireHandle(),
                    in input,
                    feedbackPointer,
                    checked((uint)feedback.Length));
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
            : throw new ObjectDisposedException(nameof(NativeReportCoordinatorSession));
    }

    private static NativeReportCoordinatorCapacity QueryAndValidateCapacity(
        IntPtr session,
        in NativeReportCoordinatorConfiguration configuration)
    {
        var status = (NativeReportCoordinatorStatus)NativeMethods.QueryCapacity(
            session,
            out var result,
            SizeOf<NativeReportCoordinatorCapacity>());
        if (status != NativeReportCoordinatorStatus.Ok)
        {
            throw new InvalidOperationException(
                $"Native report coordinator capacity query failed with {status}.");
        }

        unsafe
        {
            if (result.StructSize != SizeOf<NativeReportCoordinatorCapacity>()
                || result.SourceCapacity != configuration.MaximumSourceCount
                || result.RuleCapacity != configuration.MaximumRuleCount
                || result.ObservationCapacity != configuration.MaximumObservationCount
                || result.ReportCapacity != configuration.MaximumReportCount
                || result.TrustCapacity != configuration.MaximumTrustCount
                || result.BucketCapacity != configuration.MaximumBucketCount
                || result.PersistenceOperationCapacity != configuration.MaximumPersistenceOperationCount
                || result.ReportOutputCapacity != configuration.MaximumReportOutputCount
                || result.SourceIndexCapacity != configuration.SourceIndexCapacity
                || result.RuleIndexCapacity != configuration.RuleIndexCapacity
                || result.ObservationIndexCapacity != configuration.ObservationIndexCapacity
                || result.ReportIndexCapacity != configuration.ReportIndexCapacity
                || result.TrustIndexCapacity != configuration.TrustIndexCapacity
                || result.BucketIndexCapacity != configuration.BucketIndexCapacity
                || result.RollingObservationCapacity != configuration.MaximumRollingObservationCount
                || result.PlannedPersistenceIndexCapacity != configuration.PlannedPersistenceIndexCapacity
                || result.ReservedU32 != 0
                || result.ResidentByteCount == 0
                || result.ResidentByteCount > configuration.ResidentByteBudget
                || result.Reserved[0] != 0
                || result.Reserved[1] != 0)
            {
                throw new InvalidOperationException(
                    "Native report coordinator capacity contract mismatch.");
            }
        }

        return result;
    }

    private static uint SizeOf<T>() where T : unmanaged
        => checked((uint)Unsafe.SizeOf<T>());

    private static partial class NativeMethods
    {
        private const string LibraryName = "ResourceManager.NativeCore";

        [LibraryImport(LibraryName, EntryPoint = "rm_report_coordinator_abi_version")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial uint GetAbiVersion();

        [LibraryImport(LibraryName, EntryPoint = "rm_report_coordinator_create")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int Create(
            in NativeReportCoordinatorConfiguration configuration,
            out IntPtr handle);

        [LibraryImport(LibraryName, EntryPoint = "rm_report_coordinator_destroy")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial void Destroy(IntPtr handle);

        [LibraryImport(LibraryName, EntryPoint = "rm_report_coordinator_reconfigure")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int Reconfigure(
            IntPtr handle,
            in NativeReportCoordinatorConfiguration configuration);

        [LibraryImport(LibraryName, EntryPoint = "rm_report_coordinator_query_capacity")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int QueryCapacity(
            IntPtr handle,
            out NativeReportCoordinatorCapacity capacity,
            uint outputSize);

        [LibraryImport(LibraryName, EntryPoint = "rm_report_coordinator_replace_rules")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial int ReplaceRules(
            IntPtr handle,
            in NativeReportRuleReplaceInput input,
            NativeReportRuleInput* rules,
            uint ruleCount);

        [LibraryImport(LibraryName, EntryPoint = "rm_report_coordinator_import")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial int Import(
            IntPtr handle,
            in NativeReportImportInput input,
            NativeReportPersistenceOperation* rows,
            uint rowCount);

        [LibraryImport(LibraryName, EntryPoint = "rm_report_coordinator_observe")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial int Observe(
            IntPtr handle,
            in NativeReportSourceSnapshotInput input,
            NativeReportFactInput* facts,
            uint factCount);

        [LibraryImport(LibraryName, EntryPoint = "rm_report_coordinator_command_trust")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int CommandTrust(
            IntPtr handle,
            in NativeReportTrustCommandInput input);

        [LibraryImport(LibraryName, EntryPoint = "rm_report_coordinator_plan")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial int Plan(
            IntPtr handle,
            in NativeReportPlanInput input,
            NativeReportOutput* reports,
            uint reportCapacity,
            NativeReportPersistenceOperation* operations,
            uint operationCapacity,
            ref NativeReportPlanOutput output);

        [LibraryImport(LibraryName, EntryPoint = "rm_report_coordinator_apply_feedback")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial int ApplyFeedback(
            IntPtr handle,
            in NativeReportPersistenceFeedbackInput input,
            NativeReportPersistenceFeedback* feedback,
            uint feedbackCount);
    }
}
