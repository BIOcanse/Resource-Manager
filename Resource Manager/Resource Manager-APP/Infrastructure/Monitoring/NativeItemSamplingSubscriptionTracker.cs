using System.Runtime.CompilerServices;
using System.Diagnostics;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace ResourceManager.App.Infrastructure.Monitoring;

internal sealed class NativeItemSamplingSubscriptionTracker<TRequest>
{
    private const string CoalescedCaptureItemId = "\u001fcapture";
    private readonly object gate = new();
    private readonly HostManagerSamplingSubscriptionOwner owner;
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
    private NativeSamplingSubscriptionItemReference[] pendingItems = [];
    private IReadOnlyList<string> latestMergedItemIds = [];
    private bool latestMergedItemIdsDirty = true;
    private NativeSamplingSubscriptionSession? boundSession;
    private NativeSamplingSubscriptionSession? replaySession;
    private HostManagerSamplingSubscriptionOwner.SessionLease? pendingSessionLease;
    private TRequest? pendingRequest;
    private ulong pendingPlanEpoch;
    private ulong nextHandle;
    private ulong operationEpoch;
    private ulong planEpoch;
    private ulong replayOperationEpoch;
    private ulong lastCommandMilliseconds;
    private long lastCommandStopwatchTimestamp;

    public NativeItemSamplingSubscriptionTracker(
        HostManagerSamplingSubscriptionOwner owner,
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

    public void Track(TRequest request, DateTimeOffset now)
        => TrackCore(request, now, persistent: false, explicitInterval: null);

    public void TrackPersistent(TRequest request, DateTimeOffset now)
        => TrackCore(request, now, persistent: true, explicitInterval: null);

    public void TrackPersistent(
        TRequest request,
        DateTimeOffset now,
        TimeSpan explicitInterval)
        => TrackCore(request, now, persistent: true, explicitInterval);

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
                var session = MutationSession(lease);
                EnsureBoundSession(session, now);
                if (!sourceHandles.TryGetValue(sourceKey, out var handle))
                {
                    return false;
                }

                var input = new NativeSamplingSubscriptionRemoveInput
                {
                    AbiVersion = NativeSamplingSubscriptionAbi.Version,
                    StructSize = SizeOf<NativeSamplingSubscriptionRemoveInput>(),
                    ConfigurationGeneration = session.ConfigurationGeneration,
                    OperationEpoch = NextOperationEpoch(),
                    CommandAtMilliseconds = CommandTimestamp(now),
                    SourceHandle = handle,
                    ValidMask = (ulong)NativeSamplingSubscriptionRemoveValidity.Required
                };
                var status = session.Remove(in input);
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
                return true;
            }
            finally
            {
                lease?.Dispose();
            }
        }
    }

    public NativeItemSamplingSubscriptionPlan<TRequest> CreatePlan(DateTimeOffset now)
    {
        lock (gate)
        {
            if (pendingPlanEpoch != 0)
            {
                throw new InvalidOperationException(
                    "The prior native sampling plan must be completed before another plan is requested.");
            }

            var lease = owner.Acquire(roleId);
            var retainLease = false;
            var completionAttempted = false;
            try
            {
                var session = lease.Session;
                EnsureBoundSession(session, now);
                EnsureBuffers(session.Capacity);
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
                    RequestFlags = 0
                };
                var status = session.Plan(ref header, dueBuffer, sourceBuffer, expiredBuffer);
                if (status != NativeSamplingSubscriptionStatus.Ok)
                {
                    throw new InvalidOperationException(
                        $"Native sampling subscription plan failed with {status}.");
                }
                for (var index = 0; index < header.ExpiredSourceCount; index++)
                {
                    var expiredHandle = expiredBuffer[index].SourceHandle;
                    if (sourcePayloads.Remove(expiredHandle, out var expiredPayload))
                    {
                        sourceHandles.Remove(expiredPayload.SourceKey);
                        latestMergedItemIdsDirty = true;
                    }
                }

                var active = (header.Flags & (ulong)NativeSamplingSubscriptionPlanFlags.Active) != 0;
                var schedule = CreatePlanSchedule(
                    in header,
                    now,
                    commandTimestamp,
                    lease.WorkspaceIncarnation,
                    lease.HotPublish);
                var delay = DelayUntil(now, schedule.NextWakeAt);
                if (header.DueItemCount == 0)
                {
                    return new NativeItemSamplingSubscriptionPlan<TRequest>(
                        active,
                        default,
                        [],
                        delay,
                        schedule,
                        lease.OwnerToken);
                }

                pendingItems = new NativeSamplingSubscriptionItemReference[checked((int)header.DueItemCount)];
                var dueItemIds = new string[checked((int)header.DueItemCount)];
                for (var index = 0; index < header.DueItemCount; index++)
                {
                    var dueItem = dueBuffer[index];
                    pendingItems[index] = new NativeSamplingSubscriptionItemReference
                    {
                        StructSize = SizeOf<NativeSamplingSubscriptionItemReference>(),
                        ItemHandle = dueItem.ItemHandle
                    };
                }
                pendingPlanEpoch = currentPlanEpoch;
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

                var requestItemIds = coalesceItemsIntoLatestCapture
                    ? GetLatestMergedItemIds(views)
                    : dueItemIds;
                var request = createRequest(requestItemIds, views);
                if (request is not null)
                {
                    pendingRequest = request;
                    pendingSessionLease = lease;
                    retainLease = true;
                    return new NativeItemSamplingSubscriptionPlan<TRequest>(
                        true,
                        request,
                        dueItemIds,
                        delay,
                        schedule,
                        lease.OwnerToken);
                }

                completionAttempted = true;
                var completion = CompletePendingWithSession(
                    session,
                    lease.WorkspaceIncarnation,
                    lease.HotPublish,
                    now,
                    NativeSamplingSubscriptionCompletionStatus.Skipped);
                pendingItems = [];
                pendingPlanEpoch = 0;
                pendingRequest = default;
                return new NativeItemSamplingSubscriptionPlan<TRequest>(
                    completion.Schedule.IsActive,
                    default,
                    [],
                    completion.Delay,
                    completion.Schedule,
                    lease.OwnerToken);
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
                    }
                    catch (Exception completionError)
                    {
                        settlementError = completionError;
                    }
                }
                pendingRequest = default;
                pendingItems = [];
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
                if (!retainLease)
                {
                    lease.Dispose();
                }
            }
        }
    }

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
            pendingPlanEpoch = 0;
            nextHandle = 0;
        }
    }

    private void TrackCore(
        TRequest request,
        DateTimeOffset now,
        bool persistent,
        TimeSpan? explicitInterval,
        string? explicitSourceKey = null)
    {
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
            var session = MutationSession(lease);
            EnsureBoundSession(session, now);
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
                ConfigurationGeneration = session.ConfigurationGeneration,
                OperationEpoch = NextOperationEpoch(),
                CommandAtMilliseconds = commandTimestamp,
                ObservedAtMilliseconds = commandTimestamp,
                SourceHandle = sourceHandle,
                ExplicitIntervalMilliseconds = explicitIntervalMilliseconds,
                ValidMask = validMask,
                ItemCount = checked((uint)nativeItems.Length),
                Flags = (uint)flags
            };
            var status = session.Track(in input, nativeItems);
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
                explicitInterval);
            latestMergedItemIdsDirty = true;
        }
    }

    private HostManagerSamplingSubscriptionOwner.SessionLease? AcquireMutationLease()
        => pendingSessionLease is null
            ? owner.Acquire(roleId)
            : null;

    private NativeSamplingSubscriptionSession MutationSession(
        HostManagerSamplingSubscriptionOwner.SessionLease? lease)
        => pendingSessionLease?.Session
            ?? lease?.Session
            ?? throw new InvalidOperationException(
                "The sampling mutation has no active workspace session.");

    private NativeItemSamplingSubscriptionCompletion Complete(
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

        var priorPayloads = sourcePayloads.Values
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
    }

    private NativeItemSamplingSubscriptionCompletion CompletePendingWithSession(
        NativeSamplingSubscriptionSession session,
        ulong workspaceIncarnation,
        CompiledHostManagerSamplingSubscriptionRoleHotPublishPlan hotPublish,
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
            Status = (uint)status
        };
        var nativeStatus = session.Complete(in input, pendingItems, out var output);
        if (nativeStatus != NativeSamplingSubscriptionStatus.Ok)
        {
            throw new InvalidOperationException(
                $"Native sampling subscription completion failed with {nativeStatus}.");
        }
        pendingItems = [];
        pendingPlanEpoch = 0;
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
        CompiledHostManagerSamplingSubscriptionRoleHotPublishPlan hotPublish)
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
        CompiledHostManagerSamplingSubscriptionRoleHotPublishPlan hotPublish)
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
        var currentStopwatchTimestamp = Stopwatch.GetTimestamp();
        if (lastCommandMilliseconds == 0)
        {
            lastCommandMilliseconds = raw;
            lastCommandStopwatchTimestamp = currentStopwatchTimestamp;
            return raw;
        }

        var elapsed = Stopwatch.GetElapsedTime(
            lastCommandStopwatchTimestamp,
            currentStopwatchTimestamp);
        var elapsedMilliseconds = checked((ulong)(
            elapsed.Ticks / TimeSpan.TicksPerMillisecond));
        var monotonicCandidate = checked(
            lastCommandMilliseconds + elapsedMilliseconds);
        lastCommandMilliseconds = Math.Max(raw, monotonicCandidate);
        lastCommandStopwatchTimestamp = currentStopwatchTimestamp;
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
        TimeSpan? ExplicitInterval);
}

internal enum NativeItemSamplingSubscriptionScheduleOrigin
{
    Plan,
    Completion
}

internal sealed record NativeItemSamplingSubscriptionScheduleReceipt(
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

internal sealed record NativeItemSamplingSubscriptionCompletion(
    TimeSpan Delay,
    NativeItemSamplingSubscriptionScheduleReceipt Schedule);

internal sealed record NativeItemSamplingSubscriptionPlan<TRequest>(
    bool IsActive,
    TRequest? Request,
    IReadOnlyList<string> DueItemIds,
    TimeSpan Delay,
    NativeItemSamplingSubscriptionScheduleReceipt Schedule,
    SamplingOwnerToken OwnerToken);

internal sealed record NativeItemSamplingSubscriptionSourceView<TRequest>(
    TRequest Request,
    IReadOnlyList<string> ItemIds,
    DateTimeOffset LastSeen,
    TimeSpan Interval);
