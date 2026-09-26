using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using ResourceManager.Adapter;
using ResourceManager.Adapter.SharedMemory;
using ResourceManager.App.Application.PublicResources;
using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Domain.PublicResources;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace ResourceManager.App.Infrastructure.PublicResources;

internal sealed partial class SharedMemoryPublicResourceBroker :
    ISharedResourceBroker,
    IPublicResourceDirectoryQueries,
    IHostPublicResourceCapability,
    IHostPublicResourcePublicationNotifier,
    IHostPublicResourceSelfManager,
    ISharedResourceSubscriptionMaintenance,
    IHostedService,
    IDisposable
{
    private const string HostOwnerIdentity = "host-manager:self";
    private readonly object lifecycleGate = new();
    private readonly IRuntimePlanProvider runtimePlanProvider;
    private readonly HostManagerDeploymentState deploymentState;
    private readonly HostManagerRuntimeIdentity hostIdentity;
    private BrokerState? state;
    private int hostPublisherObserved;

    public event Action? HostResourcePublished;

    public SharedMemoryPublicResourceBroker(
        IRuntimePlanProvider runtimePlanProvider,
        HostManagerDeploymentState deploymentState,
        HostManagerRuntimeIdentity hostIdentity)
    {
        ArgumentNullException.ThrowIfNull(runtimePlanProvider);
        ArgumentNullException.ThrowIfNull(deploymentState);
        ArgumentNullException.ThrowIfNull(hostIdentity);
        this.runtimePlanProvider = runtimePlanProvider;
        this.deploymentState = deploymentState;
        this.hostIdentity = hostIdentity;
    }

    public ulong HostOwnerApplicationKey { get; } = StableKey(HostOwnerIdentity);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (lifecycleGate)
        {
            if (state is not null)
            {
                return Task.CompletedTask;
            }

            var hostPlan = runtimePlanProvider.Current.HostManager.RequirePublished();
            var recreatePlan = hostPlan.HostRecreate.SharedResources;
            var hotPlan = hostPlan.HotPublish.SharedResources;
            var nonce = Guid.NewGuid().ToString("N");
            var attempt = deploymentState.BeginAttempt(
                HostManagerModuleKind.SharedResources,
                hostPlan,
                HostManagerDeploymentOperation.InitialCreate);
            NativeSharedResourceSession? session = null;
            BrokerState? created = null;
            try
            {
                session = NativeSharedResourceSession.Create(
                    $"Local\\ResourceManager.SharedResources.{nonce}.Host",
                    $"Local\\ResourceManager.SharedResources.{nonce}.Mutex",
                    new SharedResourceSessionOptions(
                        recreatePlan.ResourceCapacity,
                        recreatePlan.SubscriptionCapacity,
                        recreatePlan.TaskCapacity,
                        hotPlan.SubscriptionCoefficient,
                        HostOwnerApplicationKey,
                        hostIdentity.ProcessId,
                        hostIdentity.ProcessCreatedAt,
                        TimeSpan.FromMilliseconds(
                            recreatePlan.MaximumSubscriptionLeaseDurationMilliseconds),
                        TimeSpan.FromMilliseconds(
                            recreatePlan.MaximumQueueDurationMilliseconds),
                        TimeSpan.FromMilliseconds(
                            recreatePlan.MaximumGrantDurationMilliseconds)));
                created = new BrokerState(
                    session,
                    hostIdentity.InstanceId,
                    new SharedResourceAuthorityId(
                        CreateNonZeroId(),
                        hostIdentity.InstanceId));
                session = null;
                state = created;
                created = null;
                HostManagerDeploymentAttemptSettlementGuard.RequireApplied(
                    deploymentState.CompleteAttemptSucceeded(attempt),
                    HostManagerModuleKind.SharedResources);
            }
            catch (Exception exception)
            {
                IReadOnlyList<Exception> cleanupExceptions = [];
                Exception? settlementException = null;
                try
                {
                    var published = state;
                    state = null;
                    cleanupExceptions = HostManagerBestEffortCleanup.DisposeAll(
                        published,
                        created,
                        session);
                }
                finally
                {
                    try
                    {
                        HostManagerDeploymentAttemptSettlementGuard.RequireFailed(
                            deploymentState.CompleteAttemptFailed(
                                attempt,
                                "shared-resource-initial-create-failed",
                                ToNativeResult(exception)),
                            HostManagerModuleKind.SharedResources);
                    }
                    catch (Exception settlement)
                    {
                        settlementException = settlement;
                    }
                }
                if (cleanupExceptions.Count != 0 || settlementException is not null)
                {
                    var failures = new List<Exception>(cleanupExceptions.Count + 2) { exception };
                    failures.AddRange(cleanupExceptions);
                    if (settlementException is not null) failures.Add(settlementException);
                    throw new AggregateException(
                        "Shared-resource Host initialization and cleanup or deployment settlement also failed.",
                        failures);
                }
                throw;
            }
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        lock (lifecycleGate)
        {
            var stopped = state;
            if (stopped is not null)
            {
                RequireNoPendingLifecycleOperations(stopped);
            }
            state = null;
            Volatile.Write(ref hostPublisherObserved, 0);
            stopped?.Dispose();
        }

        return Task.CompletedTask;
    }

    public SharedResourceId PublishHostResource(
        HostPublicResourceDefinition definition,
        IHostPublicResourceLifecycle lifecycle)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(lifecycle);
        SharedResourceId id;
        lock (lifecycleGate)
        {
            var current = RequireState();
            id = current.Session.Publish(ToPublication(current, definition));
            current.Lifecycles.Add(id, lifecycle);
            current.Definitions.Add(
                id,
                definition with { PublicResourceId = id.PublicResourceId });
            Volatile.Write(ref hostPublisherObserved, 1);
        }
        HostResourcePublished?.Invoke();
        return id;
    }

    public bool TryUpdateHostResource(
        SharedResourceId id,
        HostPublicResourceDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        lock (lifecycleGate)
        {
            var current = RequireState();
            var normalized = definition with
            {
                PublicResourceId = id.PublicResourceId
            };
            if (!current.Lifecycles.ContainsKey(id)
                || !current.Definitions.ContainsKey(id)
                || current.PendingUnloads.ContainsKey(id)
                || current.PendingRecalls.ContainsKey(id)
                || normalized.Availability != SharedResourceAvailability.Available
                || !current.Session.TryGetResource(id, out var existing)
                || existing is null
                || existing.Availability != SharedResourceAvailability.Available
                || existing.DestructiveActionActive
                || existing.AdapterKey != normalized.AdapterKey
                || existing.Flags.HasFlag(SharedResourceFlags.GpuBacked)
                    != normalized.Flags.HasFlag(SharedResourceFlags.GpuBacked))
            {
                return false;
            }
            var updated = current.Session.TryUpdate(
                id,
                ToPublication(current, normalized));
            if (updated)
            {
                current.Definitions[id] = normalized;
            }
            return updated;
        }
    }

    public bool RemoveHostResource(SharedResourceId id)
    {
        lock (lifecycleGate)
        {
            var current = RequireState();
            if (current.PendingUnloads.ContainsKey(id)
                || current.PendingRecalls.ContainsKey(id)
                || current.Session.TryGetResource(id, out var subscribedResource)
                    && subscribedResource is not null
                    && subscribedResource.ActiveSubscriberCount != 0)
            {
                return false;
            }
            var removed = current.Session.TryRevoke(id);
            if (removed)
            {
                current.Lifecycles.Remove(id);
                current.Definitions.Remove(id);
            }
            return removed;
        }
    }

    HostPublicResourceSelfManagerTickResult IHostPublicResourceSelfManager.Tick(
        HostPublicResourceSelfManagerTickRequest request)
    {
        lock (lifecycleGate)
        {
            var current = RequireState();
            var shortage = request.Shortage;
            var plannedCount = 0;
            var unloadedCount = 0;
            var recalledCount = 0;
            var rejectedCount = 0;
            uint newEffectAttemptCount = 0;
            uint recoveryAttemptCount = 0;
            ulong releasedBytes = 0;
            ulong restoredBytes = 0;
            var memoryRecallAttempted = false;
            var videoMemoryRecallAttempted = false;
            RecoverPendingLifecycleOperations(
                current,
                ref plannedCount,
                ref unloadedCount,
                ref recalledCount,
                ref rejectedCount,
                ref releasedBytes,
                ref restoredBytes,
                ref memoryRecallAttempted,
                ref videoMemoryRecallAttempted,
                request.MaximumRecoveryAttempts,
                ref recoveryAttemptCount);

            var plan = current.PublicResourceManager.Tick(shortage);
            foreach (var candidate in plan.UnloadCandidates)
            {
                if (newEffectAttemptCount >= request.MaximumNewEffectAttempts)
                {
                    break;
                }
                plannedCount++;
                var unloaded = TryUnloadManagedResource(
                    current,
                    candidate,
                    out var pointOfNoReturnCrossed);
                if (pointOfNoReturnCrossed)
                {
                    newEffectAttemptCount++;
                }
                if (!unloaded)
                {
                    rejectedCount++;
                    continue;
                }

                unloadedCount++;
                releasedBytes = checked(releasedBytes + candidate.SizeBytes);
            }

            foreach (var candidate in plan.RecallCandidates)
            {
                if (newEffectAttemptCount >= request.MaximumNewEffectAttempts)
                {
                    break;
                }
                var gpuBacked = candidate.Flags.HasFlag(
                    SharedResourceFlags.GpuBacked);
                if (gpuBacked
                        ? videoMemoryRecallAttempted
                        : memoryRecallAttempted)
                {
                    continue;
                }
                if (!current.Lifecycles.TryGetValue(
                        candidate.ResourceId,
                        out var lifecycle)
                    || !lifecycle.SupportsRecall)
                {
                    continue;
                }
                if (gpuBacked)
                {
                    videoMemoryRecallAttempted = true;
                }
                else
                {
                    memoryRecallAttempted = true;
                }
                plannedCount++;
                var recalled = TryRecallManagedResource(
                    current,
                    candidate,
                    out var restoredSizeBytes,
                    out var pointOfNoReturnCrossed);
                if (pointOfNoReturnCrossed)
                {
                    newEffectAttemptCount++;
                }
                if (!recalled)
                {
                    rejectedCount++;
                    continue;
                }

                recalledCount++;
                restoredBytes = checked(restoredBytes + restoredSizeBytes);
            }

            return new HostPublicResourceSelfManagerTickResult(
                plannedCount,
                unloadedCount,
                recalledCount,
                rejectedCount,
                newEffectAttemptCount,
                recoveryAttemptCount,
                releasedBytes,
                restoredBytes,
                plan.SampleGeneration);
        }
    }

    public PublicResourceCatalogSnapshot GetCatalog()
    {
        lock (lifecycleGate)
        {
            var resources = RequireState().Session.ListResources(out var topologyGeneration);
            return new PublicResourceCatalogSnapshot(
                SharedResourceProtocol.Version,
                topologyGeneration,
                resources.Select(ToDescriptor).ToArray(),
                DateTimeOffset.UtcNow);
        }
    }

    public PublicResourceDescriptor? Find(ulong publicResourceId)
    {
        if (publicResourceId == 0)
        {
            return null;
        }

        lock (lifecycleGate)
        {
            return RequireState().Session.TryFindResource(publicResourceId, out var snapshot)
                ? ToDescriptor(snapshot!)
                : null;
        }
    }

    public PublicResourceTransportSummary GetTransportSummary()
        => new(
            SharedResourceProtocol.Version,
            "shared-memory-zig",
            DirectMappingAvailable: false,
            "broker-query-only");

    public HostPublicResourceCapabilitySnapshot GetCapability()
    {
        var available = Volatile.Read(ref hostPublisherObserved) != 0;
        return available
            ? new HostPublicResourceCapabilitySnapshot(
                HostPublicResourceCapabilityStates.Available,
                true,
                "至少一个 Host 管理的公共资源发布者已进入当前运行实例。")
            : new HostPublicResourceCapabilitySnapshot(
                HostPublicResourceCapabilityStates.Unavailable,
                false,
                "当前运行实例尚无 Host 公共资源发布者；目录和自管调度保持关闭。");
    }

    int ISharedResourceSubscriptionMaintenance.MaintenanceIntervalMilliseconds
    {
        get
        {
            lock (lifecycleGate)
            {
                _ = RequireState();
                return runtimePlanProvider.Current.HostManager
                    .RequirePublished()
                    .HotPublish.SharedResources.MaintenanceIntervalMilliseconds;
            }
        }
    }

    SharedResourceMaintenanceResult ISharedResourceSubscriptionMaintenance.Refresh(
        ulong nowTimestamp)
    {
        lock (lifecycleGate)
        {
            var current = RequireState();
            var session = current.Session;
            return new SharedResourceMaintenanceResult(
                session.SweepSubscriptions(),
                session.SweepUses());
        }
    }

    public void Dispose()
    {
        lock (lifecycleGate)
        {
            var disposed = state;
            if (disposed is not null)
            {
                RequireNoPendingLifecycleOperations(disposed);
            }
            state = null;
            Volatile.Write(ref hostPublisherObserved, 0);
            disposed?.Dispose();
        }
    }

    private BrokerState RequireState()
        => state ?? throw new InvalidOperationException(
            "The shared-resource broker is not running.");

    private static HostManagerNativeResultSnapshot? ToNativeResult(Exception exception)
        => exception is SharedResourceNativeException nativeException
            ? new HostManagerNativeResultSnapshot(
                "shared-resource",
                nativeException.ResultCode)
            : null;

    private SharedResourcePublication ToPublication(
        BrokerState current,
        HostPublicResourceDefinition definition)
    {
        ValidateAdapterIdentity(definition.Flags, definition.AdapterKey);
        var planEpoch = runtimePlanProvider.Current.HostManager.RequirePublished().PlanEpoch;
        return new(
            PublicResourceId: definition.PublicResourceId,
            OwnerApplicationKey: HostOwnerApplicationKey,
            OwnerInstanceId: new SharedResourceAuthorityId(
                HostOwnerApplicationKey,
                current.HostInstanceId),
            OwnerContextGeneration: planEpoch,
            LeaseGeneration: planEpoch,
            BindingGeneration: planEpoch,
            CapabilityGeneration: planEpoch,
            ExecutorId: current.HostExecutorId,
            OwnerProcessId: Environment.ProcessId,
            ResourceKey: definition.ResourceKey,
            AdapterKey: definition.AdapterKey,
            ResourceId: definition.ResourceId,
            SizeBytes: definition.SizeBytes,
            ContentIdentityHash: definition.ContentIdentityHash,
            PayloadMappingId: definition.PayloadMappingId,
            PayloadGeneration: definition.PayloadGeneration,
            MaxParallelGrants: definition.MaxParallelGrants,
            Tier: definition.Tier,
            ResourceKind: definition.ResourceKind,
            RecoveryKind: definition.RecoveryKind,
            Granularity: definition.Granularity,
            InapplicableActions: definition.InapplicableActions,
            ActionRoute: definition.ActionRoute,
            OwnerDemandMask: definition.DemandMask,
            SurfaceState: definition.SurfaceState,
            Availability: definition.Availability,
            Flags: definition.Flags);
    }

    private static bool TryUnloadManagedResource(
        BrokerState current,
        HostPublicResourceUnloadCandidate candidate,
        out bool pointOfNoReturnCrossed)
    {
        pointOfNoReturnCrossed = false;
        if (!current.Lifecycles.TryGetValue(candidate.ResourceId, out var lifecycle)
            || !current.Session.TryGetResource(candidate.ResourceId, out var resource)
            || resource is null
            || resource.SchedulingRevision != candidate.SchedulingRevision
            || resource.GateEpoch != candidate.GateEpoch
            || resource.SizeBytes != candidate.SizeBytes
            || resource.ActiveSubscriberCount != candidate.SubscriberCount
            || resource.ActivityScore != candidate.ActivityScore
            || resource.Flags != candidate.Flags
            || resource.ProtectedUseCount != 0
            || resource.Availability != SharedResourceAvailability.Available
            || !resource.ConsistencyStable
            || resource.DestructiveActionActive)
        {
            return false;
        }

        SharedResourcePublication postState;
        try
        {
            postState = CreateUnloadedPublication(resource);
        }
        catch (OverflowException)
        {
            return false;
        }

        SharedResourceDestructiveToken token;
        try
        {
            token = current.Session.BeginDestructiveExpected(
                new SharedResourceDestructiveExpected(
                    resource.Id,
                    resource.SchedulingRevision,
                    new SharedResourceAuthorityId(
                        CreateNonZeroId(),
                        CreateNonZeroId()),
                    AdapterResourceActionMask.Discard));
        }
        catch (SharedResourceNativeException)
        {
            return false;
        }
        pointOfNoReturnCrossed = true;

        var operation = new HostPublicResourceLifecycleOperation(
            token.ActionAttemptId,
            HostPublicResourceLifecycleOperationKind.Unload,
            resource.Id);
        HostPublicResourceLifecycleResult result;
        try
        {
            result = lifecycle.ExecuteUnload(
                operation,
                new HostPublicResourceUnloadContext(
                    resource.Id,
                    resource.AdapterKey,
                    resource.SizeBytes,
                    resource.Tier,
                    candidate.Reason,
                    candidate.RetentionScoreQ16));
        }
        catch
        {
            result = UncertainLifecycleResult();
        }
        return TrySettlePendingUnload(
            current,
            new PendingHostPublicUnload(
                operation,
                lifecycle,
                token,
                postState,
                candidate,
                result),
            result);
    }

    private bool TryRecallManagedResource(
        BrokerState current,
        HostPublicResourceRecallCandidate candidate,
        out ulong restoredSizeBytes,
        out bool pointOfNoReturnCrossed)
    {
        restoredSizeBytes = 0;
        pointOfNoReturnCrossed = false;
        if (!current.Lifecycles.TryGetValue(candidate.ResourceId, out var lifecycle)
            || !lifecycle.SupportsRecall
            || !current.Definitions.TryGetValue(candidate.ResourceId, out var expected)
            || !current.Session.TryGetResource(candidate.ResourceId, out var resource)
            || resource is null
            || !RecallCandidateMatches(resource, candidate))
        {
            return false;
        }

        SharedResourceRecallToken token;
        try
        {
            token = current.Session.BeginRecallExpected(
                new SharedResourceRecallExpected(
                    candidate.ResourceId,
                    candidate.SchedulingRevision,
                    candidate.GateEpoch,
                    new SharedResourceAuthorityId(
                        CreateNonZeroId(),
                        CreateNonZeroId()),
                    candidate.PayloadGeneration,
                    candidate.SubscriberCount,
                    candidate.ActivityScore,
                    candidate.Flags));
        }
        catch (SharedResourceNativeException)
        {
            return false;
        }
        pointOfNoReturnCrossed = true;

        var operation = new HostPublicResourceLifecycleOperation(
            token.ActionAttemptId,
            HostPublicResourceLifecycleOperationKind.Recall,
            candidate.ResourceId);
        HostPublicResourceLifecycleResult result;
        try
        {
            result = lifecycle.ExecuteRecall(
                operation,
                new HostPublicResourceRecallContext(
                    candidate.ResourceId,
                    candidate.AdapterKey,
                    candidate.Tier,
                    candidate.SubscriberCount,
                    candidate.ActivityScore,
                    candidate.RetentionScoreQ16));
        }
        catch
        {
            result = UncertainLifecycleResult();
        }
        return TrySettlePendingRecall(
            current,
            new PendingHostPublicRecall(
                operation,
                lifecycle,
                token,
                expected,
                candidate,
                result,
                RequiresRecovery: false),
            result,
            out restoredSizeBytes);
    }

    private static bool RecallCandidateMatches(
        SharedResourceSnapshot resource,
        HostPublicResourceRecallCandidate candidate)
        => resource.SchedulingRevision == candidate.SchedulingRevision
            && resource.GateEpoch == candidate.GateEpoch
            && resource.PayloadGeneration == candidate.PayloadGeneration
            && resource.AdapterKey == candidate.AdapterKey
            && resource.ActiveSubscriberCount == candidate.SubscriberCount
            && resource.ActivityScore == candidate.ActivityScore
            && resource.Tier == candidate.Tier
            && resource.Flags == candidate.Flags
            && resource.SizeBytes == 0
            && resource.ProtectedUseCount == 0
            && resource.Availability == SharedResourceAvailability.Unavailable
            && resource.ConsistencyStable
            && !resource.DestructiveActionActive;

    private static bool RecallDefinitionMatches(
        HostPublicResourceDefinition expected,
        SharedResourceSnapshot preparing,
        HostPublicResourceDefinition restored)
        => restored.PublicResourceId == preparing.Id.PublicResourceId
            && restored.ResourceKey == expected.ResourceKey
            && preparing.AdapterKey == expected.AdapterKey
            && restored.AdapterKey == expected.AdapterKey
            && restored.ResourceId == expected.ResourceId
            && restored.ContentIdentityHash == expected.ContentIdentityHash
            && restored.MaxParallelGrants == expected.MaxParallelGrants
            && restored.Tier == expected.Tier
            && restored.ResourceKind == expected.ResourceKind
            && restored.RecoveryKind == expected.RecoveryKind
            && restored.Granularity == expected.Granularity
            && restored.InapplicableActions == expected.InapplicableActions
            && restored.ActionRoute == expected.ActionRoute
            && restored.DemandMask == expected.DemandMask
            && restored.SurfaceState == expected.SurfaceState
            && restored.Flags == expected.Flags
            && restored.Availability == SharedResourceAvailability.Available
            && restored.SizeBytes > 0
            && preparing.PayloadGeneration != ulong.MaxValue
            && restored.PayloadGeneration == preparing.PayloadGeneration + 1;

    private static void RecoverPendingLifecycleOperations(
        BrokerState current,
        ref int plannedCount,
        ref int unloadedCount,
        ref int recalledCount,
        ref int rejectedCount,
        ref ulong releasedBytes,
        ref ulong restoredBytes,
        ref bool memoryRecallAttempted,
        ref bool videoMemoryRecallAttempted,
        uint maximumRecoveryAttempts,
        ref uint recoveryAttemptCount)
    {
        foreach (var pending in current.PendingUnloads.Values.ToArray())
        {
            var requiresRecovery = pending.Result.EffectKnowledge ==
                HostPublicResourceEffectKnowledge.EffectUncertain;
            if (requiresRecovery && recoveryAttemptCount >= maximumRecoveryAttempts)
            {
                continue;
            }
            plannedCount++;
            var result = pending.Result;
            if (requiresRecovery)
            {
                recoveryAttemptCount++;
                result = RecoverLifecycleResult(pending.Lifecycle, pending.Operation);
            }
            if (TrySettlePendingUnload(current, pending, result))
            {
                unloadedCount++;
                releasedBytes = checked(
                    releasedBytes + pending.Candidate.SizeBytes);
            }
            else
            {
                rejectedCount++;
            }
        }

        foreach (var pending in current.PendingRecalls.Values.ToArray())
        {
            var requiresRecovery = pending.RequiresRecovery
                || pending.Result.EffectKnowledge ==
                    HostPublicResourceEffectKnowledge.EffectUncertain;
            if (requiresRecovery && recoveryAttemptCount >= maximumRecoveryAttempts)
            {
                continue;
            }
            var gpuBacked = pending.Candidate.Flags.HasFlag(
                SharedResourceFlags.GpuBacked);
            if (gpuBacked)
            {
                videoMemoryRecallAttempted = true;
            }
            else
            {
                memoryRecallAttempted = true;
            }
            plannedCount++;
            var result = pending.Result;
            if (requiresRecovery)
            {
                recoveryAttemptCount++;
                result = RecoverLifecycleResult(pending.Lifecycle, pending.Operation);
            }
            if (pending.Result.EffectKnowledge ==
                    HostPublicResourceEffectKnowledge.KnownEffect
                && result.EffectKnowledge ==
                    HostPublicResourceEffectKnowledge.KnownNoEffect)
            {
                result = UncertainLifecycleResult();
            }
            if (TrySettlePendingRecall(
                    current,
                    pending,
                    result,
                    out var restoredSizeBytes))
            {
                recalledCount++;
                restoredBytes = checked(restoredBytes + restoredSizeBytes);
            }
            else
            {
                rejectedCount++;
            }
        }
    }

    private static void RequireNoPendingLifecycleOperations(
        BrokerState current)
    {
        var plannedCount = 0;
        var unloadedCount = 0;
        var recalledCount = 0;
        var rejectedCount = 0;
        ulong releasedBytes = 0;
        ulong restoredBytes = 0;
        var memoryRecallAttempted = false;
        var videoMemoryRecallAttempted = false;
        uint recoveryAttemptCount = 0;
        RecoverPendingLifecycleOperations(
            current,
            ref plannedCount,
            ref unloadedCount,
            ref recalledCount,
            ref rejectedCount,
            ref releasedBytes,
            ref restoredBytes,
            ref memoryRecallAttempted,
            ref videoMemoryRecallAttempted,
            uint.MaxValue,
            ref recoveryAttemptCount);
        if (current.PendingUnloads.Count != 0
            || current.PendingRecalls.Count != 0)
        {
            throw new InvalidOperationException(
                "Host public-resource lifecycle operations remain effect-uncertain; the broker cannot discard their recovery state.");
        }
    }

    private static HostPublicResourceLifecycleResult RecoverLifecycleResult(
        IHostPublicResourceLifecycle lifecycle,
        HostPublicResourceLifecycleOperation operation)
    {
        try
        {
            return lifecycle.Recover(operation);
        }
        catch
        {
            return UncertainLifecycleResult();
        }
    }

    private static HostPublicResourceLifecycleResult UncertainLifecycleResult()
        => new(HostPublicResourceEffectKnowledge.EffectUncertain, null);

    private static bool TrySettlePendingUnload(
        BrokerState current,
        PendingHostPublicUnload pending,
        HostPublicResourceLifecycleResult result)
    {
        var updated = pending with { Result = NormalizeLifecycleResult(result) };
        try
        {
            switch (updated.Result.EffectKnowledge)
            {
                case HostPublicResourceEffectKnowledge.KnownEffect:
                    current.Session.CommitDestructive(
                        updated.Token,
                        updated.PostState);
                    current.PendingUnloads.Remove(updated.Operation.ResourceId);
                    return true;
                case HostPublicResourceEffectKnowledge.KnownNoEffect:
                    AbortUnload(current.Session, updated.Token);
                    current.PendingUnloads.Remove(updated.Operation.ResourceId);
                    return false;
                default:
                    current.PendingUnloads[updated.Operation.ResourceId] = updated;
                    return false;
            }
        }
        catch
        {
            current.PendingUnloads[updated.Operation.ResourceId] = updated;
            return false;
        }
    }

    private static bool TrySettlePendingRecall(
        BrokerState current,
        PendingHostPublicRecall pending,
        HostPublicResourceLifecycleResult result,
        out ulong restoredSizeBytes)
    {
        restoredSizeBytes = 0;
        var normalized = NormalizeLifecycleResult(result);
        var updated = pending with
        {
            Result = normalized,
            RequiresRecovery = normalized.EffectKnowledge ==
                HostPublicResourceEffectKnowledge.EffectUncertain
        };
        try
        {
            switch (normalized.EffectKnowledge)
            {
                case HostPublicResourceEffectKnowledge.KnownNoEffect:
                    AbortRecall(current.Session, updated.Token);
                    current.PendingRecalls.Remove(updated.Operation.ResourceId);
                    return false;
                case HostPublicResourceEffectKnowledge.KnownEffect:
                    if (normalized.Definition is null
                        || !current.Session.TryGetResource(
                            updated.Operation.ResourceId,
                            out var preparing)
                        || preparing is null
                        || preparing.Availability !=
                            SharedResourceAvailability.Preparing
                        || !preparing.DestructiveActionActive
                        || preparing.ProtectedUseCount != 0
                        || !preparing.ConsistencyStable
                        || !RecallDefinitionMatches(
                            updated.ExpectedDefinition,
                            preparing,
                            normalized.Definition))
                    {
                        current.PendingRecalls[updated.Operation.ResourceId] =
                            updated with { RequiresRecovery = true };
                        return false;
                    }

                    current.Session.CommitRecall(
                        updated.Token,
                        CreateRecallRestoredPublication(
                            preparing,
                            normalized.Definition));
                    current.Definitions[updated.Operation.ResourceId] =
                        normalized.Definition;
                    current.PendingRecalls.Remove(updated.Operation.ResourceId);
                    restoredSizeBytes = normalized.Definition.SizeBytes;
                    return true;
                default:
                    current.PendingRecalls[updated.Operation.ResourceId] = updated;
                    return false;
            }
        }
        catch
        {
            current.PendingRecalls[updated.Operation.ResourceId] = updated;
            return false;
        }
    }

    private static HostPublicResourceLifecycleResult NormalizeLifecycleResult(
        HostPublicResourceLifecycleResult? result)
        => result is not null
            && Enum.IsDefined(result.EffectKnowledge)
            ? result
            : UncertainLifecycleResult();

    private static void AbortUnload(
        NativeSharedResourceSession session,
        SharedResourceDestructiveToken token)
        => session.AbortDestructive(
            token,
            CreateNoEffectReceipt(token.ActionAttemptId));

    private static void AbortRecall(
        NativeSharedResourceSession session,
        SharedResourceRecallToken token)
        => session.AbortRecall(
            token,
            CreateNoEffectReceipt(token.ActionAttemptId));

    private static SharedResourceDestructiveNoEffectReceipt CreateNoEffectReceipt(
        SharedResourceAuthorityId operationId)
        => new(
            operationId,
            new SharedResourceAuthorityId(
                CreateNonZeroId(),
                CreateNonZeroId()),
            NativeSharedResourceSession.MonotonicNow(),
            SharedResourceNoEffectProof.NotInvoked,
            SharedResourceNoEffectReason.ExecutorRejectedBeforeEffect);

    private static SharedResourcePublication CreateUnloadedPublication(
        SharedResourceSnapshot resource)
        => new(
            resource.Id.PublicResourceId,
            resource.OwnerApplicationKey,
            resource.OwnerInstanceId,
            resource.OwnerContextGeneration,
            resource.LeaseGeneration,
            resource.BindingGeneration,
            resource.CapabilityGeneration,
            resource.ExecutorId,
            resource.OwnerProcessId,
            resource.ResourceKey,
            resource.AdapterKey,
            resource.ResourceId,
            SizeBytes: 0,
            resource.ContentIdentityHash,
            PayloadMappingId: 0,
            PayloadGeneration: checked(resource.PayloadGeneration + 1),
            resource.MaxParallelGrants,
            resource.Tier,
            resource.ResourceKind,
            resource.RecoveryKind,
            resource.Granularity,
            resource.InapplicableActions,
            resource.ActionRoute,
            resource.OwnerDemandMask,
            resource.SurfaceState,
            SharedResourceAvailability.Unavailable,
            resource.Flags);

    private static SharedResourcePublication CreateRecallRestoredPublication(
        SharedResourceSnapshot resource,
        HostPublicResourceDefinition restored)
        => new(
            resource.Id.PublicResourceId,
            resource.OwnerApplicationKey,
            resource.OwnerInstanceId,
            resource.OwnerContextGeneration,
            resource.LeaseGeneration,
            resource.BindingGeneration,
            resource.CapabilityGeneration,
            resource.ExecutorId,
            resource.OwnerProcessId,
            resource.ResourceKey,
            resource.AdapterKey,
            resource.ResourceId,
            restored.SizeBytes,
            resource.ContentIdentityHash,
            restored.PayloadMappingId,
            restored.PayloadGeneration,
            resource.MaxParallelGrants,
            resource.Tier,
            resource.ResourceKind,
            resource.RecoveryKind,
            resource.Granularity,
            resource.InapplicableActions,
            resource.ActionRoute,
            resource.OwnerDemandMask,
            resource.SurfaceState,
            SharedResourceAvailability.Available,
            resource.Flags);

    private static PublicResourceDescriptor ToDescriptor(SharedResourceSnapshot snapshot)
        => new(
            snapshot.Id.PublicResourceId,
            snapshot.OwnerApplicationKey,
            snapshot.OwnerProcessId,
            snapshot.AdapterKey,
            snapshot.SizeBytes,
            snapshot.Tier,
            snapshot.ResourceKind,
            snapshot.RecoveryKind,
            snapshot.Granularity,
            snapshot.OwnerDemandMask,
            snapshot.SubscriptionIntentMask,
            snapshot.Availability,
            snapshot.Flags,
            snapshot.MaxParallelGrants,
            snapshot.ActiveSubscriberCount,
            snapshot.QueuedRequestCount,
            snapshot.ReservedGrantCount,
            snapshot.ActiveUseCount,
            snapshot.SubscriptionMultiplier,
            snapshot.LastUpdatedAt,
            snapshot.AllowedActions,
            snapshot.ConsistencyStable,
            snapshot.DestructiveActionActive);

    private static void ValidateAdapterIdentity(
        SharedResourceFlags flags,
        ulong adapterKey)
    {
        var gpuBacked = flags.HasFlag(SharedResourceFlags.GpuBacked);
        if (gpuBacked != (adapterKey != 0))
        {
            throw new ArgumentException(
                "GPU-backed public resources require a non-zero adapter key, and non-GPU resources require zero.",
                nameof(adapterKey));
        }
    }

    private static ulong StableKey(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        var key = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
        return key == 0 ? 1UL : key;
    }

    private static ulong CreateNonZeroId()
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        ulong value;
        do
        {
            RandomNumberGenerator.Fill(bytes);
            value = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
        }
        while (value == 0);

        return value;
    }

    private sealed class BrokerState(
        NativeSharedResourceSession session,
        ulong hostInstanceId,
        SharedResourceAuthorityId hostExecutorId) : IDisposable
    {
        public NativeSharedResourceSession Session { get; } = session;
        public DefaultHostPublicResourceManager PublicResourceManager { get; } =
            new(session);
        public ulong HostInstanceId { get; } = hostInstanceId;
        public SharedResourceAuthorityId HostExecutorId { get; } = hostExecutorId;
        public Dictionary<SharedResourceId, IHostPublicResourceLifecycle> Lifecycles { get; } = [];
        public Dictionary<SharedResourceId, HostPublicResourceDefinition> Definitions { get; } = [];
        public Dictionary<SharedResourceId, PendingHostPublicUnload> PendingUnloads { get; } = [];
        public Dictionary<SharedResourceId, PendingHostPublicRecall> PendingRecalls { get; } = [];

        public void Dispose()
        {
            Session.Dispose();
        }
    }

    private sealed record PendingHostPublicUnload(
        HostPublicResourceLifecycleOperation Operation,
        IHostPublicResourceLifecycle Lifecycle,
        SharedResourceDestructiveToken Token,
        SharedResourcePublication PostState,
        HostPublicResourceUnloadCandidate Candidate,
        HostPublicResourceLifecycleResult Result);

    private sealed record PendingHostPublicRecall(
        HostPublicResourceLifecycleOperation Operation,
        IHostPublicResourceLifecycle Lifecycle,
        SharedResourceRecallToken Token,
        HostPublicResourceDefinition ExpectedDefinition,
        HostPublicResourceRecallCandidate Candidate,
        HostPublicResourceLifecycleResult Result,
        bool RequiresRecovery);

}
