using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ResourceManager.App.Infrastructure.NativeCore;

internal sealed partial class NativePublicServiceCoordinatorSession : IDisposable
{
    private readonly object sync = new();
    private IntPtr handle;
    private NativePublicServiceCoordinatorCapacity capacity;

    internal NativePublicServiceCoordinatorSession(
        in NativePublicServiceCoordinatorConfiguration configuration)
    {
        var actualVersion = NativeMethods.GetAbiVersion();
        if (actualVersion != NativePublicServiceCoordinatorAbi.Version)
        {
            throw new InvalidOperationException(
                $"Native public-service coordinator ABI mismatch: expected 0x{NativePublicServiceCoordinatorAbi.Version:X8}, actual 0x{actualVersion:X8}.");
        }

        var status = (NativePublicServiceCoordinatorStatus)NativeMethods.Create(
            in configuration,
            out handle);
        if (status != NativePublicServiceCoordinatorStatus.Ok || handle == IntPtr.Zero)
        {
            handle = IntPtr.Zero;
            throw new InvalidOperationException(
                $"Native public-service coordinator creation failed with {status}.");
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

    ~NativePublicServiceCoordinatorSession() => Dispose(false);

    internal NativePublicServiceCoordinatorCapacity Capacity
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

    internal unsafe NativePublicServiceCoordinatorStatus ReplaceCatalog(
        in NativePublicServiceCatalogReplaceInput input,
        ReadOnlySpan<NativePublicServiceCapabilityInput> capabilities,
        ReadOnlySpan<NativePublicServiceRouteInput> routes,
        ReadOnlySpan<byte> text)
    {
        lock (sync)
        {
            fixed (NativePublicServiceCapabilityInput* capabilityPointer = capabilities)
            fixed (NativePublicServiceRouteInput* routePointer = routes)
            fixed (byte* textPointer = text)
            {
                return (NativePublicServiceCoordinatorStatus)NativeMethods.ReplaceCatalog(
                    RequireHandle(),
                    in input,
                    capabilityPointer,
                    checked((uint)capabilities.Length),
                    routePointer,
                    checked((uint)routes.Length),
                    textPointer,
                    checked((uint)text.Length));
            }
        }
    }

    internal unsafe NativePublicServiceCoordinatorStatus ReplaceModels(
        in NativePublicServiceModelReplaceInput input,
        ReadOnlySpan<NativePublicServiceModelInput> models,
        ReadOnlySpan<NativePublicServiceModelAliasInput> aliases,
        ReadOnlySpan<byte> text)
    {
        lock (sync)
        {
            fixed (NativePublicServiceModelInput* modelPointer = models)
            fixed (NativePublicServiceModelAliasInput* aliasPointer = aliases)
            fixed (byte* textPointer = text)
            {
                return (NativePublicServiceCoordinatorStatus)NativeMethods.ReplaceModels(
                    RequireHandle(),
                    in input,
                    modelPointer,
                    checked((uint)models.Length),
                    aliasPointer,
                    checked((uint)aliases.Length),
                    textPointer,
                    checked((uint)text.Length));
            }
        }
    }

    internal NativePublicServiceCoordinatorStatus PlanModelAcquisition(
        in NativePublicServiceModelAcquisitionPlanInput input,
        out NativePublicServiceModelAcquisitionPlanOutput output)
    {
        lock (sync)
        {
            output = default;
            return (NativePublicServiceCoordinatorStatus)NativeMethods.PlanModelAcquisition(
                RequireHandle(),
                in input,
                ref output);
        }
    }

    internal NativePublicServiceCoordinatorStatus CompleteModelAcquisition(
        in NativePublicServiceModelAcquisitionCompletionInput input)
    {
        lock (sync)
        {
            return (NativePublicServiceCoordinatorStatus)NativeMethods.CompleteModelAcquisition(
                RequireHandle(),
                in input);
        }
    }

    internal unsafe NativePublicServiceCoordinatorStatus AdmitRequest(
        in NativePublicServiceAccessInput input,
        ReadOnlySpan<byte> path,
        out NativePublicServiceAccessOutput output)
    {
        lock (sync)
        {
            output = default;
            fixed (byte* pathPointer = path)
            {
                return (NativePublicServiceCoordinatorStatus)NativeMethods.AdmitRequest(
                    RequireHandle(),
                    in input,
                    pathPointer,
                    checked((uint)path.Length),
                    ref output);
            }
        }
    }

    internal NativePublicServiceCoordinatorStatus CompleteRequest(
        in NativePublicServiceRequestCompleteInput input,
        out NativePublicServiceRequestCompleteOutput output)
    {
        lock (sync)
        {
            output = default;
            return (NativePublicServiceCoordinatorStatus)NativeMethods.CompleteRequest(
                RequireHandle(),
                in input,
                ref output);
        }
    }

    internal unsafe NativePublicServiceCoordinatorStatus ResolveModel(
        in NativePublicServiceModelResolveInput input,
        ReadOnlySpan<byte> alias,
        out NativePublicServiceModelResolveOutput output)
    {
        lock (sync)
        {
            output = default;
            fixed (byte* aliasPointer = alias)
            {
                return (NativePublicServiceCoordinatorStatus)NativeMethods.ResolveModel(
                    RequireHandle(),
                    in input,
                    aliasPointer,
                    checked((uint)alias.Length),
                    ref output);
            }
        }
    }

    internal NativePublicServiceCoordinatorStatus BeginLease(
        in NativePublicServiceLeaseBeginInput input,
        out NativePublicServiceLeaseOutput output)
    {
        lock (sync)
        {
            output = default;
            return (NativePublicServiceCoordinatorStatus)NativeMethods.BeginLease(
                RequireHandle(),
                in input,
                ref output);
        }
    }

    internal NativePublicServiceCoordinatorStatus EndLease(
        in NativePublicServiceLeaseEndInput input)
    {
        lock (sync)
        {
            return (NativePublicServiceCoordinatorStatus)NativeMethods.EndLease(
                RequireHandle(),
                in input);
        }
    }

    internal NativePublicServiceCoordinatorStatus UpsertSubscription(
        in NativePublicServiceSubscriptionUpsertInput input,
        out NativePublicServiceSubscriptionOutput output)
    {
        lock (sync)
        {
            output = default;
            return (NativePublicServiceCoordinatorStatus)NativeMethods.UpsertSubscription(
                RequireHandle(),
                in input,
                ref output);
        }
    }

    internal NativePublicServiceCoordinatorStatus RemoveSubscription(
        in NativePublicServiceSubscriptionRemoveInput input)
    {
        lock (sync)
        {
            return (NativePublicServiceCoordinatorStatus)NativeMethods.RemoveSubscription(
                RequireHandle(),
                in input);
        }
    }

    internal NativePublicServiceCoordinatorStatus EnqueueTask(
        in NativePublicServiceTaskEnqueueInput input,
        out NativePublicServiceTaskOutput output)
    {
        lock (sync)
        {
            output = default;
            return (NativePublicServiceCoordinatorStatus)NativeMethods.EnqueueTask(
                RequireHandle(),
                in input,
                ref output);
        }
    }

    internal unsafe NativePublicServiceCoordinatorStatus PlanTasks(
        in NativePublicServiceTaskPlanInput input,
        Span<NativePublicServiceTaskOutput> tasks,
        out NativePublicServiceTaskPlanOutput output)
    {
        lock (sync)
        {
            output = default;
            tasks.Clear();
            fixed (NativePublicServiceTaskOutput* taskPointer = tasks)
            {
                return (NativePublicServiceCoordinatorStatus)NativeMethods.PlanTasks(
                    RequireHandle(),
                    in input,
                    taskPointer,
                    checked((uint)tasks.Length),
                    ref output);
            }
        }
    }

    internal NativePublicServiceCoordinatorStatus CompleteTask(
        in NativePublicServiceTaskCompletionInput input,
        out NativePublicServiceTaskCompletionOutput output)
    {
        lock (sync)
        {
            output = default;
            return (NativePublicServiceCoordinatorStatus)NativeMethods.CompleteTask(
                RequireHandle(),
                in input,
                ref output);
        }
    }

    internal unsafe NativePublicServiceCoordinatorStatus CancelQueuedTasks(
        in NativePublicServiceTaskCancelInput input,
        Span<NativePublicServiceTaskOutput> tasks,
        out NativePublicServiceTaskCancelOutput output)
    {
        lock (sync)
        {
            output = default;
            tasks.Clear();
            fixed (NativePublicServiceTaskOutput* taskPointer = tasks)
            {
                return (NativePublicServiceCoordinatorStatus)NativeMethods.CancelQueuedTasks(
                    RequireHandle(),
                    in input,
                    taskPointer,
                    checked((uint)tasks.Length),
                    ref output);
            }
        }
    }

    internal unsafe NativePublicServiceCoordinatorStatus ReadCapabilities(
        Span<NativePublicServiceCapabilityOutput> capabilities,
        out uint writtenCount)
    {
        lock (sync)
        {
            writtenCount = 0;
            capabilities.Clear();
            fixed (NativePublicServiceCapabilityOutput* capabilityPointer = capabilities)
            {
                return (NativePublicServiceCoordinatorStatus)NativeMethods.ReadCapabilities(
                    RequireHandle(),
                    capabilityPointer,
                    checked((uint)capabilities.Length),
                    ref writtenCount);
            }
        }
    }

    internal NativePublicServiceCoordinatorStatus Snapshot(
        out NativePublicServiceCoordinatorSnapshot output)
    {
        lock (sync)
        {
            output = default;
            return (NativePublicServiceCoordinatorStatus)NativeMethods.Snapshot(
                RequireHandle(),
                ref output,
                SizeOf<NativePublicServiceCoordinatorSnapshot>());
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
            : throw new ObjectDisposedException(nameof(NativePublicServiceCoordinatorSession));
    }

    private static unsafe NativePublicServiceCoordinatorCapacity QueryAndValidateCapacity(
        IntPtr session,
        in NativePublicServiceCoordinatorConfiguration configuration)
    {
        var status = (NativePublicServiceCoordinatorStatus)NativeMethods.QueryCapacity(
            session,
            out var result,
            SizeOf<NativePublicServiceCoordinatorCapacity>());
        if (status != NativePublicServiceCoordinatorStatus.Ok)
        {
            throw new InvalidOperationException(
                $"Native public-service coordinator capacity query failed with {status}.");
        }

        if (result.StructSize != SizeOf<NativePublicServiceCoordinatorCapacity>()
            || result.CapabilityCapacity != configuration.MaximumCapabilityCount
            || result.RouteCapacity != configuration.MaximumRouteCount
            || result.ModelCapacity != configuration.MaximumModelCount
            || result.ModelAliasCapacity != configuration.MaximumModelAliasCount
            || result.RequestCapacity != configuration.MaximumRequestCount
            || result.RateBucketCapacity != configuration.MaximumRateBucketCount
            || result.LeaseCapacity != configuration.MaximumLeaseCount
            || result.SubscriptionCapacity != configuration.MaximumSubscriptionCount
            || result.TaskCapacity != configuration.MaximumTaskCount
            || result.CapabilityIndexCapacity != configuration.CapabilityIndexCapacity
            || result.ModelIndexCapacity != configuration.ModelIndexCapacity
            || result.AliasIndexCapacity != configuration.AliasIndexCapacity
            || result.RequestIndexCapacity != configuration.RequestIndexCapacity
            || result.RateBucketIndexCapacity != configuration.RateBucketIndexCapacity
            || result.LeaseIndexCapacity != configuration.LeaseIndexCapacity
            || result.SubscriptionIndexCapacity != configuration.SubscriptionIndexCapacity
            || result.TaskIndexCapacity != configuration.TaskIndexCapacity
            || result.CatalogTextCapacity != configuration.MaximumCatalogTextBytes
            || result.ModelTextCapacity != configuration.MaximumModelTextBytes
            || result.ResidentByteCount == 0
            || result.ResidentByteCount > configuration.ResidentByteBudget
            || result.Reserved[0] != 0
            || result.Reserved[1] != 0
            || result.Reserved[2] != 0)
        {
            throw new InvalidOperationException(
                "Native public-service coordinator capacity contract mismatch.");
        }

        return result;
    }

    private static uint SizeOf<T>() where T : unmanaged
        => checked((uint)Unsafe.SizeOf<T>());

    private static partial class NativeMethods
    {
        private const string LibraryName = "ResourceManager.NativeCore";

        [LibraryImport(LibraryName, EntryPoint = "rm_public_service_coordinator_abi_version")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial uint GetAbiVersion();

        [LibraryImport(LibraryName, EntryPoint = "rm_public_service_coordinator_create")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int Create(
            in NativePublicServiceCoordinatorConfiguration configuration,
            out IntPtr handle);

        [LibraryImport(LibraryName, EntryPoint = "rm_public_service_coordinator_destroy")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial void Destroy(IntPtr handle);

        [LibraryImport(LibraryName, EntryPoint = "rm_public_service_coordinator_query_capacity")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int QueryCapacity(
            IntPtr handle,
            out NativePublicServiceCoordinatorCapacity output,
            uint outputSize);

        [LibraryImport(LibraryName, EntryPoint = "rm_public_service_coordinator_replace_catalog")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial int ReplaceCatalog(
            IntPtr handle,
            in NativePublicServiceCatalogReplaceInput input,
            NativePublicServiceCapabilityInput* capabilityPointer,
            uint capabilityCount,
            NativePublicServiceRouteInput* routePointer,
            uint routeCount,
            byte* textPointer,
            uint textByteCount);

        [LibraryImport(LibraryName, EntryPoint = "rm_public_service_coordinator_replace_models")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial int ReplaceModels(
            IntPtr handle,
            in NativePublicServiceModelReplaceInput input,
            NativePublicServiceModelInput* modelPointer,
            uint modelCount,
            NativePublicServiceModelAliasInput* aliasPointer,
            uint aliasCount,
            byte* textPointer,
            uint textByteCount);

        [LibraryImport(LibraryName, EntryPoint = "rm_public_service_coordinator_plan_model_acquisition")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int PlanModelAcquisition(
            IntPtr handle,
            in NativePublicServiceModelAcquisitionPlanInput input,
            ref NativePublicServiceModelAcquisitionPlanOutput output);

        [LibraryImport(LibraryName, EntryPoint = "rm_public_service_coordinator_complete_model_acquisition")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int CompleteModelAcquisition(
            IntPtr handle,
            in NativePublicServiceModelAcquisitionCompletionInput input);

        [LibraryImport(LibraryName, EntryPoint = "rm_public_service_coordinator_admit_request")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial int AdmitRequest(
            IntPtr handle,
            in NativePublicServiceAccessInput input,
            byte* pathPointer,
            uint pathByteCount,
            ref NativePublicServiceAccessOutput output);

        [LibraryImport(LibraryName, EntryPoint = "rm_public_service_coordinator_complete_request")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int CompleteRequest(
            IntPtr handle,
            in NativePublicServiceRequestCompleteInput input,
            ref NativePublicServiceRequestCompleteOutput output);

        [LibraryImport(LibraryName, EntryPoint = "rm_public_service_coordinator_resolve_model")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial int ResolveModel(
            IntPtr handle,
            in NativePublicServiceModelResolveInput input,
            byte* aliasPointer,
            uint aliasByteCount,
            ref NativePublicServiceModelResolveOutput output);

        [LibraryImport(LibraryName, EntryPoint = "rm_public_service_coordinator_begin_lease")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int BeginLease(
            IntPtr handle,
            in NativePublicServiceLeaseBeginInput input,
            ref NativePublicServiceLeaseOutput output);

        [LibraryImport(LibraryName, EntryPoint = "rm_public_service_coordinator_end_lease")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int EndLease(
            IntPtr handle,
            in NativePublicServiceLeaseEndInput input);

        [LibraryImport(LibraryName, EntryPoint = "rm_public_service_coordinator_upsert_subscription")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int UpsertSubscription(
            IntPtr handle,
            in NativePublicServiceSubscriptionUpsertInput input,
            ref NativePublicServiceSubscriptionOutput output);

        [LibraryImport(LibraryName, EntryPoint = "rm_public_service_coordinator_remove_subscription")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int RemoveSubscription(
            IntPtr handle,
            in NativePublicServiceSubscriptionRemoveInput input);

        [LibraryImport(LibraryName, EntryPoint = "rm_public_service_coordinator_enqueue_task")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int EnqueueTask(
            IntPtr handle,
            in NativePublicServiceTaskEnqueueInput input,
            ref NativePublicServiceTaskOutput output);

        [LibraryImport(LibraryName, EntryPoint = "rm_public_service_coordinator_plan_tasks")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial int PlanTasks(
            IntPtr handle,
            in NativePublicServiceTaskPlanInput input,
            NativePublicServiceTaskOutput* taskPointer,
            uint taskCapacity,
            ref NativePublicServiceTaskPlanOutput output);

        [LibraryImport(LibraryName, EntryPoint = "rm_public_service_coordinator_complete_task")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int CompleteTask(
            IntPtr handle,
            in NativePublicServiceTaskCompletionInput input,
            ref NativePublicServiceTaskCompletionOutput output);

        [LibraryImport(
            LibraryName,
            EntryPoint = "rm_public_service_coordinator_cancel_queued_tasks")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial int CancelQueuedTasks(
            IntPtr handle,
            in NativePublicServiceTaskCancelInput input,
            NativePublicServiceTaskOutput* taskPointer,
            uint taskCapacity,
            ref NativePublicServiceTaskCancelOutput output);

        [LibraryImport(LibraryName, EntryPoint = "rm_public_service_coordinator_read_capabilities")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial int ReadCapabilities(
            IntPtr handle,
            NativePublicServiceCapabilityOutput* capabilityPointer,
            uint capabilityCapacity,
            ref uint writtenCount);

        [LibraryImport(LibraryName, EntryPoint = "rm_public_service_coordinator_snapshot")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int Snapshot(
            IntPtr handle,
            ref NativePublicServiceCoordinatorSnapshot output,
            uint outputSize);
    }
}
