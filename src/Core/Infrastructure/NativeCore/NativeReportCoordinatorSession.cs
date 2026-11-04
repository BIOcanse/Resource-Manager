using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ResourceManager.App.Infrastructure.NativeCore;

internal sealed partial class NativeReportCoordinatorSession : IDisposable
{
    private readonly object sync = new();
    private IntPtr handle;
    private NativeReportCoordinatorCapacity capacity;
    private ulong configurationGeneration;
    private NativeReportCoordinatorConfiguration configuration;
    private readonly uint observationGrowthLimit;

    public NativeReportCoordinatorSession(in NativeReportCoordinatorConfiguration configuration, uint observationGrowthLimit = 0)
    {
        if (observationGrowthLimit != 0 && observationGrowthLimit < configuration.MaximumObservationCount)
            throw new ArgumentOutOfRangeException(nameof(observationGrowthLimit));
        this.configuration = configuration;
        // An omitted grant preserves the raw ABI's fixed-limit behavior.
        this.observationGrowthLimit = Math.Max(configuration.MaximumObservationCount, observationGrowthLimit);
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
                this.configuration = configuration;
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
                try
                {
                    return (NativeReportCoordinatorStatus)NativeMethods.ReplaceRules(
                        RequireHandle(),
                        in input,
                        rulePointer,
                        checked((uint)rules.Length));
                }
                finally
                {
                    ObserveGrowth();
                }
            }
        }
    }

    public unsafe NativeReportCoordinatorStatus Import(
        in NativeReportImportInput input,
        ReadOnlySpan<NativeReportPersistenceOperation> rows)
    {
        lock (sync)
        {
            _ = RequireHandle();
            var observations = 0U;
            foreach (ref readonly var row in rows)
                if (row.OperationKind == (uint)NativeReportPersistenceKind.Observation) observations++;
            if (observations > capacity.ObservationCapacity && observations <= observationGrowthLimit)
            {
                var reserve = ReserveObservations(observations);
                if (reserve != NativeReportCoordinatorStatus.Ok) return reserve;
            }
            fixed (NativeReportPersistenceOperation* rowPointer = rows)
            {
                try
                {
                    return (NativeReportCoordinatorStatus)NativeMethods.Import(
                        RequireHandle(),
                        in input,
                        rowPointer,
                        checked((uint)rows.Length));
                }
                finally
                {
                    ObserveGrowth();
                }
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
                try
                {
                    return (NativeReportCoordinatorStatus)NativeMethods.Observe(
                        RequireHandle(),
                        in input,
                        factPointer,
                        checked((uint)facts.Length));
                }
                finally
                {
                    ObserveGrowth();
                }
            }
        }
    }

    // One failed attempt, one exact native requirement, at most one reserve and
    // retry. The retained extent is shared by sources; current fact count alone
    // cannot size it. A failed reserve preserves the old native arena.
    internal unsafe NativeReportObservationResult ObserveGrowing(
        in NativeReportSourceSnapshotInput input, ReadOnlySpan<NativeReportFactInput> facts)
    {
        lock (sync)
        {
            var status = Observe(in input, facts);
            if (status == NativeReportCoordinatorStatus.Ok) return new(status, ReportSourceFailureReason.None, 0);
            if (status == NativeReportCoordinatorStatus.BufferTooSmall
                || status == NativeReportCoordinatorStatus.AbiMismatch && facts.Length > capacity.ObservationCapacity)
            {
                fixed (NativeReportFactInput* pointer = facts)
                {
                    var query = (NativeReportCoordinatorStatus)NativeMethods.QueryObservationCount(
                        RequireHandle(), in input, pointer, checked((uint)facts.Length), out var required);
                    if (query != NativeReportCoordinatorStatus.Ok)
                        return new(query, ReportSourceFailureReason.NativeFailure, 0);
                    if (required > observationGrowthLimit)
                        return new(NativeReportCoordinatorStatus.BufferTooSmall, ReportSourceFailureReason.ObservationCapacityExceeded, required);
                    if (required > capacity.ObservationCapacity)
                    {
                        status = ReserveObservations(required);
                        if (status == NativeReportCoordinatorStatus.Ok) status = Observe(in input, facts);
                    }
                    return new(status, status == NativeReportCoordinatorStatus.Ok ? ReportSourceFailureReason.None : ReportSourceFailureReason.NativeFailure, required);
                }
            }
            return new(status, ReportSourceFailureReason.NativeFailure, 0);
        }
    }

    private NativeReportCoordinatorStatus ReserveObservations(uint required)
    {
        try
        {
            var status = (NativeReportCoordinatorStatus)NativeMethods.ReserveObservations(RequireHandle(), required);
            if (status == NativeReportCoordinatorStatus.Ok)
            {
                configuration.MaximumObservationCount = required;
                configuration.ObservationIndexCapacity = System.Numerics.BitOperations.RoundUpToPowerOf2(checked(required * 2));
                capacity = QueryAndValidateCapacity(RequireHandle(), in configuration);
            }
            return status;
        }
        finally { ObserveGrowth(); }
    }

    public NativeReportCoordinatorStatus CommandTrust(
        in NativeReportTrustCommandInput input)
    {
        lock (sync)
        {
            try
            {
                return (NativeReportCoordinatorStatus)NativeMethods.CommandTrust(
                    RequireHandle(),
                    in input);
            }
            finally
            {
                ObserveGrowth();
            }
        }
    }

    public NativeReportCoordinatorStatus ClearHistory(
        in NativeReportClearHistoryInput input,
        out uint removedCount)
    {
        lock (sync)
        {
            try
            {
                return (NativeReportCoordinatorStatus)NativeMethods.ClearHistory(
                    RequireHandle(), in input, out removedCount);
            }
            finally
            {
                ObserveGrowth();
            }
        }
    }

    public NativeReportCoordinatorStatus QueryPlanSize(
        in NativeReportPlanInput input,
        out uint reportCount,
        out uint persistenceCount)
    {
        lock (sync)
        {
            try
            {
                return (NativeReportCoordinatorStatus)NativeMethods.QueryPlanSize(
                    RequireHandle(), in input, out reportCount, out persistenceCount);
            }
            finally
            {
                ObserveGrowth();
            }
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
                try
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
                finally
                {
                    ObserveGrowth();
                }
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

        [LibraryImport(LibraryName, EntryPoint = "rm_report_coordinator_query_observation_count")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial int QueryObservationCount(IntPtr handle,
            in NativeReportSourceSnapshotInput input, NativeReportFactInput* facts, uint factCount, out uint required);

        [LibraryImport(LibraryName, EntryPoint = "rm_report_coordinator_reserve_observations")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int ReserveObservations(IntPtr handle, uint required);

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

        [LibraryImport(LibraryName, EntryPoint = "rm_report_coordinator_clear_history")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int ClearHistory(
            IntPtr handle,
            in NativeReportClearHistoryInput input,
            out uint removedCount);

        [LibraryImport(LibraryName, EntryPoint = "rm_report_coordinator_query_plan_size")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int QueryPlanSize(
            IntPtr handle,
            in NativeReportPlanInput input,
            out uint reportCount,
            out uint persistenceCount);

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
