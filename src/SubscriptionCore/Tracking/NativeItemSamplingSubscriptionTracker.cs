using System.Runtime.CompilerServices;
using System.Collections.Immutable;

using ResourceManager.Subscription.Native;


namespace ResourceManager.Subscription;

/// <summary>Tracks subscription-source items and their current sampling plans.</summary>
public partial class NativeItemSamplingSubscriptionTracker<TRequest, TContext>
{
    private const string CoalescedCaptureItemId = "\u001fcapture";
    private readonly object gate = new();
    private readonly ISamplingSubscriptionSessionOwner<TContext> owner;
    private readonly int roleId;
    private readonly Func<TRequest, string> createSourceKey;
    private readonly Func<TRequest, IEnumerable<string>> getItemIds;
    private readonly Func<
        IReadOnlyList<string>,
        IReadOnlyList<NativeItemSamplingSubscriptionSourceView<TRequest>>,
        TRequest?> createRequest;
    private readonly bool coalesceItemsIntoLatestCapture;
    private readonly Dictionary<string, ulong> sourceHandles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ulong> itemHandles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<ulong, string> itemIds = [];
    private readonly Dictionary<ulong, SourcePayload> sourcePayloads = [];
    private NativeSamplingSubscriptionDueItem[] dueBuffer = [];
    private NativeSamplingSubscriptionSourceView[] sourceBuffer = [];
    private NativeSamplingSubscriptionExpiredSource[] expiredBuffer = [];
    private NativeSamplingSubscriptionTrigger[] triggerBuffer = [];
    private NativeSamplingSubscriptionItemReference[] pendingItems = [];
    private NativeSamplingSubscriptionTrigger[] pendingTriggers = [];
    private IReadOnlyList<string> latestMergedItemIds = [];
    private bool latestMergedItemIdsDirty = true;
    private NativeSamplingSubscriptionSession? boundSession;
    private NativeSamplingSubscriptionSession? replaySession;
    private ISamplingSubscriptionSessionLease<TContext>? pendingSessionLease;
    private TRequest? pendingRequest;
    private ulong pendingPlanEpoch;
    private ulong nextHandle;
    private ulong operationEpoch;
    private ulong planEpoch;
    private ulong replayOperationEpoch;
    private ulong lastCommandMilliseconds;
    private long lastCommandTimestamp;
    private readonly TimeProvider clock = TimeProvider.System;
    private ISubscriptionRoleSettings? lastRoleSettings;

    public NativeItemSamplingSubscriptionTracker(
        ISamplingSubscriptionSessionOwner<TContext> owner,
        int roleId,
        Func<TRequest, string> createSourceKey,
        Func<TRequest, IEnumerable<string>> getItemIds,
        Func<
            IReadOnlyList<string>,
            IReadOnlyList<NativeItemSamplingSubscriptionSourceView<TRequest>>,
            TRequest?> createRequest,
        bool coalesceItemsIntoLatestCapture)
    {
        this.owner = owner;
        this.roleId = roleId;
        this.createSourceKey = createSourceKey;
        this.getItemIds = getItemIds;
        this.createRequest = createRequest;
        this.coalesceItemsIntoLatestCapture = coalesceItemsIntoLatestCapture;
    }

    // Tests controlling UTC must also control the elapsed monotonic time.
    internal NativeItemSamplingSubscriptionTracker(
        ISamplingSubscriptionSessionOwner<TContext> owner, int roleId,
        Func<TRequest, string> createSourceKey, Func<TRequest, IEnumerable<string>> getItemIds,
        Func<IReadOnlyList<string>, IReadOnlyList<NativeItemSamplingSubscriptionSourceView<TRequest>>, TRequest?> createRequest,
        bool coalesceItemsIntoLatestCapture, TimeProvider clock)
        : this(owner, roleId, createSourceKey, getItemIds, createRequest, coalesceItemsIntoLatestCapture)
        => this.clock = clock ?? throw new ArgumentNullException(nameof(clock));

    /// <summary>Returns the exact native trigger item identities represented by this request.</summary>
    public IReadOnlyList<string> GetDeliveryItemIds(TRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var normalized = NormalizeItemIds(getItemIds(request));
        if (normalized.Count == 0) return Array.Empty<string>();
        return coalesceItemsIntoLatestCapture
            ? Array.AsReadOnly(new[] { CoalescedCaptureItemId })
            : Array.AsReadOnly(normalized.ToArray());
    }

    /// <summary>Validates the public field period against the current policy for this tracker's role.</summary>
    public void ValidatePeriod(TimeSpan interval)
    {
        if (interval.Ticks % TimeSpan.TicksPerMillisecond != 0)
            throw new ArgumentOutOfRangeException(nameof(interval), "The sampling period must be a whole number of milliseconds.");
        var milliseconds = interval.Ticks / TimeSpan.TicksPerMillisecond;
        using var lease = owner is ISamplingSubscriptionSettingsSource ? null : owner.Acquire(roleId);
        var roleSettings = owner is ISamplingSubscriptionSettingsSource settings
            ? settings.GetRoleSettings(roleId) : lease!.HotPublish;
        if (milliseconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(interval), "The sampling period must be positive.");
        // Before policy publication, retain the explicit subscription. The native
        // owner validates it against the real policy when its session is created.
        if (roleSettings is null) return;
        var minimum = roleSettings.MinimumIntervalMilliseconds;
        if (minimum <= 0 || milliseconds < minimum)
            throw new ArgumentOutOfRangeException(nameof(interval), "The sampling period must meet the current role minimum.");
    }

    public void Track(TRequest request, DateTimeOffset now)
        => TrackCore(request, now, persistent: false, explicitInterval: null,
            samplingFlags: NativeSamplingSubscriptionSourceFlags.None);

    public void TrackPersistent(TRequest request, DateTimeOffset now)
        => TrackCore(request, now, persistent: true, explicitInterval: null,
            samplingFlags: NativeSamplingSubscriptionSourceFlags.None);

    public void TrackPersistent(
        TRequest request,
        DateTimeOffset now,
        TimeSpan explicitInterval)
        => TrackCore(request, now, persistent: true, explicitInterval,
            samplingFlags: NativeSamplingSubscriptionSourceFlags.None);

    public void TrackPersistent(
        TRequest request,
        DateTimeOffset now,
        TimeSpan explicitInterval,
        NativeSamplingSubscriptionSourceFlags samplingFlags)
        => TrackCore(request, now, persistent: true, explicitInterval, samplingFlags);

    public void TrackPersistent(
        string sourceKey,
        TRequest request,
        DateTimeOffset now,
        TimeSpan explicitInterval)
        => TrackCore(
            request,
            now,
            persistent: true,
            explicitInterval,
            samplingFlags: NativeSamplingSubscriptionSourceFlags.None,
            explicitSourceKey: sourceKey);

    public void TrackPersistent(
        string sourceKey,
        TRequest request,
        DateTimeOffset now,
        TimeSpan explicitInterval,
        NativeSamplingSubscriptionSourceFlags samplingFlags)
        => TrackCore(
            request,
            now,
            persistent: true,
            explicitInterval,
            samplingFlags,
            sourceKey);

    public bool Remove(string sourceKey, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(sourceKey))
        {
            return false;
        }

        lock (gate)
        {
            var lease = AcquireMutationLease();
            try
            {
                lastRoleSettings = lease?.HotPublish ?? pendingSessionLease?.HotPublish ?? lastRoleSettings;
                var session = pendingSessionLease?.Session ?? lease?.Session;
                if (session is not null) EnsureBoundSession(session, now);
                SubscriptionsChanged?.Invoke();
                if (!sourceHandles.TryGetValue(sourceKey, out var handle))
                {
                    return false;
                }

                var input = new NativeSamplingSubscriptionRemoveInput
                {
                    AbiVersion = NativeSamplingSubscriptionAbi.Version,
                    StructSize = SizeOf<NativeSamplingSubscriptionRemoveInput>(),
                    ConfigurationGeneration = session?.ConfigurationGeneration ?? 0,
                    OperationEpoch = NextOperationEpoch(),
                    CommandAtMilliseconds = CommandTimestamp(now),
                    SourceHandle = handle,
                    ValidMask = (ulong)NativeSamplingSubscriptionRemoveValidity.Required
                };
                var status = session?.Remove(in input) ?? NativeSamplingSubscriptionStatus.Ok;
                if (status != NativeSamplingSubscriptionStatus.Ok)
                {
                    throw new InvalidOperationException(
                        $"Native sampling subscription remove failed with {status}.");
                }

                if (sourcePayloads.Remove(handle))
                {
                    latestMergedItemIdsDirty = true;
                }
                sourceHandles.Remove(sourceKey);
                RefreshActivity();
                ReleaseUnusedItemMappings();
                return true;
            }
            finally
            {
                lease?.Dispose();
            }
        }
    }

    public NativeItemSamplingSubscriptionPlan<TRequest, TContext> CreatePlan(DateTimeOffset now)
    {
        lock (gate)
        {
            if (pendingPlanEpoch != 0)
            {
                throw new InvalidOperationException(
                    "The prior native sampling plan must be completed before another plan is requested.");
            }

            if (outputClosing)
                throw new ObjectDisposedException(nameof(NativeItemSamplingSubscriptionTracker<TRequest, TContext>));
            if (outputUse is not null)
                throw new InvalidOperationException("The prior sampling output use could not be returned; close must be retried.");
            var lease = owner.Acquire(roleId);
            var retainLease = false;
            var completionAttempted = false;
            var outputReleaseAttempted = false;
            NativeSamplingOutputView outputs = default;
            outputOperationActive = true;
            try
            {
                var session = lease.Session;
                lastRoleSettings = lease.HotPublish;
                EnsureBoundSession(session, now);
                outputs = BorrowPlanOutputs(session.Capacity);
                var dueBuffer = outputs.DueItems;
                var sourceBuffer = outputs.Sources;
                var expiredBuffer = outputs.Expired;
                var triggerBuffer = outputs.Triggers;
                var currentPlanEpoch = NextPlanEpoch();
                var commandTimestamp = CommandTimestamp(now);
                var header = new NativeSamplingSubscriptionPlanHeader
                {
                    AbiVersion = NativeSamplingSubscriptionAbi.Version,
                    StructSize = SizeOf<NativeSamplingSubscriptionPlanHeader>(),
                    ConfigurationGeneration = session.ConfigurationGeneration,
                    OperationEpoch = NextOperationEpoch(),
                    PlanEpoch = currentPlanEpoch,
                    CommandAtMilliseconds = commandTimestamp,
                    ValidMask = (ulong)NativeSamplingSubscriptionPlanValidity.Required,
                    DueItemCapacity = checked((uint)dueBuffer.Length),
                    SourceViewCapacity = checked((uint)sourceBuffer.Length),
                    ExpiredSourceCapacity = checked((uint)expiredBuffer.Length),
                    TriggerCapacity = checked((uint)triggerBuffer.Length),
                    RequestFlags = 0
                };
                var status = session.Plan(ref header, dueBuffer, sourceBuffer, expiredBuffer, triggerBuffer);
                if (status != NativeSamplingSubscriptionStatus.Ok)
                {
                    throw new InvalidOperationException(
                        $"Native sampling subscription plan failed with {status}.");
                }

                pendingItems = new NativeSamplingSubscriptionItemReference[checked((int)header.DueItemCount)];
                for (var index = 0; index < header.DueItemCount; index++)
                {
                    pendingItems[index] = new NativeSamplingSubscriptionItemReference
                    {
                        StructSize = SizeOf<NativeSamplingSubscriptionItemReference>(),
                        ItemHandle = dueBuffer[index].ItemHandle
                    };
                }
                pendingTriggers = triggerBuffer.Slice(0, checked((int)header.TriggerCount)).ToArray();
                pendingPlanEpoch = pendingItems.Length != 0 || pendingTriggers.Length != 0
                    ? currentPlanEpoch
                    : 0;

                for (var index = 0; index < header.ExpiredSourceCount; index++)
                {
                    var expiredHandle = expiredBuffer[index].SourceHandle;
                    if (sourcePayloads.Remove(expiredHandle, out var expiredPayload))
                    {
                        sourceHandles.Remove(expiredPayload.SourceKey);
                        latestMergedItemIdsDirty = true;
                    }
                }
                ReleaseUnusedItemMappings();
                if (header.ExpiredSourceCount != 0) RefreshActivity();

                var active = (header.Flags & (ulong)NativeSamplingSubscriptionPlanFlags.Active) != 0;
                var triggers = ProjectTriggers(header, triggerBuffer);
                var schedule = CreatePlanSchedule(
                    in header,
                    now,
                    commandTimestamp,
                    lease.WorkspaceIncarnation,
                    lease.HotPublish, dueBuffer, triggerBuffer);
                var delay = DelayUntil(now, schedule.NextWakeAt);
                var views = new NativeItemSamplingSubscriptionSourceView<TRequest>[
                    checked((int)header.SourceViewCount)];
                for (var index = 0; index < header.SourceViewCount; index++)
                {
                    var nativeView = sourceBuffer[index];
                    if (!sourcePayloads.TryGetValue(nativeView.SourceHandle, out var payload))
                    {
                        throw new InvalidOperationException(
                            "Native sampling subscription returned a source without an active payload.");
                    }
                    views[index] = new NativeItemSamplingSubscriptionSourceView<TRequest>(
                        payload.Request,
                        payload.ItemIds,
                        payload.LastSeen,
                        TimeSpan.FromMilliseconds(
                            checked((long)nativeView.EffectiveIntervalMilliseconds)));
                }

                var activeItemIds = GetLatestMergedItemIds(views);
                var dueItemIds = new string[checked((int)header.DueItemCount)];
                for (var index = 0; index < header.DueItemCount; index++)
                {
                    var handle = pendingItems[index].ItemHandle;
                    if (!itemIds.TryGetValue(handle, out var itemId))
                    {
                        throw new InvalidOperationException(
                            "Native sampling subscription returned an unknown item handle.");
                    }
                    dueItemIds[index] = itemId;
                }

                if (header.DueItemCount == 0 && header.TriggerCount == 0)
                {
                    return FinishOutputPlan(CreatePlanResult(
                        active, default, [], triggers, delay, schedule,
                        lease.OwnerToken, activeItemIds, views), ref outputs, ref outputReleaseAttempted);
                }

                TRequest? request = default;
                if (header.DueItemCount > 0)
                {
                    var requestItemIds = coalesceItemsIntoLatestCapture
                        ? activeItemIds
                        : dueItemIds;
                    request = createRequest(requestItemIds, views);
                }

                if (request is not null)
                {
                    var result = FinishOutputPlan(CreatePlanResult(
                        active, request, dueItemIds, triggers, delay, schedule,
                        lease.OwnerToken, activeItemIds, views), ref outputs, ref outputReleaseAttempted);
                    // Complete/endUse must succeed before the plan/session lease is delivered.
                    pendingRequest = request;
                    pendingSessionLease = lease;
                    retainLease = true;
                    return result;
                }

                if (header.TriggerCount != 0)
                {
                    var result = FinishOutputPlan(CreatePlanResult(
                        active, default, dueItemIds, triggers, delay, schedule,
                        lease.OwnerToken, activeItemIds, views), ref outputs, ref outputReleaseAttempted);
                    pendingSessionLease = lease;
                    retainLease = true;
                    return result;
                }

                if (header.DueItemCount == 0)
                {
                    return FinishOutputPlan(CreatePlanResult(
                        active, default, [], triggers, delay, schedule,
                        lease.OwnerToken, activeItemIds, views), ref outputs, ref outputReleaseAttempted);
                }

                completionAttempted = true;
                var completion = CompletePendingWithSession(
                    session,
                    lease.WorkspaceIncarnation,
                    lease.HotPublish,
                    now,
                    NativeSamplingSubscriptionCompletionStatus.Skipped);
                return FinishOutputPlan(CreatePlanResult(
                    completion.Schedule.IsActive, default, [], triggers,
                    completion.Delay, completion.Schedule, lease.OwnerToken,
                    activeItemIds, views), ref outputs, ref outputReleaseAttempted);
            }
            catch (Exception error)
            {
                Exception? settlementError = null;
                if (pendingPlanEpoch != 0 && !completionAttempted)
                {
                    try
                    {
                        _ = CompletePendingWithSession(
                            lease.Session,
                            lease.WorkspaceIncarnation,
                            lease.HotPublish,
                            now,
                            NativeSamplingSubscriptionCompletionStatus.Failed);
                        pendingSessionLease = null;
                    }
                    catch (Exception completionError)
                    {
                        settlementError = completionError;
                    }
                }
                pendingRequest = default;
                pendingItems = [];
                pendingTriggers = [];
                pendingPlanEpoch = 0;
                if (settlementError is not null)
                {
                    throw new AggregateException(
                        "Native sampling plan projection failed and the plan could not be settled.",
                        error,
                        settlementError);
                }
                throw;
            }
            finally
            {
                try
                {
                    if (!outputReleaseAttempted)
                    {
                        outputs.Dispose();
                        EndOutputUse(); // No Complete on a failed Native Plan/projection.
                    }
                }
                finally
                {
                    outputOperationActive = false;
                    if (!retainLease) lease.Dispose();
                }
            }
        }
    }

    private ImmutableArray<NativeItemSamplingSubscriptionTrigger<TRequest>> ProjectTriggers(
        in NativeSamplingSubscriptionPlanHeader header,
        ReadOnlySpan<NativeSamplingSubscriptionTrigger> triggerBuffer)
    {
        var result = ImmutableArray.CreateBuilder<NativeItemSamplingSubscriptionTrigger<TRequest>>(
            checked((int)header.TriggerCount));
        for (var index = 0; index < header.TriggerCount; index++)
        {
            var native = triggerBuffer[index];
            if (native.StructSize != SizeOf<NativeSamplingSubscriptionTrigger>()
                || (native.Flags & ~(uint)NativeSamplingSubscriptionTriggerFlags.Known) != 0
                || native.SourceHandle == 0
                || native.ItemHandle == 0
                || native.ScheduledDueMilliseconds == 0
                || native.Sequence == 0
                || native.PlanEpoch != header.PlanEpoch
                || !sourcePayloads.TryGetValue(native.SourceHandle, out var payload)
                || !itemIds.TryGetValue(native.ItemHandle, out var itemId))
            {
                throw new InvalidOperationException(
                    "Native sampling subscription returned an invalid trigger identity.");
            }

            result.Add(new NativeItemSamplingSubscriptionTrigger<TRequest>(
                payload.SourceKey,
                itemId,
                native.ScheduledDueMilliseconds,
                native.Sequence,
                payload.Request,
                (native.Flags & (uint)NativeSamplingSubscriptionTriggerFlags.Fresh) != 0));
        }
        return result.MoveToImmutable();
    }

    private static NativeItemSamplingSubscriptionPlan<TRequest, TContext> CreatePlanResult(
        bool active,
        TRequest? request,
        IReadOnlyList<string> dueItemIds,
        ImmutableArray<NativeItemSamplingSubscriptionTrigger<TRequest>> triggers,
        TimeSpan delay,
        NativeItemSamplingSubscriptionScheduleReceipt schedule,
        TContext ownerToken,
        IReadOnlyList<string> activeItemIds,
        IReadOnlyList<NativeItemSamplingSubscriptionSourceView<TRequest>> sourceViews)
        => new(active, request, dueItemIds, delay, schedule, ownerToken)
        {
            ActiveItemIds = activeItemIds,
            SourceViews = sourceViews,
            Triggers = triggers
        };

    public NativeItemSamplingSubscriptionCompletion MarkSampled(
        TRequest request,
        DateTimeOffset now)
        => Complete(request, now, NativeSamplingSubscriptionCompletionStatus.Sampled);

    public NativeItemSamplingSubscriptionCompletion MarkFailed(
        TRequest request,
        DateTimeOffset now)
        => Complete(request, now, NativeSamplingSubscriptionCompletionStatus.Failed);

    public NativeItemSamplingSubscriptionCompletion MarkSkipped(
        TRequest request,
        DateTimeOffset now)
        => Complete(request, now, NativeSamplingSubscriptionCompletionStatus.Skipped);

    internal (int SourceHandleCount, int SourcePayloadCount)
        CaptureManagedSourceState()
    {
        lock (gate)
        {
            return (sourceHandles.Count, sourcePayloads.Count);
        }
    }

    public bool HasPersistentDemand(Func<TRequest, bool> matches)
    {
        lock (gate)
        {
            return sourcePayloads.Values.Any(payload => payload.Persistent && matches(payload.Request));
        }
    }

    public void Clear(DateTimeOffset now)
    {
        lock (gate)
        {
            if (pendingPlanEpoch != 0)
            {
                throw new InvalidOperationException(
                    "The pending native sampling plan must be completed before reset.");
            }
            using var lease = owner.TryAcquireForCleanup(roleId);
            if (lease is not null)
            {
                var session = lease.Session;
                var input = new NativeSamplingSubscriptionControlInput
                {
                    AbiVersion = NativeSamplingSubscriptionAbi.Version,
                    StructSize = SizeOf<NativeSamplingSubscriptionControlInput>(),
                    ConfigurationGeneration = session.ConfigurationGeneration,
                    OperationEpoch = NextOperationEpoch(),
                    CommandAtMilliseconds = CommandTimestamp(now),
                    ValidMask = (ulong)NativeSamplingSubscriptionControlValidity.Required
                };
                var resetStatus = session.Reset(in input);
                if (resetStatus == NativeSamplingSubscriptionStatus.Ok)
                {
                    boundSession = session;
                }
                else
                {
                    throw new InvalidOperationException(
                        $"Native sampling subscription reset failed with {resetStatus}.");
                }
            }

            sourceHandles.Clear();
            itemHandles.Clear();
            itemIds.Clear();
            sourcePayloads.Clear();
            latestMergedItemIds = [];
            latestMergedItemIdsDirty = true;
            pendingSessionLease?.Dispose();
            pendingSessionLease = null;
            pendingRequest = default;
            pendingItems = [];
            pendingTriggers = [];
            pendingPlanEpoch = 0;
            nextHandle = 0;
            RefreshActivity();
        }
    }

    private void TrackCore(
        TRequest request,
        DateTimeOffset now,
        bool persistent,
        TimeSpan? explicitInterval,
        NativeSamplingSubscriptionSourceFlags samplingFlags,
        string? explicitSourceKey = null)
    {
        const NativeSamplingSubscriptionSourceFlags supportedSamplingFlags =
            NativeSamplingSubscriptionSourceFlags.FreshOnTrigger
            | NativeSamplingSubscriptionSourceFlags.AlignSameItemPhase
            | NativeSamplingSubscriptionSourceFlags.PeriodicTriggers;
        if ((samplingFlags & ~supportedSamplingFlags) != 0
            || ((samplingFlags & (NativeSamplingSubscriptionSourceFlags.FreshOnTrigger
                | NativeSamplingSubscriptionSourceFlags.AlignSameItemPhase)) != 0
                && (samplingFlags & NativeSamplingSubscriptionSourceFlags.PeriodicTriggers) == 0))
        {
            throw new ArgumentOutOfRangeException(
                nameof(samplingFlags),
                "Fresh and phase-aligned subscriptions require periodic trigger scheduling.");
        }
        if (explicitInterval is not null
            && explicitInterval.Value < TimeSpan.FromMilliseconds(1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(explicitInterval),
                "An explicit sampling interval must be at least one millisecond.");
        }

        var normalizedItems = NormalizeItemIds(getItemIds(request));
        if (normalizedItems.Count == 0)
        {
            return;
        }
        var sourceKey = explicitSourceKey ?? createSourceKey(request);
        if (string.IsNullOrWhiteSpace(sourceKey))
        {
            throw new InvalidOperationException("Sampling source key must not be empty.");
        }

        lock (gate)
        {
            var lease = AcquireMutationLease();
            using var ownedLease = lease;
            lastRoleSettings = lease?.HotPublish ?? pendingSessionLease?.HotPublish ?? lastRoleSettings;
            var session = pendingSessionLease?.Session ?? lease?.Session;
            if (session is not null) EnsureBoundSession(session, now);
            SubscriptionsChanged?.Invoke();
            var sourceHandle = sourceHandles.GetValueOrDefault(sourceKey);
            var newSource = sourceHandle == 0;
            if (newSource)
            {
                sourceHandle = NextHandle();
            }

            var nativeItemIds = coalesceItemsIntoLatestCapture
                ? [CoalescedCaptureItemId]
                : normalizedItems;
            var nativeItems = new NativeSamplingSubscriptionItemReference[nativeItemIds.Count];
            var newItemMappings = new List<(string ItemId, ulong Handle)>();
            for (var index = 0; index < nativeItemIds.Count; index++)
            {
                var itemId = nativeItemIds[index];
                var itemHandle = itemHandles.GetValueOrDefault(itemId);
                if (itemHandle == 0)
                {
                    itemHandle = NextHandle();
                    newItemMappings.Add((itemId, itemHandle));
                }
                nativeItems[index] = new NativeSamplingSubscriptionItemReference
                {
                    StructSize = SizeOf<NativeSamplingSubscriptionItemReference>(),
                    ItemHandle = itemHandle
                };
            }

            var flags = persistent ? NativeSamplingSubscriptionSourceFlags.Persistent : 0;
            flags |= samplingFlags;
            ulong validMask = (ulong)NativeSamplingSubscriptionTrackValidity.Required;
            ulong explicitIntervalMilliseconds = 0;
            if (explicitInterval is not null)
            {
                flags |= NativeSamplingSubscriptionSourceFlags.ExplicitInterval;
                validMask |= (ulong)NativeSamplingSubscriptionTrackValidity.ExplicitInterval;
                explicitIntervalMilliseconds = checked((ulong)Math.Ceiling(
                    explicitInterval.Value.TotalMilliseconds));
            }
            var commandTimestamp = CommandTimestamp(now);
            var input = new NativeSamplingSubscriptionTrackInput
            {
                AbiVersion = NativeSamplingSubscriptionAbi.Version,
                StructSize = SizeOf<NativeSamplingSubscriptionTrackInput>(),
                ConfigurationGeneration = session?.ConfigurationGeneration ?? 0,
                OperationEpoch = NextOperationEpoch(),
                CommandAtMilliseconds = commandTimestamp,
                ObservedAtMilliseconds = commandTimestamp,
                SourceHandle = sourceHandle,
                ExplicitIntervalMilliseconds = explicitIntervalMilliseconds,
                ValidMask = validMask,
                ItemCount = checked((uint)nativeItems.Length),
                Flags = (uint)flags
            };
            var status = session?.Track(in input, nativeItems) ?? NativeSamplingSubscriptionStatus.Ok;
            ThrowIfCapacityExceeded(status);
            if (status != NativeSamplingSubscriptionStatus.Ok)
            {
                throw new InvalidOperationException(
                    $"Native sampling subscription track failed with {status}.");
            }

            if (newSource)
            {
                sourceHandles.Add(sourceKey, sourceHandle);
            }
            foreach (var item in newItemMappings)
            {
                itemHandles.Add(item.ItemId, item.Handle);
                itemIds.Add(item.Handle, item.ItemId);
            }
            sourcePayloads[sourceHandle] = new SourcePayload(
                request,
                sourceKey,
                normalizedItems,
                now,
                commandTimestamp,
                persistent,
                explicitInterval,
                samplingFlags);
            ReleaseUnusedItemMappings();
            latestMergedItemIdsDirty = true;
            RefreshActivity();
        }
    }

    private void ReleaseUnusedItemMappings()
    {
        var retained = new HashSet<ulong>(pendingItems.Select(static item => item.ItemHandle));
        retained.UnionWith(pendingTriggers.Select(static trigger => trigger.ItemHandle));
        foreach (var payload in sourcePayloads.Values)
        {
            if (coalesceItemsIntoLatestCapture)
                retained.Add(itemHandles[CoalescedCaptureItemId]);
            else
                foreach (var itemId in payload.ItemIds) retained.Add(itemHandles[itemId]);
        }
        foreach (var (handle, itemId) in itemIds.ToArray())
        {
            if (retained.Contains(handle)) continue;
            itemIds.Remove(handle);
            itemHandles.Remove(itemId);
        }
    }

    private ISamplingSubscriptionSessionLease<TContext>? AcquireMutationLease()
        => pendingSessionLease is null
            ? owner.TryAcquireForCleanup(roleId)
            : null;

    internal NativeItemSamplingSubscriptionCompletion Complete(
        TRequest request,
        DateTimeOffset now,
        NativeSamplingSubscriptionCompletionStatus status)
    {
        lock (gate)
        {
            if (pendingPlanEpoch == 0)
            {
                throw new InvalidOperationException("There is no native sampling plan awaiting completion.");
            }
            if (!EqualityComparer<TRequest>.Default.Equals(request, pendingRequest))
            {
                throw new InvalidOperationException(
                    "Sampling completion request does not represent the pending native plan.");
            }
            var lease = pendingSessionLease
                ?? throw new InvalidOperationException(
                    "The pending native sampling plan has no retained session lease.");
            try
            {
                return CompletePendingWithSession(
                    lease.Session,
                    lease.WorkspaceIncarnation,
                    lease.HotPublish,
                    now,
                    status);
            }
            finally
            {
                pendingSessionLease = null;
                pendingRequest = default;
                pendingItems = [];
                pendingTriggers = [];
                pendingPlanEpoch = 0;
                lease.Dispose();
            }
        }
    }

    internal NativeItemSamplingSubscriptionCompletion CompletePlan(
        NativeItemSamplingSubscriptionPlan<TRequest, TContext> plan,
        DateTimeOffset now,
        NativeSamplingSubscriptionCompletionStatus status)
    {
        ArgumentNullException.ThrowIfNull(plan);
        lock (gate)
        {
            if (pendingPlanEpoch == 0 || plan.Schedule.PlanEpoch != pendingPlanEpoch)
            {
                throw new InvalidOperationException(
                    "Sampling completion does not represent the pending native plan.");
            }
            if (plan.Request is not null
                && !EqualityComparer<TRequest>.Default.Equals(plan.Request, pendingRequest))
            {
                throw new InvalidOperationException(
                    "Sampling completion request does not represent the pending native plan.");
            }
            var lease = pendingSessionLease
                ?? throw new InvalidOperationException(
                    "The pending native sampling plan has no retained session lease.");
            try
            {
                return CompletePendingWithSession(
                    lease.Session,
                    lease.WorkspaceIncarnation,
                    lease.HotPublish,
                    now,
                    status);
            }
            finally
            {
                pendingSessionLease = null;
                pendingRequest = default;
                pendingItems = [];
                pendingTriggers = [];
                pendingPlanEpoch = 0;
                lease.Dispose();
            }
        }
    }

    private void EnsureBoundSession(
        NativeSamplingSubscriptionSession session,
        DateTimeOffset commandAt)
    {
        if (ReferenceEquals(boundSession, session))
        {
            return;
        }
        if (pendingPlanEpoch != 0)
        {
            throw new InvalidOperationException(
                "A native sampling workspace changed without retaining the pending plan session.");
        }

        if (!ReferenceEquals(replaySession, session))
        {
            replaySession = session;
            replayOperationEpoch = 0;
        }
        var resetInput = new NativeSamplingSubscriptionControlInput
        {
            AbiVersion = NativeSamplingSubscriptionAbi.Version,
            StructSize = SizeOf<NativeSamplingSubscriptionControlInput>(),
            ConfigurationGeneration = session.ConfigurationGeneration,
            OperationEpoch = NextReplayOperationEpoch(),
            CommandAtMilliseconds = CommandTimestamp(commandAt),
            ValidMask = (ulong)NativeSamplingSubscriptionControlValidity.Required
        };
        var resetStatus = session.Reset(in resetInput);
        if (resetStatus != NativeSamplingSubscriptionStatus.Ok)
        {
            throw new InvalidOperationException(
                $"Native sampling subscription incarnation reset failed with {resetStatus}.");
        }

        var priorPayloadCount = sourcePayloads.Count;
        var replayAt = CommandTimestamp(commandAt);
        var roleSettings = CurrentSettings()
            ?? throw new InvalidOperationException("The subscription role settings have not been published.");
        var priorPayloads = sourcePayloads.Values
            .Where(payload => IsInUse(payload, replayAt, roleSettings))
            .OrderBy(static payload => payload.SourceKey, StringComparer.Ordinal)
            .ToArray();
        var nextSourceHandles = new Dictionary<string, ulong>(StringComparer.Ordinal);
        var nextItemHandles = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
        var nextItemIds = new Dictionary<ulong, string>();
        var nextSourcePayloads = new Dictionary<ulong, SourcePayload>();
        ulong nextIncarnationHandle = 0;
        foreach (var payload in priorPayloads)
        {
            var sourceHandle = NextIncarnationHandle(ref nextIncarnationHandle);
            var replayItemIds = coalesceItemsIntoLatestCapture
                ? [CoalescedCaptureItemId]
                : payload.ItemIds;
            var nativeItems = replayItemIds
                .Select(item =>
                {
                    if (!nextItemHandles.TryGetValue(item, out var itemHandle))
                    {
                        itemHandle = NextIncarnationHandle(ref nextIncarnationHandle);
                        nextItemHandles.Add(item, itemHandle);
                        nextItemIds.Add(itemHandle, item);
                    }
                    return new NativeSamplingSubscriptionItemReference
                    {
                        StructSize = SizeOf<NativeSamplingSubscriptionItemReference>(),
                        ItemHandle = itemHandle
                    };
                })
                .ToArray();
            var flags = payload.Persistent
                ? NativeSamplingSubscriptionSourceFlags.Persistent
                : NativeSamplingSubscriptionSourceFlags.None;
            flags |= payload.SamplingFlags;
            ulong validMask = (ulong)NativeSamplingSubscriptionTrackValidity.Required;
            ulong explicitIntervalMilliseconds = 0;
            if (payload.ExplicitInterval is not null)
            {
                flags |= NativeSamplingSubscriptionSourceFlags.ExplicitInterval;
                validMask |= (ulong)NativeSamplingSubscriptionTrackValidity.ExplicitInterval;
                explicitIntervalMilliseconds = checked(
                    (ulong)Math.Ceiling(
                        payload.ExplicitInterval.Value.TotalMilliseconds));
            }
            var input = new NativeSamplingSubscriptionTrackInput
            {
                AbiVersion = NativeSamplingSubscriptionAbi.Version,
                StructSize = SizeOf<NativeSamplingSubscriptionTrackInput>(),
                ConfigurationGeneration = session.ConfigurationGeneration,
                OperationEpoch = NextReplayOperationEpoch(),
                CommandAtMilliseconds = CommandTimestamp(commandAt),
                ObservedAtMilliseconds = payload.NativeObservedAtMilliseconds,
                SourceHandle = sourceHandle,
                ExplicitIntervalMilliseconds = explicitIntervalMilliseconds,
                ValidMask = validMask,
                ItemCount = checked((uint)nativeItems.Length),
                Flags = (uint)flags
            };
            var status = session.Track(in input, nativeItems);
            ThrowIfCapacityExceeded(status);
            if (status != NativeSamplingSubscriptionStatus.Ok)
            {
                throw new InvalidOperationException(
                    $"Native sampling subscription replay failed with {status}.");
            }
            nextSourceHandles.Add(payload.SourceKey, sourceHandle);
            nextSourcePayloads.Add(sourceHandle, payload);
        }

        sourceHandles.Clear();
        foreach (var mapping in nextSourceHandles)
        {
            sourceHandles.Add(mapping.Key, mapping.Value);
        }
        itemHandles.Clear();
        foreach (var mapping in nextItemHandles)
        {
            itemHandles.Add(mapping.Key, mapping.Value);
        }
        itemIds.Clear();
        foreach (var mapping in nextItemIds)
        {
            itemIds.Add(mapping.Key, mapping.Value);
        }
        sourcePayloads.Clear();
        foreach (var mapping in nextSourcePayloads)
        {
            sourcePayloads.Add(mapping.Key, mapping.Value);
        }
        latestMergedItemIdsDirty = true;
        nextHandle = nextIncarnationHandle;
        operationEpoch = replayOperationEpoch;
        planEpoch = 0;
        boundSession = session;
        if (priorPayloadCount != sourcePayloads.Count) RefreshActivity();
    }

    private NativeItemSamplingSubscriptionCompletion CompletePendingWithSession(
        NativeSamplingSubscriptionSession session,
        ulong workspaceIncarnation,
        ISubscriptionRoleSettings hotPublish,
        DateTimeOffset now,
        NativeSamplingSubscriptionCompletionStatus status)
    {
        var timestamp = CommandTimestamp(now);
        var completedPlanEpoch = pendingPlanEpoch;
        var input = new NativeSamplingSubscriptionCompletionInput
        {
            AbiVersion = NativeSamplingSubscriptionAbi.Version,
            StructSize = SizeOf<NativeSamplingSubscriptionCompletionInput>(),
            ConfigurationGeneration = session.ConfigurationGeneration,
            OperationEpoch = NextOperationEpoch(),
            PlanEpoch = pendingPlanEpoch,
            CommandAtMilliseconds = timestamp,
            CompletedAtMilliseconds = timestamp,
            ValidMask = (ulong)NativeSamplingSubscriptionCompletionValidity.Required,
            ItemCount = checked((uint)pendingItems.Length),
            TriggerCount = checked((uint)pendingTriggers.Length),
            Status = (uint)status
        };
        var nativeStatus = session.Complete(in input, pendingItems, pendingTriggers, out var output);
        if (nativeStatus != NativeSamplingSubscriptionStatus.Ok)
        {
            throw new InvalidOperationException(
                $"Native sampling subscription completion failed with {nativeStatus}.");
        }
        pendingItems = [];
        pendingTriggers = [];
        pendingPlanEpoch = 0;
        ReleaseUnusedItemMappings();
        var schedule = CreateCompletionSchedule(
            in output,
            completedPlanEpoch,
            now,
            timestamp,
            workspaceIncarnation,
            hotPublish);
        return new NativeItemSamplingSubscriptionCompletion(
            DelayUntil(now, schedule.NextWakeAt),
            schedule);
    }

    private NativeItemSamplingSubscriptionScheduleReceipt CreatePlanSchedule(
        in NativeSamplingSubscriptionPlanHeader header,
        DateTimeOffset commandAt,
        ulong commandTimestamp,
        ulong workspaceIncarnation,
        ISubscriptionRoleSettings hotPublish,
        ReadOnlySpan<NativeSamplingSubscriptionDueItem> dueBuffer,
        ReadOnlySpan<NativeSamplingSubscriptionTrigger> triggerBuffer)
    {
        DateTimeOffset? earliestScheduledDueAt = null;
        for (var index = 0; index < header.DueItemCount; index++)
        {
            var dueAt = ProjectNativeTimestamp(
                commandAt,
                commandTimestamp,
                dueBuffer[index].ScheduledDueMilliseconds);
            if (earliestScheduledDueAt is null || dueAt < earliestScheduledDueAt.Value)
            {
                earliestScheduledDueAt = dueAt;
            }
        }
        for (var index = 0; index < header.TriggerCount; index++)
        {
            var dueAt = ProjectNativeTimestamp(
                commandAt,
                commandTimestamp,
                triggerBuffer[index].ScheduledDueMilliseconds);
            if (earliestScheduledDueAt is null || dueAt < earliestScheduledDueAt.Value)
            {
                earliestScheduledDueAt = dueAt;
            }
        }

        DateTimeOffset? nextWakeAt = (header.Flags & (ulong)NativeSamplingSubscriptionPlanFlags.NextWakeValid) != 0
            ? ProjectNativeTimestamp(
                commandAt,
                commandTimestamp,
                header.NextWakeMilliseconds)
            : null;
        return new NativeItemSamplingSubscriptionScheduleReceipt(
            NativeItemSamplingSubscriptionScheduleOrigin.Plan,
            workspaceIncarnation,
            header.ConfigurationGeneration,
            header.StateRevision,
            header.PlanEpoch,
            commandAt,
            TimeSpan.FromMilliseconds(hotPublish.DefaultIntervalMilliseconds),
            TimeSpan.FromMilliseconds(hotPublish.FreshnessGraceMilliseconds),
            nextWakeAt,
            earliestScheduledDueAt,
            checked((int)header.ActiveSourceCount),
            checked((int)header.ActiveItemCount),
            checked((int)header.DueItemCount),
            checked((int)header.ExpiredSourceCount));
    }

    private static NativeItemSamplingSubscriptionScheduleReceipt CreateCompletionSchedule(
        in NativeSamplingSubscriptionCompletionOutput output,
        ulong planEpoch,
        DateTimeOffset commandAt,
        ulong commandTimestamp,
        ulong workspaceIncarnation,
        ISubscriptionRoleSettings hotPublish)
    {
        DateTimeOffset? nextWakeAt = (output.Flags & (ulong)NativeSamplingSubscriptionCompletionFlags.NextWakeValid) != 0
            ? ProjectNativeTimestamp(
                commandAt,
                commandTimestamp,
                output.NextWakeMilliseconds)
            : null;
        return new NativeItemSamplingSubscriptionScheduleReceipt(
            NativeItemSamplingSubscriptionScheduleOrigin.Completion,
            workspaceIncarnation,
            output.ConfigurationGeneration,
            output.StateRevision,
            planEpoch,
            commandAt,
            TimeSpan.FromMilliseconds(hotPublish.DefaultIntervalMilliseconds),
            TimeSpan.FromMilliseconds(hotPublish.FreshnessGraceMilliseconds),
            nextWakeAt,
            null,
            ProjectNativeCount(output.ActiveSourceCount),
            ProjectNativeCount(output.ActiveItemCount),
            0,
            0);
    }

    private static DateTimeOffset ProjectNativeTimestamp(
        DateTimeOffset commandAt,
        ulong commandTimestamp,
        ulong nativeTimestamp)
    {
        if (nativeTimestamp >= commandTimestamp)
        {
            var delta = nativeTimestamp - commandTimestamp;
            var maximum = checked((ulong)(
                (DateTimeOffset.MaxValue.UtcTicks - commandAt.UtcTicks)
                / TimeSpan.TicksPerMillisecond));
            return delta >= maximum
                ? DateTimeOffset.MaxValue
                : commandAt.AddTicks(
                    checked((long)delta) * TimeSpan.TicksPerMillisecond);
        }

        var negativeDelta = commandTimestamp - nativeTimestamp;
        var minimum = checked((ulong)(
            (commandAt.UtcTicks - DateTimeOffset.MinValue.UtcTicks)
            / TimeSpan.TicksPerMillisecond));
        return negativeDelta >= minimum
            ? DateTimeOffset.MinValue
            : commandAt.AddTicks(
                -checked((long)negativeDelta) * TimeSpan.TicksPerMillisecond);
    }

    private static int ProjectNativeCount(uint value)
        => value > int.MaxValue ? int.MaxValue : (int)value;

    private static TimeSpan DelayUntil(
        DateTimeOffset now,
        DateTimeOffset? nextWakeAt)
    {
        if (nextWakeAt is null || nextWakeAt <= now)
        {
            return TimeSpan.Zero;
        }
        return nextWakeAt.Value - now;
    }

    private static void ThrowIfCapacityExceeded(NativeSamplingSubscriptionStatus status)
    {
        if (status is NativeSamplingSubscriptionStatus.ItemCapacityExceeded
            or NativeSamplingSubscriptionStatus.SourceCapacityExceeded
            or NativeSamplingSubscriptionStatus.MembershipCapacityExceeded)
            throw new SamplingSubscriptionCapacityException(status);
    }

    private void EnsureBuffers(NativeSamplingSubscriptionCapacity capacity)
    {
        if (dueBuffer.Length != capacity.DueItemCapacity)
        {
            dueBuffer = new NativeSamplingSubscriptionDueItem[checked((int)capacity.DueItemCapacity)];
        }
        if (sourceBuffer.Length != capacity.SourceViewCapacity)
        {
            sourceBuffer = new NativeSamplingSubscriptionSourceView[checked((int)capacity.SourceViewCapacity)];
        }
        if (expiredBuffer.Length != capacity.ExpiredSourceCapacity)
        {
            expiredBuffer = new NativeSamplingSubscriptionExpiredSource[
                checked((int)capacity.ExpiredSourceCapacity)];
        }
        if (triggerBuffer.Length != capacity.TriggerCapacity)
        {
            triggerBuffer = new NativeSamplingSubscriptionTrigger[checked((int)capacity.TriggerCapacity)];
        }
    }

    private ulong NextHandle()
    {
        if (nextHandle == ulong.MaxValue)
        {
            throw new InvalidOperationException("Sampling payload handle identity is exhausted.");
        }
        return ++nextHandle;
    }

    private static ulong NextIncarnationHandle(ref ulong value)
    {
        if (value == ulong.MaxValue)
        {
            throw new InvalidOperationException("Sampling incarnation handle identity is exhausted.");
        }
        return ++value;
    }

    private ulong NextReplayOperationEpoch()
    {
        if (replayOperationEpoch == ulong.MaxValue)
        {
            throw new InvalidOperationException("Sampling replay operation epoch is exhausted.");
        }
        return ++replayOperationEpoch;
    }

    private ulong NextOperationEpoch()
    {
        if (operationEpoch == ulong.MaxValue)
        {
            throw new InvalidOperationException("Sampling operation epoch is exhausted.");
        }
        return ++operationEpoch;
    }

    private ulong NextPlanEpoch()
    {
        if (planEpoch == ulong.MaxValue)
        {
            throw new InvalidOperationException("Sampling plan epoch is exhausted.");
        }
        return ++planEpoch;
    }

    private ulong CommandTimestamp(DateTimeOffset value)
    {
        var raw = RawTimestamp(value);
        var currentTimestamp = clock.GetTimestamp();
        if (lastCommandMilliseconds == 0)
        {
            lastCommandMilliseconds = raw;
            lastCommandTimestamp = currentTimestamp;
            return raw;
        }

        var elapsed = clock.GetElapsedTime(
            lastCommandTimestamp,
            currentTimestamp);
        var elapsedMilliseconds = checked((ulong)(
            elapsed.Ticks / TimeSpan.TicksPerMillisecond));
        var monotonicCandidate = checked(
            lastCommandMilliseconds + elapsedMilliseconds);
        lastCommandMilliseconds = Math.Max(raw, monotonicCandidate);
        lastCommandTimestamp = currentTimestamp;
        return lastCommandMilliseconds;
    }

    private static ulong RawTimestamp(DateTimeOffset value)
    {
        var milliseconds = value.ToUniversalTime().ToUnixTimeMilliseconds();
        return milliseconds > 0
            ? checked((ulong)milliseconds)
            : throw new ArgumentOutOfRangeException(nameof(value));
    }

    private static IReadOnlyList<string> NormalizeItemIds(IEnumerable<string> values)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalized = new List<string>();
        foreach (var value in values)
        {
            var itemId = value.Trim();
            if (itemId.Length != 0 && seen.Add(itemId))
            {
                normalized.Add(itemId);
            }
        }
        normalized.Sort(StringComparer.OrdinalIgnoreCase);
        return normalized.ToArray();
    }

    private IReadOnlyList<string> GetLatestMergedItemIds(
        IReadOnlyList<NativeItemSamplingSubscriptionSourceView<TRequest>> views)
    {
        if (!latestMergedItemIdsDirty)
        {
            return latestMergedItemIds;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var merged = new List<string>();
        foreach (var view in views)
        {
            foreach (var itemId in view.ItemIds)
            {
                if (seen.Add(itemId))
                {
                    merged.Add(itemId);
                }
            }
        }
        merged.Sort(StringComparer.OrdinalIgnoreCase);
        latestMergedItemIds = merged.ToArray();
        latestMergedItemIdsDirty = false;
        return latestMergedItemIds;
    }

    private static uint SizeOf<T>() where T : unmanaged
        => checked((uint)Unsafe.SizeOf<T>());

    private sealed record SourcePayload(
        TRequest Request,
        string SourceKey,
        IReadOnlyList<string> ItemIds,
        DateTimeOffset LastSeen,
        ulong NativeObservedAtMilliseconds,
        bool Persistent,
        TimeSpan? ExplicitInterval,
        NativeSamplingSubscriptionSourceFlags SamplingFlags);
}

public enum NativeItemSamplingSubscriptionScheduleOrigin
{
    Plan,
    Completion
}

public sealed record NativeItemSamplingSubscriptionScheduleReceipt(
    NativeItemSamplingSubscriptionScheduleOrigin Origin,
    ulong WorkspaceIncarnation,
    ulong ConfigurationGeneration,
    ulong StateRevision,
    ulong PlanEpoch,
    DateTimeOffset CommandAt,
    TimeSpan DefaultInterval,
    TimeSpan FreshnessGrace,
    DateTimeOffset? NextWakeAt,
    DateTimeOffset? EarliestScheduledDueAt,
    int ActiveSourceCount,
    int ActiveItemCount,
    int DueItemCount,
    int ExpiredSourceCount)
{
    public bool IsActive => ActiveSourceCount > 0 && ActiveItemCount > 0;

    public DateTimeOffset? ReadyUntil => IsActive && NextWakeAt is not null
        ? NextWakeAt.Value + FreshnessGrace
        : null;
}

public sealed record NativeItemSamplingSubscriptionCompletion(
    TimeSpan Delay,
    NativeItemSamplingSubscriptionScheduleReceipt Schedule);

public sealed record NativeItemSamplingSubscriptionPlan<TRequest, TContext>(
    bool IsActive,
    TRequest? Request,
    IReadOnlyList<string> DueItemIds,
    TimeSpan Delay,
    NativeItemSamplingSubscriptionScheduleReceipt Schedule,
    TContext OwnerToken)
{
    public IReadOnlyList<string> ActiveItemIds { get; init; } = [];
    public IReadOnlyList<NativeItemSamplingSubscriptionSourceView<TRequest>> SourceViews { get; init; } = [];
    public ImmutableArray<NativeItemSamplingSubscriptionTrigger<TRequest>> Triggers { get; init; } = [];
}

public sealed record NativeItemSamplingSubscriptionTrigger<TRequest>(
    string SourceKey,
    string ItemId,
    ulong ScheduledDueMilliseconds,
    ulong Sequence,
    TRequest Request,
    bool IsFresh);

public sealed record NativeItemSamplingSubscriptionSourceView<TRequest>(
    TRequest Request,
    IReadOnlyList<string> ItemIds,
    DateTimeOffset LastSeen,
    TimeSpan Interval);

