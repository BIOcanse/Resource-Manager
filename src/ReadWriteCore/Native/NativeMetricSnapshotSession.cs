using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ResourceManager.ReadWrite.Native;

public sealed partial class NativeMetricSnapshotSession : IDisposable
{
    private readonly object sync = new();
    private IntPtr handle;
    private NativeMetricSnapshotCapacity capacity;
    private INativeMetricSnapshotScratch? scratch;
    private readonly Func<ulong, uint, INativeMetricSnapshotScratch> createScratch;
    private NativeMetricSnapshotConfiguration configuration;
    private uint catalogSourceCount, catalogRuleCount, catalogMetricCount;
    private uint scratchRuleCount, scratchMetricCount;
    // Retained even if ending use throws. Native may reference it across calls.
    private NativeMetricSnapshotScratchLease? scratchUse;
    private MemoryHandle scratchPin;

    static NativeMetricSnapshotSession() => ReadWriteNativeLibraryResolver.EnsureRegistered();

    public NativeMetricSnapshotSession(in NativeMetricSnapshotConfiguration configuration)
        : this(in configuration, NativeMetricSnapshotScratch.CreateOrdinary) { }

    internal NativeMetricSnapshotSession(in NativeMetricSnapshotConfiguration configuration,
        Func<ulong, uint, INativeMetricSnapshotScratch> createScratch)
    {
        ArgumentNullException.ThrowIfNull(createScratch);
        this.createScratch = createScratch;
        this.configuration = configuration;
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
            scratch = createScratch(capacity.ScratchByteCount, capacity.ScratchAlignment)
                ?? throw new InvalidOperationException("The metric scratch factory returned no resource.");
            if (scratch.Length < capacity.ScratchByteCount)
            {
                scratch.Dispose();
                throw new InvalidOperationException("The metric scratch resource is too short.");
            }
        }
        catch
        {
            NativeMethods.Destroy(handle);
            handle = IntPtr.Zero;
            throw;
        }
    }

    public NativeMetricSnapshotCapacity Capacity => capacity;
    internal Action<string>? ResizeLog { get; set; }

    public static uint GetAbiVersion() => NativeMethods.GetAbiVersion();

    public static ulong GetWireContractFingerprint()
        => NativeMethods.GetWireContractFingerprint();

    public static unsafe ulong CalculateCatalogFingerprint(
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

    public NativeMetricSnapshotStatus Reconfigure(
        in NativeMetricSnapshotConfiguration configuration)
    {
        lock (sync)
        {
            var status = (NativeMetricSnapshotStatus)NativeMethods.Reconfigure(
                RequireHandle(),
                in configuration);
            if (status == NativeMetricSnapshotStatus.Ok)
            {
                this.configuration = configuration;
                capacity = QueryAndValidateCapacity(RequireHandle(), in configuration);
            }

            return status;
        }
    }

    public unsafe NativeMetricSnapshotStatus SynchronizeCounterDemand(
        in NativeMetricSnapshotControlInput input, ReadOnlySpan<NativeMetricSnapshotPlanMetricInput> metrics)
    {
        lock (sync)
        {
            fixed (NativeMetricSnapshotPlanMetricInput* pointer = metrics)
                return (NativeMetricSnapshotStatus)NativeMethods.SynchronizeCounterDemand(RequireHandle(), in input, pointer, checked((uint)metrics.Length));
        }
    }

    public NativeMetricSnapshotStatus Reset(in NativeMetricSnapshotControlInput input)
    {
        lock (sync)
        {
            var status = (NativeMetricSnapshotStatus)NativeMethods.Reset(
                RequireHandle(),
                in input);
            if (status == NativeMetricSnapshotStatus.Ok)
            {
                capacity = QueryAndValidateCapacity(RequireHandle(), in configuration);
                LogCatalogResize(0, 0, 0, "catalog-reset");
            }
            return status;
        }
    }

    public unsafe NativeMetricSnapshotStatus ReplaceCatalog(
        in NativeMetricSnapshotCatalogReplaceInput input,
        ReadOnlySpan<NativeMetricSnapshotSourcePolicyInput> sources,
        ReadOnlySpan<NativeMetricSnapshotMetricDefinitionInput> rules)
    {
        lock (sync)
        {
            fixed (NativeMetricSnapshotSourcePolicyInput* sourcePointer = sources)
            fixed (NativeMetricSnapshotMetricDefinitionInput* rulePointer = rules)
            {
                var status = (NativeMetricSnapshotStatus)NativeMethods.ReplaceCatalog(
                    RequireHandle(),
                    in input,
                    sourcePointer,
                    checked((uint)sources.Length),
                    rulePointer,
                    checked((uint)rules.Length));
                if (status == NativeMetricSnapshotStatus.Ok)
                {
                    capacity = QueryAndValidateCapacity(RequireHandle(), in configuration);
                    LogCatalogResize(input.SourceCount, input.RuleCount, input.MetricCount, "catalog-replacement");
                }
                return status;
            }
        }
    }

    public unsafe NativeMetricSnapshotStatus Plan(
        in NativeMetricSnapshotPlanInput input,
        ReadOnlySpan<NativeMetricSnapshotPlanMetricInput> requestedMetrics,
        ReadOnlySpan<NativeMetricSnapshotSourceStateInput> sourceModes,
        out NativeMetricSnapshotPlanOutput output,
        Span<NativeMetricSnapshotSourcePlanOutput> sourcePlans,
        Span<NativeMetricSnapshotMetricPlanOutput> metricPlans)
    {
        lock (sync)
        {
            var current = RequireHandle();
            output = default;
            sourcePlans.Clear();
            metricPlans.Clear();
            if (scratchUse is not null) return NativeMetricSnapshotStatus.Unavailable;
            var pointer = AcquireScratch();
            var status = NativeMetricSnapshotStatus.Unavailable;
            try
            {
                fixed (NativeMetricSnapshotPlanMetricInput* requestedPointer = requestedMetrics)
                fixed (NativeMetricSnapshotSourceStateInput* modePointer = sourceModes)
                fixed (NativeMetricSnapshotSourcePlanOutput* sourcePlanPointer = sourcePlans)
                fixed (NativeMetricSnapshotMetricPlanOutput* metricPlanPointer = metricPlans)
                {
                    status = (NativeMetricSnapshotStatus)NativeMethods.Plan(current, in input,
                        requestedPointer, checked((uint)requestedMetrics.Length),
                        modePointer, checked((uint)sourceModes.Length), out output,
                        sourcePlanPointer, checked((uint)sourcePlans.Length),
                        metricPlanPointer, checked((uint)metricPlans.Length),
                        pointer, capacity.ScratchByteCount);
                    return status;
                }
            }
            finally { EndScratch(status == NativeMetricSnapshotStatus.Ok); }
        }
    }

    public NativeMetricSnapshotStatus BeginCompletion(
        in NativeMetricSnapshotCompletionHeader input)
    {
        lock (sync)
        {
            var current = RequireHandle();
            if (scratchUse is not null) return NativeMetricSnapshotStatus.Unavailable;
            var pointer = AcquireScratch();
            // If the interop call throws, retain the loan until Destroy establishes
            // that no Native reference remains. A normal failed Begin never binds.
            var status = (NativeMetricSnapshotStatus)NativeMethods.BeginCompletion(
                current, in input, pointer, capacity.ScratchByteCount);
            if (status != NativeMetricSnapshotStatus.Ok) EndScratch(false);
            return status;
        }
    }

    public unsafe NativeMetricSnapshotStatus SubmitRequested(
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

    public unsafe NativeMetricSnapshotStatus SubmitObservations(
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

    public NativeMetricSnapshotStatus SubmitCpuCounter(
        in NativeMetricSnapshotCpuCounterInput input)
    {
        lock (sync)
        {
            return (NativeMetricSnapshotStatus)NativeMethods.SubmitCpuCounter(
                RequireHandle(),
                in input);
        }
    }

    public unsafe NativeMetricSnapshotStatus SubmitGpuInventory(
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

    public NativeMetricSnapshotStatus FinalizeCompletion(
        in NativeMetricSnapshotFinalizeInput input)
    {
        lock (sync)
        {
            var status = (NativeMetricSnapshotStatus)NativeMethods.FinalizeCompletion(
                RequireHandle(), in input);
            if (status == NativeMetricSnapshotStatus.Ok) EndScratch(true);
            return status;
        }
    }

    public NativeMetricSnapshotStatus AbortCompletion(
        in NativeMetricSnapshotFinalizeInput input)
    {
        lock (sync)
        {
            var status = (NativeMetricSnapshotStatus)NativeMethods.AbortCompletion(
                RequireHandle(), in input);
            if (status == NativeMetricSnapshotStatus.Ok) EndScratch(true);
            return status;
        }
    }

    public NativeMetricSnapshotStatus QueryHeader(
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

    public unsafe NativeMetricSnapshotStatus Read(
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

    public unsafe NativeMetricSnapshotStatus ExportState(
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

    public unsafe NativeMetricSnapshotStatus ImportState(
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
            currentStorage?.Dispose();
            currentStorage = null;
            CloseRetiredCurrentStorage();
            // Native Destroy has ended every reference before we unpin/end use.
            // Failed endUse/retirement is retained and Dispose may retry it.
            EndScratch(false);
            scratch?.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    private unsafe IntPtr AcquireScratch()
    {
        EnsureScratch();
        var resource = scratch!;
        scratchUse = resource.Acquire();
        try
        {
            scratchPin = scratchUse.Pin();
            var first = (nuint)scratchPin.Pointer;
            var mask = checked((nuint)(capacity.ScratchAlignment - 1));
            var aligned = checked(first + mask) & ~mask;
            var skip = checked((ulong)(aligned - first));
            if (first == 0 || skip > scratchUse.Length
                || capacity.ScratchByteCount > scratchUse.Length - skip)
                throw new InvalidOperationException("The pinned metric scratch extent is invalid.");
            _ = checked(aligned + checked((nuint)capacity.ScratchByteCount));
            return (IntPtr)(void*)aligned;
        }
        catch { EndScratch(false); throw; }
    }

    private void EnsureScratch()
    {
        if (scratch is not null && scratch.Length >= capacity.ScratchByteCount) return;
        // The completion has ended before this path. Retire the old refillable
        // resource before creating the next high-water identity, as PDH does.
        var oldBytes = scratch?.Length ?? 0;
        scratch?.Dispose();
        scratch = null;
        var candidate = createScratch(capacity.ScratchByteCount, capacity.ScratchAlignment)
            ?? throw new InvalidOperationException("The metric scratch factory returned no resource.");
        scratch = candidate; // Retain retirement obligations even if validation fails.
        if (candidate.Length < capacity.ScratchByteCount)
            throw new InvalidOperationException("The metric scratch resource is too short.");
        LogResize(
            $"Native resize workArea=metric-completion-scratch oldRows=rules:{scratchRuleCount},metrics:{scratchMetricCount} newRows=rules:{catalogRuleCount},metrics:{catalogMetricCount} oldBytes={oldBytes} newBytes={candidate.Length} reason=catalog-high-water");
        scratchRuleCount = catalogRuleCount;
        scratchMetricCount = catalogMetricCount;
    }

    private void LogCatalogResize(uint sources, uint rules, uint metrics, string reason)
    {
        if (catalogSourceCount == sources && catalogRuleCount == rules && catalogMetricCount == metrics) return;
        foreach (var area in new[] { "metric-active-bank", "metric-staging-bank" })
            LogResize(
                $"Native resize workArea={area} oldRows=sources:{catalogSourceCount},rules:{catalogRuleCount},metrics:{catalogMetricCount} newRows=sources:{sources},rules:{rules},metrics:{metrics} reason={reason}");
        if (catalogSourceCount != sources || catalogRuleCount != rules)
            LogResize(
                $"Native resize workArea=metric-plan-authority oldRows=sources:{catalogSourceCount},rules:{catalogRuleCount} newRows=sources:{sources},rules:{rules} reason={reason}");
        catalogSourceCount = sources;
        catalogRuleCount = rules;
        catalogMetricCount = metrics;
    }

    private void LogResize(string message)
    {
        if (ResizeLog is { } report) report(message);
        else Trace.TraceInformation(message); // Explicit ordinary-library diagnostic sink.
    }

    private void EndScratch(bool publish)
    {
        if (scratchUse is null) return;
        if (publish) scratchUse.Complete();
        scratchPin.Dispose();
        scratchUse.Dispose();
        scratchUse = null;
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
                || result.ScratchByteCount == 0
                || result.ScratchAlignment != 8
                || result.ResidentByteCount > configuration.ResidentByteBudget
                || result.ScratchByteCount > configuration.ResidentByteBudget - result.ResidentByteCount
                || result.ReservedU32 != 0)
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

        [LibraryImport(LibraryName, EntryPoint = "rm_metric_snapshot_synchronize_counter_demand")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial int SynchronizeCounterDemand(IntPtr handle,
            in NativeMetricSnapshotControlInput input, NativeMetricSnapshotPlanMetricInput* metrics, uint count);

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
            NativeMetricSnapshotSourceStateInput* sourceModes,
            uint sourceModeCount,
            out NativeMetricSnapshotPlanOutput output,
            NativeMetricSnapshotSourcePlanOutput* sourcePlans,
            uint sourcePlanCapacity,
            NativeMetricSnapshotMetricPlanOutput* metricPlans,
            uint metricPlanCapacity,
            IntPtr scratchPointer,
            ulong scratchByteCount);

        [LibraryImport(LibraryName, EntryPoint = "rm_metric_snapshot_begin_completion")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int BeginCompletion(
            IntPtr handle,
            in NativeMetricSnapshotCompletionHeader input,
            IntPtr scratchPointer,
            ulong scratchByteCount);

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
