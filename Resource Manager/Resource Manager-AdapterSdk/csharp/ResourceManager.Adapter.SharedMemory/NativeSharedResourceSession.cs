using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using ResourceManager.Adapter;

namespace ResourceManager.Adapter.SharedMemory;

public sealed unsafe class NativeSharedResourceSession : IDisposable
{
    private readonly SharedMemoryRegion _region;
    private readonly SharedMemoryMutex _mutex;
    private IntPtr _handle;

    private NativeSharedResourceSession(
        SharedMemoryRegion region,
        SharedMemoryMutex mutex,
        IntPtr handle,
        SharedResourceLedgerDescriptor descriptor,
        bool writable)
    {
        _region = region;
        _mutex = mutex;
        _handle = handle;
        Descriptor = descriptor;
        Writable = writable;
    }

    public SharedResourceLedgerDescriptor Descriptor { get; private set; }

    public bool Writable { get; }

    public bool SupportsHostPublicRecallTransactions =>
        (ReadNativeCapabilities()
            & SharedResourceProtocol.HostPublicRecallTransactionsCapability) != 0;

    public static ulong PublishedLayoutFingerprint => ManagedLayoutFingerprint();

    public static NativeSharedResourceSession Create(
        string mappingName,
        string mutexName,
        SharedResourceSessionOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mappingName);
        ArgumentException.ThrowIfNullOrWhiteSpace(mutexName);
        ArgumentNullException.ThrowIfNull(options);
        ValidateOptions(options);
        EnsureAbi();

        var ledgerInstanceId = CreateNonZeroId();
        var config = ToNativeConfig(ledgerInstanceId, options);
        ulong requiredSize = 0;
        ThrowIfFailure(
            "required-size",
            NativeSharedResourceMethods.rm_shared_ledger_required_size(&config, &requiredSize));
        if (requiredSize == 0 || requiredSize > int.MaxValue)
        {
            throw new InvalidOperationException(
                $"Native shared-resource mapping size {requiredSize} is invalid.");
        }

        var region = SharedMemoryRegion.CreateNew(mappingName, checked((long)requiredSize));
        try
        {
            var mutex = SharedMemoryMutex.CreateNew(mutexName);
            try
            {
                IntPtr handle = IntPtr.Zero;
                ThrowIfFailure(
                    "create",
                    NativeSharedResourceMethods.rm_shared_ledger_create(
                        region.Pointer,
                        requiredSize,
                        mutex.Handle.ToPointer(),
                        &config,
                        &handle));
                if (handle == IntPtr.Zero)
                {
                    throw new InvalidOperationException(
                        "Native shared-resource create returned a null handle.");
                }

                var descriptor = new SharedResourceLedgerDescriptor(
                    mappingName,
                    mutexName,
                    ledgerInstanceId,
                    options.ResourceCapacity,
                    options.SubscriptionCapacity,
                    options.TaskCapacity,
                    checked((long)requiredSize),
                    options.SubscriptionCoefficient,
                    options.OwnerApplicationKey,
                    options.OwnerProcessId,
                    options.OwnerProcessCreatedAt.UtcDateTime.Ticks,
                    SharedResourceLeaseClockDomain.WindowsPerformanceCounter,
                    checked((ulong)Stopwatch.Frequency),
                    config.MaximumSubscriptionTtl,
                    config.MaximumQueueTtl,
                    config.MaximumGrantTtl);
                return new NativeSharedResourceSession(
                    region,
                    mutex,
                    handle,
                    descriptor,
                    writable: true);
            }
            catch
            {
                mutex.Dispose();
                throw;
            }
        }
        catch
        {
            region.Dispose();
            throw;
        }
    }

    public static NativeSharedResourceSession OpenExisting(
        SharedResourceLedgerDescriptor descriptor,
        bool writable = false)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (descriptor.ProtocolVersion != SharedResourceProtocol.Version)
        {
            throw new InvalidOperationException(
                $"Shared-resource protocol {descriptor.ProtocolVersion} is unsupported.");
        }

        EnsureAbi();
        var config = ToNativeConfig(descriptor);
        var region = SharedMemoryRegion.OpenExisting(
            descriptor.MappingName,
            descriptor.MappingSizeBytes,
            writable);
        try
        {
            var mutex = SharedMemoryMutex.OpenExisting(descriptor.MutexName);
            try
            {
                IntPtr handle = IntPtr.Zero;
                ThrowIfFailure(
                    "open",
                    NativeSharedResourceMethods.rm_shared_ledger_open(
                        region.Pointer,
                        checked((ulong)descriptor.MappingSizeBytes),
                        mutex.Handle.ToPointer(),
                        &config,
                        writable ? (byte)1 : (byte)0,
                        &handle));
                if (handle == IntPtr.Zero)
                {
                    throw new InvalidOperationException(
                        "Native shared-resource open returned a null handle.");
                }

                return new NativeSharedResourceSession(
                    region,
                    mutex,
                    handle,
                    descriptor,
                    writable);
            }
            catch
            {
                mutex.Dispose();
                throw;
            }
        }
        catch
        {
            region.Dispose();
            throw;
        }
    }

    public SharedResourceId Publish(SharedResourcePublication publication)
    {
        ArgumentNullException.ThrowIfNull(publication);
        var native = ToNative(publication);
        NativeResourceRef output = default;
        ThrowIfFailure(
            "publish",
            NativeSharedResourceMethods.rm_shared_resource_publish(
                RequireHandle(),
                &native,
                &output));
        return FromNative(output);
    }

    public bool TryUpdate(SharedResourceId id, SharedResourcePublication publication)
    {
        ArgumentNullException.ThrowIfNull(publication);
        var resource = ToNative(id);
        var native = ToNative(publication);
        var result = NativeSharedResourceMethods.rm_shared_resource_update(
            RequireHandle(),
            &resource,
            &native);
        return HandleTryMutation("update", result);
    }

    public bool TryRevoke(SharedResourceId id)
    {
        var resource = ToNative(id);
        var result = NativeSharedResourceMethods.rm_shared_resource_revoke(
            RequireHandle(),
            &resource);
        return HandleTryMutation("revoke", result);
    }

    public bool TryGetResource(SharedResourceId id, out SharedResourceSnapshot? snapshot)
    {
        var resource = ToNative(id);
        NativeResourceSnapshot native = default;
        var result = NativeSharedResourceMethods.rm_shared_resource_snapshot(
            RequireHandle(),
            &resource,
            &native);
        if (result is (int)NativeSharedResourceResult.NotFound
            or (int)NativeSharedResourceResult.StaleReference)
        {
            snapshot = null;
            return false;
        }

        ThrowIfFailure("snapshot", result);
        snapshot = FromNative(native);
        return true;
    }

    public bool TryFindResource(ulong publicResourceId, out SharedResourceSnapshot? snapshot)
    {
        if (publicResourceId == 0)
        {
            snapshot = null;
            return false;
        }

        NativeResourceSnapshot native = default;
        var result = NativeSharedResourceMethods.rm_shared_resource_snapshot_by_public_id(
            RequireHandle(),
            publicResourceId,
            &native);
        if (result == (int)NativeSharedResourceResult.NotFound)
        {
            snapshot = null;
            return false;
        }

        ThrowIfFailure("snapshot-by-public-id", result);
        snapshot = FromNative(native);
        return true;
    }

    public IReadOnlyList<SharedResourceSnapshot> ListResources(out ulong topologyGeneration)
    {
        var capacity = Descriptor.ResourceCapacity;
        var native = new NativeResourceSnapshot[capacity];
        uint count = 0;
        ulong generation = 0;
        fixed (NativeResourceSnapshot* output = native)
        {
            ThrowIfFailure(
                "snapshot-batch",
                NativeSharedResourceMethods.rm_shared_resource_snapshot_batch(
                    RequireHandle(),
                    output,
                    checked((uint)capacity),
                    &count,
                    &generation));
        }

        var result = new SharedResourceSnapshot[count];
        for (var index = 0; index < count; index++)
        {
            result[index] = FromNative(native[index]);
        }

        topologyGeneration = generation;
        return result;
    }

    public SharedResourceSubscriptionReceipt Subscribe(
        SharedResourceId resourceId,
        ulong subscriberApplicationKey,
        ulong subscriberInstanceId,
        ulong subscriberSessionId,
        int subscriberProcessId,
        TimeSpan leaseDuration,
        SharedResourceSubscriptionIntent intent)
    {
        var request = new NativeSubscriptionRequest
        {
            AbiVersion = SharedResourceProtocol.Version,
            StructSize = checked((uint)Marshal.SizeOf<NativeSubscriptionRequest>()),
            Resource = ToNative(resourceId),
            SubscriberApplicationKey = subscriberApplicationKey,
            SubscriberInstanceId = subscriberInstanceId,
            SubscriberSessionId = subscriberSessionId,
            LeaseDuration = DurationToNativeTicks(
                leaseDuration,
                Descriptor.MaximumSubscriptionTtl),
            SubscriberProcessId = subscriberProcessId,
            IntentMask = (byte)intent
        };
        NativeSubscriptionReceipt output = default;
        ThrowIfFailure(
            "subscribe",
            NativeSharedResourceMethods.rm_shared_subscription_subscribe(
                RequireHandle(),
                &request,
                &output));
        return FromNative(output);
    }

    public SharedResourceSubscriptionReceipt ConfirmSubscription(
        SharedResourceSubscriptionReceipt receipt,
        TimeSpan leaseDuration,
        SharedResourceSubscriptionIntent intent)
    {
        var native = ToNative(receipt);
        NativeSubscriptionReceipt output = default;
        ThrowIfFailure(
            "confirm-subscription",
            NativeSharedResourceMethods.rm_shared_subscription_confirm(
                RequireHandle(),
                &native,
                DurationToNativeTicks(
                    leaseDuration,
                    Descriptor.MaximumSubscriptionTtl),
                (byte)intent,
                &output));
        return FromNative(output);
    }

    public bool TryReleaseSubscription(SharedResourceSubscriptionReceipt receipt)
    {
        var native = ToNative(receipt);
        var result = NativeSharedResourceMethods.rm_shared_subscription_release(
            RequireHandle(),
            &native);
        return HandleTryMutation("release-subscription", result);
    }

    public int SweepSubscriptions()
    {
        uint removed = 0;
        ThrowIfFailure(
            "sweep-subscriptions",
            NativeSharedResourceMethods.rm_shared_subscription_sweep(
                RequireHandle(),
                &removed));
        return checked((int)removed);
    }

    public SharedResourceTaskReceipt EnqueueUse(SharedResourceUseRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var native = ToNative(request, Descriptor);
        NativeTaskReceipt output = default;
        ThrowIfFailure(
            "enqueue-use",
            NativeSharedResourceMethods.rm_shared_use_enqueue(
                RequireHandle(),
                &native,
                &output));
        return FromNative(output);
    }

    public bool TryReserveNext(
        SharedResourceId resourceId,
        TimeSpan grantLeaseDuration,
        out SharedResourceGrantReceipt receipt)
    {
        var resource = ToNative(resourceId);
        NativeGrantReceipt output = default;
        var result = NativeSharedResourceMethods.rm_shared_use_reserve_next(
            RequireHandle(),
            &resource,
            DurationToNativeTicks(
                grantLeaseDuration,
                Descriptor.MaximumGrantTtl),
            &output);
        if (result == (int)NativeSharedResourceResult.QueueEmpty)
        {
            receipt = default;
            return false;
        }

        ThrowIfFailure("reserve-next", result);
        receipt = FromNative(output);
        return true;
    }

    public SharedResourceGrantReceipt BeginGrantedUse(
        SharedResourceGrantReceipt receipt)
    {
        var native = ToNative(receipt);
        NativeGrantReceipt output = default;
        ThrowIfFailure(
            "begin-use",
            NativeSharedResourceMethods.rm_shared_use_begin(
                RequireHandle(),
                &native,
                &output));
        return FromNative(output);
    }

    public SharedResourceGrantReceipt ConfirmGrantedUse(
        SharedResourceGrantReceipt receipt,
        TimeSpan leaseDuration)
    {
        var native = ToNative(receipt);
        NativeGrantReceipt output = default;
        ThrowIfFailure(
            "confirm-use",
            NativeSharedResourceMethods.rm_shared_use_confirm(
                RequireHandle(),
                &native,
                DurationToNativeTicks(
                    leaseDuration,
                    Descriptor.MaximumGrantTtl),
                &output));
        return FromNative(output);
    }

    public void CompleteGrantedUse(SharedResourceGrantReceipt receipt)
    {
        var native = ToNative(receipt);
        ThrowIfFailure(
            "complete-use",
            NativeSharedResourceMethods.rm_shared_use_complete(RequireHandle(), &native));
    }

    public bool TryCancelUse(SharedResourceTaskReceipt receipt)
    {
        var native = ToNative(receipt);
        var result = NativeSharedResourceMethods.rm_shared_use_cancel(
            RequireHandle(),
            &native);
        return HandleTryMutation("cancel-use", result);
    }

    public int SweepUses()
    {
        uint removed = 0;
        ThrowIfFailure(
            "sweep-uses",
            NativeSharedResourceMethods.rm_shared_use_sweep(
                RequireHandle(),
                &removed));
        return checked((int)removed);
    }

    public AdapterResourceActionMask FilterAllowedActions(
        SharedResourceId resourceId,
        AdapterResourceActionMask requestedActions)
    {
        var resource = ToNative(resourceId);
        byte allowed = 0;
        ThrowIfFailure(
            "filter-actions",
            NativeSharedResourceMethods.rm_shared_action_filter(
                RequireHandle(),
                &resource,
                (byte)requestedActions,
                &allowed));
        return (AdapterResourceActionMask)allowed;
    }

    public SharedResourceDestructiveToken BeginDestructiveExpected(
        SharedResourceDestructiveExpected expected)
    {
        var request = ToNative(expected);
        NativeDestructiveToken output = default;
        ThrowIfFailure(
            "begin-destructive-expected",
            NativeSharedResourceMethods.rm_shared_action_begin_destructive_expected(
                RequireHandle(),
                &request,
                &output));
        return FromNative(output);
    }

    public void CommitDestructive(
        SharedResourceDestructiveToken token,
        SharedResourcePublication postState)
    {
        var native = ToNative(token);
        var nativePostState = ToNative(postState);
        ThrowIfFailure(
            "commit-destructive",
            NativeSharedResourceMethods.rm_shared_action_commit_destructive(
                RequireHandle(),
                &native,
                &nativePostState));
    }

    public void AbortDestructive(
        SharedResourceDestructiveToken token,
        SharedResourceDestructiveNoEffectReceipt receipt)
    {
        var native = ToNative(token);
        var nativeReceipt = ToNative(receipt);
        ThrowIfFailure(
            "abort-destructive",
            NativeSharedResourceMethods.rm_shared_action_abort_destructive(
                RequireHandle(),
                &native,
                &nativeReceipt));
    }

    public SharedResourceRecallToken BeginRecallExpected(
        SharedResourceRecallExpected expected)
    {
        if (!SupportsHostPublicRecallTransactions)
        {
            throw new NotSupportedException(
                "The loaded native shared-resource runtime does not support Host public-resource recall transactions.");
        }
        var request = ToNative(expected);
        NativeRecallToken output = default;
        ThrowIfFailure(
            "begin-recall-expected",
            NativeSharedResourceMethods.rm_shared_action_begin_recall_expected(
                RequireHandle(),
                &request,
                &output));
        return FromNative(output);
    }

    public void CommitRecall(
        SharedResourceRecallToken token,
        SharedResourcePublication postState)
    {
        ArgumentNullException.ThrowIfNull(postState);
        var nativeToken = ToNative(token);
        var nativePostState = ToNative(postState);
        ThrowIfFailure(
            "commit-recall",
            NativeSharedResourceMethods.rm_shared_action_commit_recall(
                RequireHandle(),
                &nativeToken,
                &nativePostState));
    }

    public void AbortRecall(
        SharedResourceRecallToken token,
        SharedResourceDestructiveNoEffectReceipt receipt)
    {
        var nativeToken = ToNative(token);
        var nativeReceipt = ToNative(receipt);
        ThrowIfFailure(
            "abort-recall",
            NativeSharedResourceMethods.rm_shared_action_abort_recall(
                RequireHandle(),
                &nativeToken,
                &nativeReceipt));
    }

    public HostPublicResourceUnloadPlan PlanHostPublicResourceUnloads(
        ulong sampleGeneration,
        HostPublicResourceCapacityShortage shortage,
        HostPublicResourceManagerOptions? options = null)
    {
        if (sampleGeneration == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleGeneration));
        }

        options ??= new HostPublicResourceManagerOptions();
        ValidateHostPublicManagerOptions(options);
        var request = new NativeHostPublicManagerPlanRequest
        {
            AbiVersion = SharedResourceProtocol.Version,
            StructSize = checked((uint)Marshal.SizeOf<NativeHostPublicManagerPlanRequest>()),
            SampleGeneration = sampleGeneration,
            MemoryShortage = shortage.MemoryAfterNormalReleaseRounds ? (byte)1 : (byte)0,
            VideoMemoryShortage = shortage.VideoMemoryAfterNormalReleaseRounds ? (byte)1 : (byte)0,
            SubscriberWeight = options.SubscriberWeight,
            ActivityWeight = options.ActivityWeight,
            WeightScale = options.WeightScale,
            SubscriberHalfSaturation = options.SubscriberHalfSaturation,
            ActivityHalfSaturation = options.ActivityHalfSaturation
        };
        var output = new NativeHostPublicUnloadCandidate[Descriptor.ResourceCapacity];
        NativeHostPublicManagerPlanSummary summary = default;
        fixed (NativeHostPublicUnloadCandidate* nativeOutput = output)
        {
            ThrowIfFailure(
                "plan-host-public-resource-unloads",
                NativeSharedResourceMethods.rm_shared_public_manager_plan(
                    RequireHandle(),
                    &request,
                    nativeOutput,
                    checked((uint)output.Length),
                    &summary));
        }

        if (summary.AbiVersion != SharedResourceProtocol.Version
            || summary.StructSize != Marshal.SizeOf<NativeHostPublicManagerPlanSummary>()
            || summary.SampleGeneration != sampleGeneration
            || summary.ResourceCount > (uint)Descriptor.ResourceCapacity
            || summary.ProtectedResourceCount > summary.ResourceCount
            || summary.CandidateCount > (uint)output.Length
            || summary.ZeroSubscriberCandidateCount + summary.CapacityCandidateCount
                != summary.CandidateCount)
        {
            throw new InvalidOperationException(
                "Native Host public-resource self-manager returned an invalid plan summary.");
        }

        var candidates = new HostPublicResourceUnloadCandidate[summary.CandidateCount];
        for (var index = 0; index < candidates.Length; index++)
        {
            var native = output[index];
            var resource = FromNative(native.Resource);
            if (resource.LedgerInstanceId != Descriptor.LedgerInstanceId
                || resource.ResourceSlot >= (uint)Descriptor.ResourceCapacity
                || resource.ResourceGeneration == 0
                || resource.PublicResourceId == 0
                || native.SubscriberCount > int.MaxValue
                || native.Reason is < (byte)HostPublicResourceUnloadReason.ZeroSubscribers
                    or > (byte)HostPublicResourceUnloadReason.CapacityShortage
                || native.Reserved0 != 0)
            {
                throw new InvalidOperationException(
                    "Native Host public-resource self-manager returned an invalid candidate.");
            }
            candidates[index] = new HostPublicResourceUnloadCandidate(
                resource,
                native.SchedulingRevision,
                native.GateEpoch,
                native.SizeBytes,
                native.RetentionScoreQ16,
                checked((int)native.SubscriberCount),
                native.ActivityScore,
                (HostPublicResourceUnloadReason)native.Reason,
                (SharedResourceFlags)native.Flags);
        }

        return new HostPublicResourceUnloadPlan(
            summary.TopologyGeneration,
            summary.SampleGeneration,
            checked((int)summary.ResourceCount),
            checked((int)summary.ProtectedResourceCount),
            checked((int)summary.ZeroSubscriberCandidateCount),
            checked((int)summary.CapacityCandidateCount),
            candidates);
    }

    public static ulong MonotonicNow()
    {
        var timestamp = Stopwatch.GetTimestamp();
        return timestamp <= 0 ? 1UL : checked((ulong)timestamp);
    }

    public static ulong DeadlineAfter(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }

        var durationTicks = checked((ulong)Math.Ceiling(
            duration.TotalSeconds * Stopwatch.Frequency));
        return checked(MonotonicNow() + Math.Max(1UL, durationTicks));
    }

    internal static ulong DurationToNativeTicks(
        TimeSpan duration,
        ulong maximumDuration)
    {
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }

        var product =
            (UInt128)checked((ulong)duration.Ticks)
            * checked((ulong)Stopwatch.Frequency);
        var divisor = (UInt128)TimeSpan.TicksPerSecond;
        var durationTicks = (product + divisor - 1) / divisor;
        if (durationTicks == 0
            || durationTicks > maximumDuration
            || durationTicks > ulong.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }

        return (ulong)durationTicks;
    }

    public void Dispose()
    {
        var handle = Interlocked.Exchange(ref _handle, IntPtr.Zero);
        if (handle != IntPtr.Zero)
        {
            NativeSharedResourceMethods.rm_shared_ledger_destroy(handle);
        }

        _mutex.Dispose();
        _region.Dispose();
    }

    private IntPtr RequireHandle()
    {
        var handle = Volatile.Read(ref _handle);
        ObjectDisposedException.ThrowIf(handle == IntPtr.Zero, this);
        return handle;
    }

    private static NativeLedgerConfig ToNativeConfig(
        ulong ledgerInstanceId,
        SharedResourceSessionOptions options)
        => new()
        {
            AbiVersion = SharedResourceProtocol.Version,
            StructSize = checked((uint)Marshal.SizeOf<NativeLedgerConfig>()),
            LedgerInstanceId = ledgerInstanceId,
            OwnerApplicationKey = options.OwnerApplicationKey,
            OwnerProcessCreatedUtcTicks = options.OwnerProcessCreatedAt.UtcDateTime.Ticks,
            SubscriptionCoefficient = options.SubscriptionCoefficient,
            ResourceCapacity = checked((uint)options.ResourceCapacity),
            SubscriptionCapacity = checked((uint)options.SubscriptionCapacity),
            TaskCapacity = checked((uint)options.TaskCapacity),
            OwnerProcessId = options.OwnerProcessId,
            SynchronizationKind = 1,
            LeaseClockDomain =
                (uint)SharedResourceLeaseClockDomain.WindowsPerformanceCounter,
            LeaseClockFrequencyHz = checked((ulong)Stopwatch.Frequency),
            MaximumSubscriptionTtl = DurationToNativeTicks(
                options.MaximumSubscriptionLeaseDuration,
                ulong.MaxValue),
            MaximumQueueTtl = DurationToNativeTicks(
                options.MaximumQueueDuration,
                ulong.MaxValue),
            MaximumGrantTtl = DurationToNativeTicks(
                options.MaximumGrantDuration,
                ulong.MaxValue)
        };

    private static NativeLedgerConfig ToNativeConfig(SharedResourceLedgerDescriptor descriptor)
        => new()
        {
            AbiVersion = descriptor.ProtocolVersion,
            StructSize = checked((uint)Marshal.SizeOf<NativeLedgerConfig>()),
            LedgerInstanceId = descriptor.LedgerInstanceId,
            OwnerApplicationKey = descriptor.OwnerApplicationKey,
            OwnerProcessCreatedUtcTicks = descriptor.OwnerProcessCreatedUtcTicks,
            SubscriptionCoefficient = descriptor.SubscriptionCoefficient,
            ResourceCapacity = checked((uint)descriptor.ResourceCapacity),
            SubscriptionCapacity = checked((uint)descriptor.SubscriptionCapacity),
            TaskCapacity = checked((uint)descriptor.TaskCapacity),
            OwnerProcessId = descriptor.OwnerProcessId,
            SynchronizationKind = 1,
            LeaseClockDomain = (uint)descriptor.LeaseClockDomain,
            LeaseClockFrequencyHz = descriptor.LeaseClockFrequency,
            MaximumSubscriptionTtl = descriptor.MaximumSubscriptionTtl,
            MaximumQueueTtl = descriptor.MaximumQueueTtl,
            MaximumGrantTtl = descriptor.MaximumGrantTtl
        };

    private static NativeResourcePublication ToNative(SharedResourcePublication publication)
        => new()
        {
            AbiVersion = SharedResourceProtocol.Version,
            StructSize = checked((uint)Marshal.SizeOf<NativeResourcePublication>()),
            PublicResourceId = publication.PublicResourceId,
            OwnerApplicationKey = publication.OwnerApplicationKey,
            OwnerInstanceIdLow = publication.OwnerInstanceId.Low,
            OwnerInstanceIdHigh = publication.OwnerInstanceId.High,
            OwnerContextGeneration = publication.OwnerContextGeneration,
            LeaseGeneration = publication.LeaseGeneration,
            BindingGeneration = publication.BindingGeneration,
            CapabilityGeneration = publication.CapabilityGeneration,
            ExecutorIdLow = publication.ExecutorId.Low,
            ExecutorIdHigh = publication.ExecutorId.High,
            ResourceKey = publication.ResourceKey,
            AdapterKey = publication.AdapterKey,
            SizeBytes = publication.SizeBytes,
            ContentIdentityHash = publication.ContentIdentityHash,
            PayloadMappingId = publication.PayloadMappingId,
            PayloadGeneration = publication.PayloadGeneration,
            LastUpdatedUtcTicks = DateTime.UtcNow.Ticks,
            OwnerProcessId = publication.OwnerProcessId,
            ResourceId = publication.ResourceId,
            MaxParallelGrants = publication.MaxParallelGrants,
            Tier = (byte)publication.Tier,
            ResourceKind = (byte)publication.ResourceKind,
            RecoveryKind = (byte)publication.RecoveryKind,
            Granularity = (byte)publication.Granularity,
            InapplicableActions = (byte)publication.InapplicableActions,
            ActionRoute = (byte)publication.ActionRoute,
            OwnerDemandMask = (byte)publication.OwnerDemandMask,
            ActivityScore = 0,
            SurfaceState = (byte)publication.SurfaceState,
            Availability = (byte)publication.Availability,
            Flags = (byte)publication.Flags
        };

    private static NativeUseRequest ToNative(
        SharedResourceUseRequest request,
        SharedResourceLedgerDescriptor descriptor)
        => new()
        {
            AbiVersion = SharedResourceProtocol.Version,
            StructSize = checked((uint)Marshal.SizeOf<NativeUseRequest>()),
            Resource = ToNative(request.ResourceId),
            RequestKey = request.RequestKey,
            RequesterApplicationKey = request.RequesterApplicationKey,
            RequesterInstanceId = request.RequesterInstanceId,
            RequesterSessionId = request.RequesterSessionId,
            ScorePlanGeneration = request.ScorePlanGeneration,
            QueueLeaseDuration = DurationToNativeTicks(
                request.QueueLeaseDuration,
                descriptor.MaximumQueueTtl),
            BaseScore = request.BaseScore,
            RequesterProcessId = request.RequesterProcessId
        };

    private static NativeResourceRef ToNative(SharedResourceId id)
        => new()
        {
            LedgerInstanceId = id.LedgerInstanceId,
            ResourceGeneration = id.ResourceGeneration,
            PublicResourceId = id.PublicResourceId,
            ResourceSlot = id.ResourceSlot
        };

    private static NativeSubscriptionReceipt ToNative(SharedResourceSubscriptionReceipt receipt)
        => new()
        {
            Resource = ToNative(receipt.ResourceId),
            SubscriberInstanceId = receipt.SubscriberInstanceId,
            SubscriberSessionId = receipt.SubscriberSessionId,
            DeadlineTimestamp = receipt.DeadlineTimestamp,
            SubscriptionGeneration = receipt.SubscriptionGeneration,
            SubscriptionSlot = receipt.SubscriptionSlot,
            SubscriberProcessId = receipt.SubscriberProcessId,
            IntentMask = (byte)receipt.Intent
        };

    private static NativeTaskReceipt ToNative(SharedResourceTaskReceipt receipt)
        => new()
        {
            Resource = ToNative(receipt.ResourceId),
            RequestKey = receipt.RequestKey,
            RequesterApplicationKey = receipt.RequesterApplicationKey,
            RequesterInstanceId = receipt.RequesterInstanceId,
            RequesterSessionId = receipt.RequesterSessionId,
            TaskGeneration = receipt.TaskGeneration,
            TaskSlot = receipt.TaskSlot,
            State = (byte)receipt.State
        };

    private static NativeGrantReceipt ToNative(SharedResourceGrantReceipt receipt)
        => new()
        {
            Task = ToNative(receipt.Task),
            GrantGeneration = receipt.GrantGeneration,
            PayloadGeneration = receipt.PayloadGeneration,
            DeadlineTimestamp = receipt.DeadlineTimestamp
        };

    private static NativeDestructiveToken ToNative(SharedResourceDestructiveToken token)
        => new()
        {
            Resource = ToNative(token.ResourceId),
            SchedulingRevision = token.SchedulingRevision,
            GateEpoch = token.GateEpoch,
            ActionAttemptIdLow = token.ActionAttemptId.Low,
            ActionAttemptIdHigh = token.ActionAttemptId.High,
            ActionMask = (byte)token.Action
        };

    private static NativeRecallExpectedRequest ToNative(
        SharedResourceRecallExpected expected)
        => new()
        {
            AbiVersion = SharedResourceProtocol.Version,
            StructSize = checked((uint)Marshal.SizeOf<NativeRecallExpectedRequest>()),
            Resource = ToNative(expected.ResourceId),
            SchedulingRevision = expected.SchedulingRevision,
            GateEpoch = expected.GateEpoch,
            ActionAttemptIdLow = expected.ActionAttemptId.Low,
            ActionAttemptIdHigh = expected.ActionAttemptId.High,
            PayloadGeneration = expected.PayloadGeneration,
            SubscriberCount = checked((uint)expected.SubscriberCount),
            ActivityScore = expected.ActivityScore,
            Flags = (byte)expected.Flags
        };

    private static NativeRecallToken ToNative(SharedResourceRecallToken token)
        => new()
        {
            Resource = ToNative(token.ResourceId),
            SchedulingRevision = token.SchedulingRevision,
            GateEpoch = token.GateEpoch,
            ActionAttemptIdLow = token.ActionAttemptId.Low,
            ActionAttemptIdHigh = token.ActionAttemptId.High,
            PayloadGeneration = token.PayloadGeneration
        };

    private static NativeDestructiveExpectedRequest ToNative(
        SharedResourceDestructiveExpected expected)
        => new()
        {
            AbiVersion = SharedResourceProtocol.Version,
            StructSize = checked((uint)Marshal.SizeOf<NativeDestructiveExpectedRequest>()),
            Resource = ToNative(expected.ResourceId),
            SchedulingRevision = expected.SchedulingRevision,
            ActionAttemptIdLow = expected.ActionAttemptId.Low,
            ActionAttemptIdHigh = expected.ActionAttemptId.High,
            ActionMask = (byte)expected.Action
        };

    private static NativeDestructiveNoEffectReceipt ToNative(
        SharedResourceDestructiveNoEffectReceipt receipt)
        => new()
        {
            AbiVersion = SharedResourceProtocol.Version,
            StructSize = checked((uint)Marshal.SizeOf<NativeDestructiveNoEffectReceipt>()),
            ActionAttemptIdLow = receipt.ActionAttemptId.Low,
            ActionAttemptIdHigh = receipt.ActionAttemptId.High,
            ReceiptIdLow = receipt.ReceiptId.Low,
            ReceiptIdHigh = receipt.ReceiptId.High,
            ObservedMonotonicTimestamp = receipt.ObservedMonotonicTimestamp,
            ProofMask = (byte)receipt.Proof,
            Reason = (byte)receipt.Reason
        };

    private static SharedResourceId FromNative(NativeResourceRef id)
        => new(
            id.LedgerInstanceId,
            id.ResourceSlot,
            id.ResourceGeneration,
            id.PublicResourceId);

    private static SharedResourceSnapshot FromNative(NativeResourceSnapshot snapshot)
    {
        if (snapshot.AbiVersion != SharedResourceProtocol.Version
            || snapshot.StructSize != Marshal.SizeOf<NativeResourceSnapshot>())
        {
            throw new InvalidOperationException("Native shared-resource snapshot ABI mismatch.");
        }

        return new SharedResourceSnapshot(
            FromNative(snapshot.Id),
            snapshot.OwnerApplicationKey,
            new SharedResourceAuthorityId(
                snapshot.OwnerInstanceIdHigh,
                snapshot.OwnerInstanceIdLow),
            snapshot.OwnerContextGeneration,
            snapshot.LeaseGeneration,
            snapshot.BindingGeneration,
            snapshot.CapabilityGeneration,
            new SharedResourceAuthorityId(
                snapshot.ExecutorIdHigh,
                snapshot.ExecutorIdLow),
            snapshot.OwnerProcessId,
            snapshot.ResourceKey,
            snapshot.AdapterKey,
            snapshot.ResourceId,
            snapshot.SizeBytes,
            snapshot.ContentIdentityHash,
            snapshot.PayloadMappingId,
            snapshot.PayloadGeneration,
            snapshot.MaxParallelGrants,
            (AdapterResourceTier)snapshot.Tier,
            (AdapterResourceKind)snapshot.ResourceKind,
            (AdapterResourceRecoveryKind)snapshot.RecoveryKind,
            (AdapterResourceGranularity)snapshot.Granularity,
            (AdapterResourceActionMask)snapshot.InapplicableActions,
            (AdapterResourceActionRoute)snapshot.ActionRoute,
            (AdapterResourceDemandMask)snapshot.OwnerDemandMask,
            (SharedResourceSubscriptionIntent)snapshot.SubscriptionIntentMask,
            snapshot.ActivityScore,
            (AdapterSoftwareSurfaceState)snapshot.SurfaceState,
            (SharedResourceAvailability)snapshot.Availability,
            (SharedResourceFlags)snapshot.Flags,
            checked((int)snapshot.ActiveSubscriberCount),
            checked((int)snapshot.QueuedRequestCount),
            checked((int)snapshot.ReservedGrantCount),
            checked((int)snapshot.ActiveUseCount),
            snapshot.SchedulingRevision,
            snapshot.GateEpoch,
            snapshot.SubscriptionMultiplier,
            new DateTimeOffset(new DateTime(snapshot.LastUpdatedUtcTicks, DateTimeKind.Utc)),
            (AdapterResourceActionMask)snapshot.AllowedActions,
            snapshot.ConsistencyState == 0,
            snapshot.DestructiveActive != 0);
    }

    private static SharedResourceSubscriptionReceipt FromNative(NativeSubscriptionReceipt receipt)
        => new(
            FromNative(receipt.Resource),
            receipt.SubscriptionSlot,
            receipt.SubscriptionGeneration,
            receipt.SubscriberInstanceId,
            receipt.SubscriberSessionId,
            receipt.SubscriberProcessId,
            receipt.DeadlineTimestamp,
            (SharedResourceSubscriptionIntent)receipt.IntentMask);

    private static SharedResourceTaskReceipt FromNative(NativeTaskReceipt receipt)
        => new(
            FromNative(receipt.Resource),
            receipt.RequestKey,
            receipt.RequesterApplicationKey,
            receipt.RequesterInstanceId,
            receipt.RequesterSessionId,
            receipt.TaskSlot,
            receipt.TaskGeneration,
            (SharedResourceTaskState)receipt.State);

    private static SharedResourceGrantReceipt FromNative(NativeGrantReceipt receipt)
        => new(
            FromNative(receipt.Task),
            receipt.GrantGeneration,
            receipt.PayloadGeneration,
            receipt.DeadlineTimestamp);

    private static SharedResourceDestructiveToken FromNative(NativeDestructiveToken token)
        => new(
            FromNative(token.Resource),
            token.SchedulingRevision,
            token.GateEpoch,
            new SharedResourceAuthorityId(
                token.ActionAttemptIdHigh,
                token.ActionAttemptIdLow),
            (AdapterResourceActionMask)token.ActionMask);

    private static SharedResourceRecallToken FromNative(NativeRecallToken token)
        => new(
            FromNative(token.Resource),
            token.SchedulingRevision,
            token.GateEpoch,
            new SharedResourceAuthorityId(
                token.ActionAttemptIdHigh,
                token.ActionAttemptIdLow),
            token.PayloadGeneration);

    private static bool HandleTryMutation(string operation, int result)
    {
        if (result == (int)NativeSharedResourceResult.Ok)
        {
            return true;
        }

        if (result is (int)NativeSharedResourceResult.NotFound
            or (int)NativeSharedResourceResult.StaleReference
            or (int)NativeSharedResourceResult.ResourceBusy
            or (int)NativeSharedResourceResult.Expired)
        {
            return false;
        }

        ThrowIfFailure(operation, result);
        return false;
    }

    private static void ValidateOptions(SharedResourceSessionOptions options)
    {
        if (options.ResourceCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options.ResourceCapacity));
        }

        if (options.SubscriptionCapacity < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options.SubscriptionCapacity));
        }

        if (options.TaskCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options.TaskCapacity));
        }

        if (!double.IsFinite(options.SubscriptionCoefficient)
            || options.SubscriptionCoefficient < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options.SubscriptionCoefficient));
        }

        if (options.OwnerApplicationKey == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options.OwnerApplicationKey));
        }

        _ = DurationToNativeTicks(
            options.MaximumSubscriptionLeaseDuration,
            ulong.MaxValue);
        _ = DurationToNativeTicks(
            options.MaximumQueueDuration,
            ulong.MaxValue);
        _ = DurationToNativeTicks(
            options.MaximumGrantDuration,
            ulong.MaxValue);
    }

    private static void EnsureAbi()
    {
        var nativeVersion = NativeSharedResourceMethods.rm_shared_ledger_abi_version();
        if (nativeVersion != SharedResourceProtocol.Version)
        {
            throw new InvalidOperationException(
                $"Native shared-resource ABI {nativeVersion} does not match {SharedResourceProtocol.Version}.");
        }

        EnsureSize<NativeLedgerConfig>(96);
        EnsureSize<NativeResourceRef>(32);
        EnsureSize<NativeResourcePublication>(176);
        EnsureSize<NativeResourceSnapshot>(240);
        EnsureSize<NativeSubscriptionRequest>(88);
        EnsureSize<NativeSubscriptionReceipt>(80);
        EnsureSize<NativeUseRequest>(104);
        EnsureSize<NativeTaskReceipt>(80);
        EnsureSize<NativeGrantReceipt>(104);
        EnsureSize<NativeDestructiveExpectedRequest>(72);
        EnsureSize<NativeDestructiveToken>(72);
        EnsureSize<NativeRecallExpectedRequest>(88);
        EnsureSize<NativeRecallToken>(72);
        EnsureSize<NativeDestructiveNoEffectReceipt>(56);
        EnsureSize<NativeHostPublicManagerPlanRequest>(48);
        EnsureSize<NativeHostPublicUnloadCandidate>(72);
        EnsureSize<NativeHostPublicManagerPlanSummary>(48);
        var nativeFingerprint = NativeSharedResourceMethods.rm_shared_resource_layout_fingerprint();
        var managedFingerprint = ManagedLayoutFingerprint();
        if (nativeFingerprint != managedFingerprint)
        {
            throw new InvalidOperationException(
                $"Native shared-resource layout fingerprint {nativeFingerprint:X16} does not match managed {managedFingerprint:X16}.");
        }
    }

    private static ulong ReadNativeCapabilities()
    {
        try
        {
            return NativeSharedResourceMethods.rm_shared_ledger_capabilities();
        }
        catch (EntryPointNotFoundException)
        {
            return 0;
        }
    }

    private static void ValidateHostPublicManagerOptions(HostPublicResourceManagerOptions options)
    {
        if (options.SubscriberWeight == 0
            || options.ActivityWeight == 0
            || options.WeightScale == 0
            || (ulong)options.SubscriberWeight + options.ActivityWeight != options.WeightScale
            || options.SubscriberHalfSaturation == 0
            || options.ActivityHalfSaturation == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Host public-resource weights must be positive, sum to WeightScale, and use positive half-saturation constants.");
        }
    }

    private static ulong ManagedLayoutFingerprint()
    {
        ReadOnlySpan<ulong> values =
        [
            SizeOf<NativeLedgerConfig>(),
            OffsetOf<NativeLedgerConfig>(nameof(NativeLedgerConfig.AbiVersion)),
            OffsetOf<NativeLedgerConfig>(nameof(NativeLedgerConfig.StructSize)),
            OffsetOf<NativeLedgerConfig>(nameof(NativeLedgerConfig.LedgerInstanceId)),
            OffsetOf<NativeLedgerConfig>(nameof(NativeLedgerConfig.OwnerApplicationKey)),
            OffsetOf<NativeLedgerConfig>(nameof(NativeLedgerConfig.OwnerProcessCreatedUtcTicks)),
            OffsetOf<NativeLedgerConfig>(nameof(NativeLedgerConfig.SubscriptionCoefficient)),
            OffsetOf<NativeLedgerConfig>(nameof(NativeLedgerConfig.ResourceCapacity)),
            OffsetOf<NativeLedgerConfig>(nameof(NativeLedgerConfig.SubscriptionCapacity)),
            OffsetOf<NativeLedgerConfig>(nameof(NativeLedgerConfig.TaskCapacity)),
            OffsetOf<NativeLedgerConfig>(nameof(NativeLedgerConfig.OwnerProcessId)),
            OffsetOf<NativeLedgerConfig>(nameof(NativeLedgerConfig.SynchronizationKind)),
            OffsetOf<NativeLedgerConfig>(nameof(NativeLedgerConfig.Reserved0)),
            OffsetOf<NativeLedgerConfig>(nameof(NativeLedgerConfig.LeaseClockDomain)),
            OffsetOf<NativeLedgerConfig>(nameof(NativeLedgerConfig.LeaseClockFrequencyHz)),
            OffsetOf<NativeLedgerConfig>(nameof(NativeLedgerConfig.MaximumSubscriptionTtl)),
            OffsetOf<NativeLedgerConfig>(nameof(NativeLedgerConfig.MaximumQueueTtl)),
            OffsetOf<NativeLedgerConfig>(nameof(NativeLedgerConfig.MaximumGrantTtl)),

            SizeOf<NativeSubscriptionRequest>(),
            OffsetOf<NativeSubscriptionRequest>(nameof(NativeSubscriptionRequest.AbiVersion)),
            OffsetOf<NativeSubscriptionRequest>(nameof(NativeSubscriptionRequest.StructSize)),
            OffsetOf<NativeSubscriptionRequest>(nameof(NativeSubscriptionRequest.Resource)),
            OffsetOf<NativeSubscriptionRequest>(nameof(NativeSubscriptionRequest.SubscriberApplicationKey)),
            OffsetOf<NativeSubscriptionRequest>(nameof(NativeSubscriptionRequest.SubscriberInstanceId)),
            OffsetOf<NativeSubscriptionRequest>(nameof(NativeSubscriptionRequest.SubscriberSessionId)),
            OffsetOf<NativeSubscriptionRequest>(nameof(NativeSubscriptionRequest.LeaseDuration)),
            OffsetOf<NativeSubscriptionRequest>(nameof(NativeSubscriptionRequest.SubscriberProcessId)),
            OffsetOf<NativeSubscriptionRequest>(nameof(NativeSubscriptionRequest.IntentMask)),
            OffsetOf<NativeSubscriptionRequest>(nameof(NativeSubscriptionRequest.Reserved00)),
            OffsetOf<NativeSubscriptionRequest>(nameof(NativeSubscriptionRequest.Reserved1)),

            SizeOf<NativeUseRequest>(),
            OffsetOf<NativeUseRequest>(nameof(NativeUseRequest.AbiVersion)),
            OffsetOf<NativeUseRequest>(nameof(NativeUseRequest.StructSize)),
            OffsetOf<NativeUseRequest>(nameof(NativeUseRequest.Resource)),
            OffsetOf<NativeUseRequest>(nameof(NativeUseRequest.RequestKey)),
            OffsetOf<NativeUseRequest>(nameof(NativeUseRequest.RequesterApplicationKey)),
            OffsetOf<NativeUseRequest>(nameof(NativeUseRequest.RequesterInstanceId)),
            OffsetOf<NativeUseRequest>(nameof(NativeUseRequest.RequesterSessionId)),
            OffsetOf<NativeUseRequest>(nameof(NativeUseRequest.ScorePlanGeneration)),
            OffsetOf<NativeUseRequest>(nameof(NativeUseRequest.QueueLeaseDuration)),
            OffsetOf<NativeUseRequest>(nameof(NativeUseRequest.BaseScore)),
            OffsetOf<NativeUseRequest>(nameof(NativeUseRequest.RequesterProcessId)),
            OffsetOf<NativeUseRequest>(nameof(NativeUseRequest.Reserved0)),

            SizeOf<NativeResourceRef>(),
            OffsetOf<NativeResourceRef>(nameof(NativeResourceRef.LedgerInstanceId)),
            OffsetOf<NativeResourceRef>(nameof(NativeResourceRef.ResourceGeneration)),
            OffsetOf<NativeResourceRef>(nameof(NativeResourceRef.PublicResourceId)),
            OffsetOf<NativeResourceRef>(nameof(NativeResourceRef.ResourceSlot)),
            OffsetOf<NativeResourceRef>(nameof(NativeResourceRef.Reserved)),

            SizeOf<NativeResourcePublication>(),
            OffsetOf<NativeResourcePublication>(nameof(NativeResourcePublication.AbiVersion)),
            OffsetOf<NativeResourcePublication>(nameof(NativeResourcePublication.StructSize)),
            OffsetOf<NativeResourcePublication>(nameof(NativeResourcePublication.PublicResourceId)),
            OffsetOf<NativeResourcePublication>(nameof(NativeResourcePublication.OwnerApplicationKey)),
            OffsetOf<NativeResourcePublication>(nameof(NativeResourcePublication.OwnerInstanceIdLow)),
            OffsetOf<NativeResourcePublication>(nameof(NativeResourcePublication.OwnerInstanceIdHigh)),
            OffsetOf<NativeResourcePublication>(nameof(NativeResourcePublication.OwnerContextGeneration)),
            OffsetOf<NativeResourcePublication>(nameof(NativeResourcePublication.LeaseGeneration)),
            OffsetOf<NativeResourcePublication>(nameof(NativeResourcePublication.BindingGeneration)),
            OffsetOf<NativeResourcePublication>(nameof(NativeResourcePublication.CapabilityGeneration)),
            OffsetOf<NativeResourcePublication>(nameof(NativeResourcePublication.ExecutorIdLow)),
            OffsetOf<NativeResourcePublication>(nameof(NativeResourcePublication.ExecutorIdHigh)),
            OffsetOf<NativeResourcePublication>(nameof(NativeResourcePublication.ResourceKey)),
            OffsetOf<NativeResourcePublication>(nameof(NativeResourcePublication.AdapterKey)),
            OffsetOf<NativeResourcePublication>(nameof(NativeResourcePublication.SizeBytes)),
            OffsetOf<NativeResourcePublication>(nameof(NativeResourcePublication.ContentIdentityHash)),
            OffsetOf<NativeResourcePublication>(nameof(NativeResourcePublication.PayloadMappingId)),
            OffsetOf<NativeResourcePublication>(nameof(NativeResourcePublication.PayloadGeneration)),
            OffsetOf<NativeResourcePublication>(nameof(NativeResourcePublication.LastUpdatedUtcTicks)),
            OffsetOf<NativeResourcePublication>(nameof(NativeResourcePublication.OwnerProcessId)),
            OffsetOf<NativeResourcePublication>(nameof(NativeResourcePublication.ResourceId)),
            OffsetOf<NativeResourcePublication>(nameof(NativeResourcePublication.MaxParallelGrants)),
            OffsetOf<NativeResourcePublication>(nameof(NativeResourcePublication.Tier)),
            OffsetOf<NativeResourcePublication>(nameof(NativeResourcePublication.ResourceKind)),
            OffsetOf<NativeResourcePublication>(nameof(NativeResourcePublication.RecoveryKind)),
            OffsetOf<NativeResourcePublication>(nameof(NativeResourcePublication.Granularity)),
            OffsetOf<NativeResourcePublication>(nameof(NativeResourcePublication.InapplicableActions)),
            OffsetOf<NativeResourcePublication>(nameof(NativeResourcePublication.ActionRoute)),
            OffsetOf<NativeResourcePublication>(nameof(NativeResourcePublication.OwnerDemandMask)),
            OffsetOf<NativeResourcePublication>(nameof(NativeResourcePublication.ActivityScore)),
            OffsetOf<NativeResourcePublication>(nameof(NativeResourcePublication.SurfaceState)),
            OffsetOf<NativeResourcePublication>(nameof(NativeResourcePublication.Availability)),
            OffsetOf<NativeResourcePublication>(nameof(NativeResourcePublication.Flags)),
            OffsetOf<NativeResourcePublication>(nameof(NativeResourcePublication.Reserved0)),
            OffsetOf<NativeResourcePublication>(nameof(NativeResourcePublication.Reserved1)),

            SizeOf<NativeResourceSnapshot>(),
            OffsetOf<NativeResourceSnapshot>(nameof(NativeResourceSnapshot.AbiVersion)),
            OffsetOf<NativeResourceSnapshot>(nameof(NativeResourceSnapshot.StructSize)),
            OffsetOf<NativeResourceSnapshot>(nameof(NativeResourceSnapshot.Id)),
            OffsetOf<NativeResourceSnapshot>(nameof(NativeResourceSnapshot.OwnerApplicationKey)),
            OffsetOf<NativeResourceSnapshot>(nameof(NativeResourceSnapshot.OwnerInstanceIdLow)),
            OffsetOf<NativeResourceSnapshot>(nameof(NativeResourceSnapshot.OwnerInstanceIdHigh)),
            OffsetOf<NativeResourceSnapshot>(nameof(NativeResourceSnapshot.OwnerContextGeneration)),
            OffsetOf<NativeResourceSnapshot>(nameof(NativeResourceSnapshot.LeaseGeneration)),
            OffsetOf<NativeResourceSnapshot>(nameof(NativeResourceSnapshot.BindingGeneration)),
            OffsetOf<NativeResourceSnapshot>(nameof(NativeResourceSnapshot.CapabilityGeneration)),
            OffsetOf<NativeResourceSnapshot>(nameof(NativeResourceSnapshot.ExecutorIdLow)),
            OffsetOf<NativeResourceSnapshot>(nameof(NativeResourceSnapshot.ExecutorIdHigh)),
            OffsetOf<NativeResourceSnapshot>(nameof(NativeResourceSnapshot.ResourceKey)),
            OffsetOf<NativeResourceSnapshot>(nameof(NativeResourceSnapshot.AdapterKey)),
            OffsetOf<NativeResourceSnapshot>(nameof(NativeResourceSnapshot.SizeBytes)),
            OffsetOf<NativeResourceSnapshot>(nameof(NativeResourceSnapshot.ContentIdentityHash)),
            OffsetOf<NativeResourceSnapshot>(nameof(NativeResourceSnapshot.PayloadMappingId)),
            OffsetOf<NativeResourceSnapshot>(nameof(NativeResourceSnapshot.PayloadGeneration)),
            OffsetOf<NativeResourceSnapshot>(nameof(NativeResourceSnapshot.LastUpdatedUtcTicks)),
            OffsetOf<NativeResourceSnapshot>(nameof(NativeResourceSnapshot.SubscriptionMultiplier)),
            OffsetOf<NativeResourceSnapshot>(nameof(NativeResourceSnapshot.OwnerProcessId)),
            OffsetOf<NativeResourceSnapshot>(nameof(NativeResourceSnapshot.ResourceId)),
            OffsetOf<NativeResourceSnapshot>(nameof(NativeResourceSnapshot.ActiveSubscriberCount)),
            OffsetOf<NativeResourceSnapshot>(nameof(NativeResourceSnapshot.QueuedRequestCount)),
            OffsetOf<NativeResourceSnapshot>(nameof(NativeResourceSnapshot.ReservedGrantCount)),
            OffsetOf<NativeResourceSnapshot>(nameof(NativeResourceSnapshot.ActiveUseCount)),
            OffsetOf<NativeResourceSnapshot>(nameof(NativeResourceSnapshot.SchedulingRevision)),
            OffsetOf<NativeResourceSnapshot>(nameof(NativeResourceSnapshot.GateEpoch)),
            OffsetOf<NativeResourceSnapshot>(nameof(NativeResourceSnapshot.MaxParallelGrants)),
            OffsetOf<NativeResourceSnapshot>(nameof(NativeResourceSnapshot.Tier)),
            OffsetOf<NativeResourceSnapshot>(nameof(NativeResourceSnapshot.ResourceKind)),
            OffsetOf<NativeResourceSnapshot>(nameof(NativeResourceSnapshot.RecoveryKind)),
            OffsetOf<NativeResourceSnapshot>(nameof(NativeResourceSnapshot.Granularity)),
            OffsetOf<NativeResourceSnapshot>(nameof(NativeResourceSnapshot.InapplicableActions)),
            OffsetOf<NativeResourceSnapshot>(nameof(NativeResourceSnapshot.ActionRoute)),
            OffsetOf<NativeResourceSnapshot>(nameof(NativeResourceSnapshot.OwnerDemandMask)),
            OffsetOf<NativeResourceSnapshot>(nameof(NativeResourceSnapshot.SubscriptionIntentMask)),
            OffsetOf<NativeResourceSnapshot>(nameof(NativeResourceSnapshot.ActivityScore)),
            OffsetOf<NativeResourceSnapshot>(nameof(NativeResourceSnapshot.SurfaceState)),
            OffsetOf<NativeResourceSnapshot>(nameof(NativeResourceSnapshot.Availability)),
            OffsetOf<NativeResourceSnapshot>(nameof(NativeResourceSnapshot.Flags)),
            OffsetOf<NativeResourceSnapshot>(nameof(NativeResourceSnapshot.AllowedActions)),
            OffsetOf<NativeResourceSnapshot>(nameof(NativeResourceSnapshot.ConsistencyState)),
            OffsetOf<NativeResourceSnapshot>(nameof(NativeResourceSnapshot.DestructiveActive)),
            OffsetOf<NativeResourceSnapshot>(nameof(NativeResourceSnapshot.Reserved0)),

            SizeOf<NativeDestructiveExpectedRequest>(),
            OffsetOf<NativeDestructiveExpectedRequest>(nameof(NativeDestructiveExpectedRequest.AbiVersion)),
            OffsetOf<NativeDestructiveExpectedRequest>(nameof(NativeDestructiveExpectedRequest.StructSize)),
            OffsetOf<NativeDestructiveExpectedRequest>(nameof(NativeDestructiveExpectedRequest.Resource)),
            OffsetOf<NativeDestructiveExpectedRequest>(nameof(NativeDestructiveExpectedRequest.SchedulingRevision)),
            OffsetOf<NativeDestructiveExpectedRequest>(nameof(NativeDestructiveExpectedRequest.ActionAttemptIdLow)),
            OffsetOf<NativeDestructiveExpectedRequest>(nameof(NativeDestructiveExpectedRequest.ActionAttemptIdHigh)),
            OffsetOf<NativeDestructiveExpectedRequest>(nameof(NativeDestructiveExpectedRequest.ActionMask)),
            OffsetOf<NativeDestructiveExpectedRequest>(nameof(NativeDestructiveExpectedRequest.Reserved00)),

            SizeOf<NativeDestructiveToken>(),
            OffsetOf<NativeDestructiveToken>(nameof(NativeDestructiveToken.Resource)),
            OffsetOf<NativeDestructiveToken>(nameof(NativeDestructiveToken.SchedulingRevision)),
            OffsetOf<NativeDestructiveToken>(nameof(NativeDestructiveToken.GateEpoch)),
            OffsetOf<NativeDestructiveToken>(nameof(NativeDestructiveToken.ActionAttemptIdLow)),
            OffsetOf<NativeDestructiveToken>(nameof(NativeDestructiveToken.ActionAttemptIdHigh)),
            OffsetOf<NativeDestructiveToken>(nameof(NativeDestructiveToken.ActionMask)),
            OffsetOf<NativeDestructiveToken>(nameof(NativeDestructiveToken.Reserved00)),

            SizeOf<NativeDestructiveNoEffectReceipt>(),
            OffsetOf<NativeDestructiveNoEffectReceipt>(nameof(NativeDestructiveNoEffectReceipt.AbiVersion)),
            OffsetOf<NativeDestructiveNoEffectReceipt>(nameof(NativeDestructiveNoEffectReceipt.StructSize)),
            OffsetOf<NativeDestructiveNoEffectReceipt>(nameof(NativeDestructiveNoEffectReceipt.ActionAttemptIdLow)),
            OffsetOf<NativeDestructiveNoEffectReceipt>(nameof(NativeDestructiveNoEffectReceipt.ActionAttemptIdHigh)),
            OffsetOf<NativeDestructiveNoEffectReceipt>(nameof(NativeDestructiveNoEffectReceipt.ReceiptIdLow)),
            OffsetOf<NativeDestructiveNoEffectReceipt>(nameof(NativeDestructiveNoEffectReceipt.ReceiptIdHigh)),
            OffsetOf<NativeDestructiveNoEffectReceipt>(nameof(NativeDestructiveNoEffectReceipt.ObservedMonotonicTimestamp)),
            OffsetOf<NativeDestructiveNoEffectReceipt>(nameof(NativeDestructiveNoEffectReceipt.ProofMask)),
            OffsetOf<NativeDestructiveNoEffectReceipt>(nameof(NativeDestructiveNoEffectReceipt.Reason)),
            OffsetOf<NativeDestructiveNoEffectReceipt>(nameof(NativeDestructiveNoEffectReceipt.Reserved00)),

        ];
        var hash = 14695981039346656037UL;
        foreach (var value in values)
        {
            hash = unchecked((hash ^ value) * 1099511628211UL);
        }

        return hash;
    }

    private static ulong SizeOf<T>()
        where T : struct
        => checked((ulong)Marshal.SizeOf<T>());

    private static ulong OffsetOf<T>(string fieldName)
        where T : struct
        => checked((ulong)Marshal.OffsetOf<T>(fieldName).ToInt64());

    private static void EnsureSize<T>(int expected)
        where T : struct
    {
        var actual = Marshal.SizeOf<T>();
        if (actual != expected)
        {
            throw new InvalidOperationException(
                $"Native shared-resource ABI size mismatch for {typeof(T).Name}: {actual} != {expected}.");
        }
    }

    private static void EnsureCount<T>(
        IReadOnlyList<T>? values,
        int expected,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        if (values.Count != expected)
        {
            throw new ArgumentException(
                $"Scheduler configuration field '{parameterName}' requires exactly {expected} values.",
                parameterName);
        }
    }

    private static void ThrowIfFailure(string operation, int result)
    {
        if (result != (int)NativeSharedResourceResult.Ok)
        {
            throw new SharedResourceNativeException(operation, result);
        }
    }

    private static ulong CreateNonZeroId()
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        ulong value;
        do
        {
            RandomNumberGenerator.Fill(bytes);
            value = BitConverter.ToUInt64(bytes);
        }
        while (value == 0);

        return value;
    }
}
