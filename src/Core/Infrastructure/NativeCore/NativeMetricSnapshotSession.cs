using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ResourceManager.App.Infrastructure.NativeCore;

internal sealed partial class NativeMetricSnapshotSession : IDisposable
{
    private readonly object sync = new();
    private IntPtr handle;
    private NativeMetricSnapshotCapacity capacity;

    internal NativeMetricSnapshotSession(in NativeMetricSnapshotConfiguration configuration)
    {
        var actualVersion = NativeMethods.GetAbiVersion();
        if (actualVersion != NativeMetricSnapshotAbi.Version)
        {
            throw new InvalidOperationException(
                $"Native metric-snapshot ABI mismatch: expected 0x{NativeMetricSnapshotAbi.Version:X8}, actual 0x{actualVersion:X8}.");
        }

        var actualWireContract = NativeMethods.GetWireContractFingerprint();
        if (actualWireContract != NativeMetricSnapshotAbi.WireContractFingerprint)
        {
            throw new InvalidOperationException(
                $"Native metric-snapshot wire contract mismatch: expected 0x{NativeMetricSnapshotAbi.WireContractFingerprint:X16}, actual 0x{actualWireContract:X16}.");
        }

        var status = (NativeMetricSnapshotStatus)NativeMethods.Create(in configuration, out handle);
        if (status != NativeMetricSnapshotStatus.Ok || handle == IntPtr.Zero)
        {
            handle = IntPtr.Zero;
            throw new InvalidOperationException(
                $"Native metric-snapshot session creation failed with {status}.");
        }

        try
        {
            capacity = QueryAndValidateCapacity(handle, in configuration);
        }
        catch
        {
            NativeMethods.Destroy(handle);
            handle = IntPtr.Zero;
            throw;
        }
    }

    internal NativeMetricSnapshotCapacity Capacity => capacity;

    internal static uint GetAbiVersion() => NativeMethods.GetAbiVersion();

    internal static ulong GetWireContractFingerprint()
        => NativeMethods.GetWireContractFingerprint();

    internal static unsafe ulong CalculateCatalogFingerprint(
        ReadOnlySpan<NativeMetricSnapshotSourcePolicyInput> sources,
        ReadOnlySpan<NativeMetricSnapshotMetricDefinitionInput> rules)
    {
        fixed (NativeMetricSnapshotSourcePolicyInput* sourcePointer = sources)
        fixed (NativeMetricSnapshotMetricDefinitionInput* rulePointer = rules)
        {
            var status = (NativeMetricSnapshotStatus)
                NativeMethods.CalculateCatalogFingerprint(
                    sourcePointer,
                    checked((uint)sources.Length),
                    rulePointer,
                    checked((uint)rules.Length),
                    out var result);
            return status == NativeMetricSnapshotStatus.Ok && result != 0
                ? result
                : throw new InvalidOperationException(
                    $"Native metric-snapshot catalog fingerprint failed with {status}.");
        }
    }

    internal NativeMetricSnapshotStatus Reconfigure(
        in NativeMetricSnapshotConfiguration configuration)
    {
        lock (sync)
        {
            var status = (NativeMetricSnapshotStatus)NativeMethods.Reconfigure(
                RequireHandle(),
                in configuration);
            if (status == NativeMetricSnapshotStatus.Ok)
            {
                capacity = QueryAndValidateCapacity(RequireHandle(), in configuration);
            }

            return status;
        }
    }

    internal NativeMetricSnapshotStatus Reset(in NativeMetricSnapshotControlInput input)
    {
        lock (sync)
        {
            return (NativeMetricSnapshotStatus)NativeMethods.Reset(
                RequireHandle(),
                in input);
        }
    }

    internal unsafe NativeMetricSnapshotStatus ReplaceCatalog(
        in NativeMetricSnapshotCatalogReplaceInput input,
        ReadOnlySpan<NativeMetricSnapshotSourcePolicyInput> sources,
        ReadOnlySpan<NativeMetricSnapshotMetricDefinitionInput> rules)
    {
        lock (sync)
        {
            fixed (NativeMetricSnapshotSourcePolicyInput* sourcePointer = sources)
            fixed (NativeMetricSnapshotMetricDefinitionInput* rulePointer = rules)
            {
                return (NativeMetricSnapshotStatus)NativeMethods.ReplaceCatalog(
                    RequireHandle(),
                    in input,
                    sourcePointer,
                    checked((uint)sources.Length),
                    rulePointer,
                    checked((uint)rules.Length));
            }
        }
    }

    internal unsafe NativeMetricSnapshotStatus Plan(
        in NativeMetricSnapshotPlanInput input,
        ReadOnlySpan<NativeMetricSnapshotPlanMetricInput> requestedMetrics,
        ReadOnlySpan<NativeMetricSnapshotSourceModeInput> sourceModes,
        out NativeMetricSnapshotPlanOutput output,
        Span<NativeMetricSnapshotSourcePlanOutput> sourcePlans,
        Span<NativeMetricSnapshotMetricPlanOutput> metricPlans)
    {
        lock (sync)
        {
            output = default;
            sourcePlans.Clear();
            metricPlans.Clear();
            fixed (NativeMetricSnapshotPlanMetricInput* requestedPointer = requestedMetrics)
            fixed (NativeMetricSnapshotSourceModeInput* modePointer = sourceModes)
            fixed (NativeMetricSnapshotSourcePlanOutput* sourcePlanPointer = sourcePlans)
            fixed (NativeMetricSnapshotMetricPlanOutput* metricPlanPointer = metricPlans)
            {
                return (NativeMetricSnapshotStatus)NativeMethods.Plan(
                    RequireHandle(),
                    in input,
                    requestedPointer,
                    checked((uint)requestedMetrics.Length),
                    modePointer,
                    checked((uint)sourceModes.Length),
                    out output,
                    sourcePlanPointer,
                    checked((uint)sourcePlans.Length),
                    metricPlanPointer,
                    checked((uint)metricPlans.Length));
            }
        }
    }

    internal NativeMetricSnapshotStatus BeginCompletion(
        in NativeMetricSnapshotCompletionHeader input)
    {
        lock (sync)
        {
            return (NativeMetricSnapshotStatus)NativeMethods.BeginCompletion(
                RequireHandle(),
                in input);
        }
    }

    internal unsafe NativeMetricSnapshotStatus SubmitRequested(
        ReadOnlySpan<NativeMetricSnapshotRequestedMetricInput> inputs)
    {
        lock (sync)
        {
            fixed (NativeMetricSnapshotRequestedMetricInput* pointer = inputs)
            {
                return (NativeMetricSnapshotStatus)NativeMethods.SubmitRequested(
                    RequireHandle(),
                    pointer,
                    checked((uint)inputs.Length));
            }
        }
    }

    internal unsafe NativeMetricSnapshotStatus SubmitObservations(
        ReadOnlySpan<NativeMetricSnapshotObservationInput> inputs)
    {
        lock (sync)
        {
            fixed (NativeMetricSnapshotObservationInput* pointer = inputs)
            {
                return (NativeMetricSnapshotStatus)NativeMethods.SubmitObservations(
                    RequireHandle(),
                    pointer,
                    checked((uint)inputs.Length));
            }
        }
    }

    internal NativeMetricSnapshotStatus SubmitCpuCounter(
        in NativeMetricSnapshotCpuCounterInput input)
    {
        lock (sync)
        {
            return (NativeMetricSnapshotStatus)NativeMethods.SubmitCpuCounter(
                RequireHandle(),
                in input);
        }
    }

    internal unsafe NativeMetricSnapshotStatus SubmitGpuInventory(
        ReadOnlySpan<NativeMetricSnapshotGpuInventoryInput> inputs)
    {
        lock (sync)
        {
            fixed (NativeMetricSnapshotGpuInventoryInput* pointer = inputs)
            {
                return (NativeMetricSnapshotStatus)NativeMethods.SubmitGpuInventory(
                    RequireHandle(),
                    pointer,
                    checked((uint)inputs.Length));
            }
        }
    }

    internal NativeMetricSnapshotStatus FinalizeCompletion(
        in NativeMetricSnapshotFinalizeInput input)
    {
        lock (sync)
        {
            return (NativeMetricSnapshotStatus)NativeMethods.FinalizeCompletion(
                RequireHandle(),
                in input);
        }
    }

    internal NativeMetricSnapshotStatus AbortCompletion(
        in NativeMetricSnapshotFinalizeInput input)
    {
        lock (sync)
        {
            return (NativeMetricSnapshotStatus)NativeMethods.AbortCompletion(
                RequireHandle(),
                in input);
        }
    }

    internal NativeMetricSnapshotStatus QueryHeader(
        out NativeMetricSnapshotSnapshotHeader output)
    {
        lock (sync)
        {
            output = default;
            return (NativeMetricSnapshotStatus)NativeMethods.QueryHeader(
                RequireHandle(),
                out output,
                SizeOf<NativeMetricSnapshotSnapshotHeader>());
        }
    }

    internal unsafe NativeMetricSnapshotStatus Read(
        in NativeMetricSnapshotReadInput input,
        Span<NativeMetricSnapshotSourceOutput> sources,
        Span<NativeMetricSnapshotMetricOutput> metrics,
        Span<NativeMetricSnapshotGpuInventoryOutput> gpuInventory,
        Span<NativeMetricSnapshotRuleStateOutput> rules)
    {
        lock (sync)
        {
            sources.Clear();
            metrics.Clear();
            gpuInventory.Clear();
            rules.Clear();
            fixed (NativeMetricSnapshotSourceOutput* sourcePointer = sources)
            fixed (NativeMetricSnapshotMetricOutput* metricPointer = metrics)
            fixed (NativeMetricSnapshotGpuInventoryOutput* gpuPointer = gpuInventory)
            fixed (NativeMetricSnapshotRuleStateOutput* rulePointer = rules)
            {
                return (NativeMetricSnapshotStatus)NativeMethods.Read(
                    RequireHandle(),
                    in input,
                    sourcePointer,
                    checked((uint)sources.Length),
                    metricPointer,
                    checked((uint)metrics.Length),
                    gpuPointer,
                    checked((uint)gpuInventory.Length),
                    rulePointer,
                    checked((uint)rules.Length));
            }
        }
    }

    internal unsafe NativeMetricSnapshotStatus ExportState(
        in NativeMetricSnapshotReadInput input,
        out NativeMetricSnapshotPersistenceHeader header,
        Span<NativeMetricSnapshotSourcePersistenceOutput> sources,
        Span<NativeMetricSnapshotRuleStateOutput> rules,
        Span<NativeMetricSnapshotGpuInventoryOutput> gpuInventory)
    {
        lock (sync)
        {
            header = default;
            sources.Clear();
            rules.Clear();
            gpuInventory.Clear();
            fixed (NativeMetricSnapshotSourcePersistenceOutput* sourcePointer = sources)
            fixed (NativeMetricSnapshotRuleStateOutput* rulePointer = rules)
            fixed (NativeMetricSnapshotGpuInventoryOutput* gpuPointer = gpuInventory)
            {
                return (NativeMetricSnapshotStatus)NativeMethods.ExportState(
                    RequireHandle(),
                    in input,
                    out header,
                    sourcePointer,
                    checked((uint)sources.Length),
                    rulePointer,
                    checked((uint)rules.Length),
                    gpuPointer,
                    checked((uint)gpuInventory.Length));
            }
        }
    }

    internal unsafe NativeMetricSnapshotStatus ImportState(
        in NativeMetricSnapshotPersistenceInput input,
        in NativeMetricSnapshotPersistenceHeader header,
        ReadOnlySpan<NativeMetricSnapshotSourcePersistenceOutput> sources,
        ReadOnlySpan<NativeMetricSnapshotRuleStateOutput> rules,
        ReadOnlySpan<NativeMetricSnapshotGpuInventoryOutput> gpuInventory)
    {
        lock (sync)
        {
            fixed (NativeMetricSnapshotSourcePersistenceOutput* sourcePointer = sources)
            fixed (NativeMetricSnapshotRuleStateOutput* rulePointer = rules)
            fixed (NativeMetricSnapshotGpuInventoryOutput* gpuPointer = gpuInventory)
            {
                return (NativeMetricSnapshotStatus)NativeMethods.ImportState(
                    RequireHandle(),
                    in input,
                    in header,
                    sourcePointer,
                    checked((uint)sources.Length),
                    rulePointer,
                    checked((uint)rules.Length),
                    gpuPointer,
                    checked((uint)gpuInventory.Length));
            }
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            var current = Interlocked.Exchange(ref handle, IntPtr.Zero);
            if (current != IntPtr.Zero)
            {
                NativeMethods.Destroy(current);
            }
        }

        GC.SuppressFinalize(this);
    }

    private IntPtr RequireHandle()
    {
        var current = Volatile.Read(ref handle);
        return current != IntPtr.Zero
            ? current
            : throw new ObjectDisposedException(nameof(NativeMetricSnapshotSession));
    }

    private static NativeMetricSnapshotCapacity QueryAndValidateCapacity(
        IntPtr session,
        in NativeMetricSnapshotConfiguration configuration)
    {
        var status = (NativeMetricSnapshotStatus)NativeMethods.QueryCapacity(
            session,
            out var result,
            SizeOf<NativeMetricSnapshotCapacity>());
        if (status != NativeMetricSnapshotStatus.Ok)
        {
            throw new InvalidOperationException(
                $"Native metric-snapshot capacity query failed with {status}.");
        }

        unsafe
        {
            if (result.StructSize != SizeOf<NativeMetricSnapshotCapacity>()
                || result.SourceCapacity != configuration.MaximumSourceCount
                || result.MetricCapacity != configuration.MaximumMetricCount
                || result.RuleCapacity != configuration.MaximumRuleCount
                || result.RequestedCapacity != configuration.MaximumRequestedCount
                || result.ObservationCapacity != configuration.MaximumObservationCount
                || result.GpuAdapterCapacity != configuration.MaximumGpuAdapterCount
                || result.PersistenceSourceCapacity != configuration.MaximumPersistenceSourceCount
                || result.PersistenceRuleCapacity != configuration.MaximumPersistenceRuleCount
                || result.PersistenceGpuCapacity != configuration.MaximumPersistenceGpuCount
                || result.SourceIndexCapacity != configuration.SourceIndexCapacity
                || result.MetricIndexCapacity != configuration.MetricIndexCapacity
                || result.RuleIndexCapacity != configuration.RuleIndexCapacity
                || result.GpuIndexCapacity != configuration.GpuIndexCapacity
                || result.GpuLuidIndexCapacity != configuration.GpuLuidIndexCapacity
                || result.GpuKeyIndexCapacity != configuration.GpuKeyIndexCapacity
                || result.PlanMetricCapacity != configuration.MaximumPlanMetricCount
                || result.SourceModeCapacity != configuration.MaximumSourceModeCount
                || result.SourcePlanCapacity != configuration.MaximumSourcePlanCount
                || result.MetricPlanCapacity != configuration.MaximumMetricPlanCount
                || result.ResidentByteCount == 0
                || result.ResidentByteCount > configuration.ResidentByteBudget
                || result.Reserved[0] != 0
                || result.Reserved[1] != 0)
            {
                throw new InvalidOperationException(
                    "Native metric-snapshot capacity contract mismatch.");
            }
        }

        return result;
    }

    private static uint SizeOf<T>() where T : unmanaged
        => checked((uint)Unsafe.SizeOf<T>());

    private static partial class NativeMethods
    {
        private const string LibraryName = "ResourceManager.NativeCore";

        [LibraryImport(LibraryName, EntryPoint = "rm_metric_snapshot_abi_version")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial uint GetAbiVersion();

        [LibraryImport(
            LibraryName,
            EntryPoint = "rm_metric_snapshot_wire_contract_fingerprint")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial ulong GetWireContractFingerprint();

        [LibraryImport(LibraryName, EntryPoint = "rm_metric_snapshot_create")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int Create(
            in NativeMetricSnapshotConfiguration configuration,
            out IntPtr handle);

        [LibraryImport(LibraryName, EntryPoint = "rm_metric_snapshot_destroy")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial void Destroy(IntPtr handle);

        [LibraryImport(LibraryName, EntryPoint = "rm_metric_snapshot_reconfigure")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int Reconfigure(
            IntPtr handle,
            in NativeMetricSnapshotConfiguration configuration);

        [LibraryImport(LibraryName, EntryPoint = "rm_metric_snapshot_reset")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int Reset(
            IntPtr handle,
            in NativeMetricSnapshotControlInput input);

        [LibraryImport(LibraryName, EntryPoint = "rm_metric_snapshot_query_capacity")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int QueryCapacity(
            IntPtr handle,
            out NativeMetricSnapshotCapacity output,
            uint outputSize);

        [LibraryImport(LibraryName, EntryPoint = "rm_metric_snapshot_replace_catalog")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial int ReplaceCatalog(
            IntPtr handle,
            in NativeMetricSnapshotCatalogReplaceInput input,
            NativeMetricSnapshotSourcePolicyInput* sources,
            uint sourceCount,
            NativeMetricSnapshotMetricDefinitionInput* rules,
            uint ruleCount);

        [LibraryImport(
            LibraryName,
            EntryPoint = "rm_metric_snapshot_calculate_catalog_fingerprint")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial int CalculateCatalogFingerprint(
            NativeMetricSnapshotSourcePolicyInput* sources,
            uint sourceCount,
            NativeMetricSnapshotMetricDefinitionInput* rules,
            uint ruleCount,
            out ulong output);

        [LibraryImport(LibraryName, EntryPoint = "rm_metric_snapshot_plan")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial int Plan(
            IntPtr handle,
            in NativeMetricSnapshotPlanInput input,
            NativeMetricSnapshotPlanMetricInput* requestedMetrics,
            uint requestedMetricCount,
            NativeMetricSnapshotSourceModeInput* sourceModes,
            uint sourceModeCount,
            out NativeMetricSnapshotPlanOutput output,
            NativeMetricSnapshotSourcePlanOutput* sourcePlans,
            uint sourcePlanCapacity,
            NativeMetricSnapshotMetricPlanOutput* metricPlans,
            uint metricPlanCapacity);

        [LibraryImport(LibraryName, EntryPoint = "rm_metric_snapshot_begin_completion")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int BeginCompletion(
            IntPtr handle,
            in NativeMetricSnapshotCompletionHeader input);

        [LibraryImport(LibraryName, EntryPoint = "rm_metric_snapshot_submit_requested")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial int SubmitRequested(
            IntPtr handle,
            NativeMetricSnapshotRequestedMetricInput* inputs,
            uint inputCount);

        [LibraryImport(LibraryName, EntryPoint = "rm_metric_snapshot_submit_observations")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial int SubmitObservations(
            IntPtr handle,
            NativeMetricSnapshotObservationInput* inputs,
            uint inputCount);

        [LibraryImport(LibraryName, EntryPoint = "rm_metric_snapshot_submit_cpu_counter")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int SubmitCpuCounter(
            IntPtr handle,
            in NativeMetricSnapshotCpuCounterInput input);

        [LibraryImport(LibraryName, EntryPoint = "rm_metric_snapshot_submit_gpu_inventory")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial int SubmitGpuInventory(
            IntPtr handle,
            NativeMetricSnapshotGpuInventoryInput* inputs,
            uint inputCount);

        [LibraryImport(LibraryName, EntryPoint = "rm_metric_snapshot_finalize_completion")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int FinalizeCompletion(
            IntPtr handle,
            in NativeMetricSnapshotFinalizeInput input);

        [LibraryImport(LibraryName, EntryPoint = "rm_metric_snapshot_abort_completion")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int AbortCompletion(
            IntPtr handle,
            in NativeMetricSnapshotFinalizeInput input);

        [LibraryImport(LibraryName, EntryPoint = "rm_metric_snapshot_query_header")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int QueryHeader(
            IntPtr handle,
            out NativeMetricSnapshotSnapshotHeader output,
            uint outputSize);

        [LibraryImport(LibraryName, EntryPoint = "rm_metric_snapshot_read")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial int Read(
            IntPtr handle,
            in NativeMetricSnapshotReadInput input,
            NativeMetricSnapshotSourceOutput* sources,
            uint sourceCapacity,
            NativeMetricSnapshotMetricOutput* metrics,
            uint metricCapacity,
            NativeMetricSnapshotGpuInventoryOutput* gpuInventory,
            uint gpuCapacity,
            NativeMetricSnapshotRuleStateOutput* rules,
            uint ruleCapacity);

        [LibraryImport(LibraryName, EntryPoint = "rm_metric_snapshot_export_state")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial int ExportState(
            IntPtr handle,
            in NativeMetricSnapshotReadInput input,
            out NativeMetricSnapshotPersistenceHeader header,
            NativeMetricSnapshotSourcePersistenceOutput* sources,
            uint sourceCapacity,
            NativeMetricSnapshotRuleStateOutput* rules,
            uint ruleCapacity,
            NativeMetricSnapshotGpuInventoryOutput* gpuInventory,
            uint gpuCapacity);

        [LibraryImport(LibraryName, EntryPoint = "rm_metric_snapshot_import_state")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial int ImportState(
            IntPtr handle,
            in NativeMetricSnapshotPersistenceInput input,
            in NativeMetricSnapshotPersistenceHeader header,
            NativeMetricSnapshotSourcePersistenceOutput* sources,
            uint sourceCount,
            NativeMetricSnapshotRuleStateOutput* rules,
            uint ruleCount,
            NativeMetricSnapshotGpuInventoryOutput* gpuInventory,
            uint gpuCount);
    }
}
