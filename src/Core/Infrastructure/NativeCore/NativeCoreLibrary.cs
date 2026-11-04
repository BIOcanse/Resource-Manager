using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ResourceManager.App.Infrastructure.NativeCore;

internal static partial class NativeCoreLibrary
{
    private const string LibraryName = "ResourceManager.NativeCore";

    internal static uint GetPdhCollectorAbiVersion() => NativePdhLibrary.GetPdhCollectorAbiVersion();

    [LibraryImport(LibraryName, EntryPoint = "rm_process_policy_executor_abi_version")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint GetProcessPolicyExecutorAbiVersion();

    [LibraryImport(LibraryName, EntryPoint = "rm_adapter_instance_lease_abi_version")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint GetAdapterInstanceLeaseAbiVersion();

    [LibraryImport(LibraryName, EntryPoint = "rm_adapter_instance_lease_create")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int CreateAdapterInstanceLease(
        in NativeAdapterInstanceLeaseConfig config,
        out IntPtr handle);

    [LibraryImport(LibraryName, EntryPoint = "rm_adapter_instance_lease_destroy")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void DestroyAdapterInstanceLease(IntPtr handle);

    [LibraryImport(LibraryName, EntryPoint = "rm_adapter_instance_lease_issue")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int IssueAdapterInstanceLease(
        IntPtr handle,
        in NativeAdapterInstanceLeaseIssueInput input,
        out NativeAdapterInstanceLeaseReceipt receipt,
        uint outputSize);

    [LibraryImport(LibraryName, EntryPoint = "rm_adapter_instance_lease_renew")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int RenewAdapterInstanceLease(
        IntPtr handle,
        in NativeAdapterInstanceLeaseRenewInput input,
        out NativeAdapterInstanceLeaseReceipt receipt,
        uint outputSize);

    [LibraryImport(LibraryName, EntryPoint = "rm_adapter_instance_lease_resolve")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int ResolveAdapterInstanceLease(
        IntPtr handle,
        in NativeAdapterInstanceLeaseResolveInput input,
        out NativeAdapterInstanceLeaseReceipt receipt,
        uint outputSize);

    [LibraryImport(LibraryName, EntryPoint = "rm_adapter_instance_lease_revoke")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int RevokeAdapterInstanceLease(
        IntPtr handle,
        in NativeAdapterInstanceLeaseRevokeInput input);

    [LibraryImport(LibraryName, EntryPoint = "rm_adapter_instance_lease_expire")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int ExpireAdapterInstanceLeases(
        IntPtr handle,
        ulong nowTimestamp,
        out uint expiredCount);

    [LibraryImport(LibraryName, EntryPoint = "rm_process_policy_apply_batch")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static unsafe partial int ApplyProcessPolicyBatch(
        in NativeProcessPolicyBatchHeader header,
        NativeProcessPolicyBatchItem* items,
        uint itemCapacity,
        NativeProcessPolicyBatchItemResult* results,
        uint resultCapacity);

    [LibraryImport(LibraryName, EntryPoint = "rm_memory_cleanup_abi_version")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint GetMemoryCleanupAbiVersion();

    [LibraryImport(LibraryName, EntryPoint = "rm_memory_cleanup_create")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int CreateMemoryCleanup(
        in NativeMemoryCleanupConfig config,
        out IntPtr handle);

    [LibraryImport(LibraryName, EntryPoint = "rm_memory_cleanup_destroy")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void DestroyMemoryCleanup(IntPtr handle);

    [LibraryImport(LibraryName, EntryPoint = "rm_memory_cleanup_reconfigure")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int ReconfigureMemoryCleanup(
        IntPtr handle,
        in NativeMemoryCleanupConfig config);

    [LibraryImport(LibraryName, EntryPoint = "rm_memory_cleanup_reset")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int ResetMemoryCleanup(IntPtr handle);

    [LibraryImport(LibraryName, EntryPoint = "rm_memory_cleanup_required_output_capacity")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int GetRequiredMemoryCleanupOutputCapacity(
        IntPtr handle,
        in NativeMemoryCleanupPlanHeader header,
        uint inputCapacity,
        out uint outputCapacity);

    [LibraryImport(LibraryName, EntryPoint = "rm_memory_cleanup_plan")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static unsafe partial int PlanMemoryCleanup(
        IntPtr handle,
        ref NativeMemoryCleanupPlanHeader header,
        NativeMemoryCleanupCandidateInput* inputs,
        uint inputCapacity,
        NativeMemoryCleanupDecisionOutput* outputs,
        uint outputCapacity);

    [LibraryImport(LibraryName, EntryPoint = "rm_memory_cleanup_complete")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static unsafe partial int CompleteMemoryCleanup(
        IntPtr handle,
        NativeMemoryCleanupFeedbackInput* feedback,
        uint feedbackCount);

}

