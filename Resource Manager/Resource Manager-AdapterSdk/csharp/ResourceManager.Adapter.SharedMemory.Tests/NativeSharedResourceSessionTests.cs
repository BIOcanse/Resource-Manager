using ResourceManager.Adapter;
using ResourceManager.Adapter.SharedMemory;
using System.Runtime.InteropServices;

namespace ResourceManager.Adapter.SharedMemory.Tests;

public sealed class NativeSharedResourceSessionTests
{
    private const AdapterResourceActionMask DestructiveActions =
        AdapterResourceActionMask.Discard
        | AdapterResourceActionMask.Trim
        | AdapterResourceActionMask.MoveDown;

    [Fact]
    public void PublishUpdateAndRevoke_AreVisibleAcrossIndependentViews()
    {
        using var owner = CreateSession(resourceCapacity: 2, subscriptionCapacity: 4, taskCapacity: 8);
        var id = owner.Publish(CreatePublication(sizeBytes: 4096));
        using var reader = NativeSharedResourceSession.OpenExisting(owner.Descriptor);

        Assert.True(reader.TryGetResource(id, out var initial));
        Assert.NotNull(initial);
        Assert.Equal(4096UL, initial.SizeBytes);
        Assert.Equal(0, initial.ActivityScore);

        Assert.True(owner.TryUpdate(id, CreatePublication(sizeBytes: 8192) with
        {
            PublicResourceId = id.PublicResourceId
        }));
        Assert.True(reader.TryGetResource(id, out var updated));
        Assert.Equal(8192UL, updated!.SizeBytes);
        Assert.Equal(id.ResourceGeneration, updated.Id.ResourceGeneration);

        Assert.True(owner.TryRevoke(id));
        Assert.False(reader.TryGetResource(id, out _));

        var replacement = owner.Publish(CreatePublication(sizeBytes: 1024));
        Assert.Equal(id.ResourceSlot, replacement.ResourceSlot);
        Assert.NotEqual(id.ResourceGeneration, replacement.ResourceGeneration);
        Assert.False(reader.TryGetResource(id, out _));
        Assert.True(reader.TryGetResource(replacement, out _));
    }

    [Fact]
    public void Subscription_IsImmediateResidencyInterestButNeverUseOccupancy()
    {
        using var owner = CreateSession(resourceCapacity: 1, subscriptionCapacity: 4, taskCapacity: 8);
        var id = owner.Publish(CreatePublication(sizeBytes: 4096));
        Assert.True(owner.TryGetResource(id, out var initial));
        var staleUnload = new SharedResourceDestructiveExpected(
            id,
            initial!.SchedulingRevision,
            new SharedResourceAuthorityId(6901, 6902),
            AdapterResourceActionMask.Discard);
        using var client = NativeSharedResourceSession.OpenExisting(owner.Descriptor, writable: true);
        var lease = client.Subscribe(
            id,
            subscriberApplicationKey: 701,
            subscriberInstanceId: 7001,
            subscriberSessionId: 8001,
            subscriberProcessId: Environment.ProcessId,
            TimeSpan.FromMinutes(1),
            SharedResourceSubscriptionIntent.ReadySoon);

        Assert.True(owner.TryGetResource(id, out var subscribed));
        Assert.Equal(1, subscribed!.ActiveSubscriberCount);
        Assert.Equal(0, subscribed.ProtectedUseCount);
        Assert.Equal(SharedResourceSubscriptionIntent.ReadySoon, subscribed.SubscriptionIntentMask);
        Assert.True(subscribed.SubscriptionMultiplier > 1.0d);
        Assert.Equal(initial.SchedulingRevision + 1, subscribed.SchedulingRevision);
        Assert.Throws<SharedResourceNativeException>(() =>
            owner.BeginDestructiveExpected(staleUnload));
        Assert.Equal(
            DestructiveActions,
            owner.FilterAllowedActions(id, DestructiveActions));

        var renewed = client.ConfirmSubscription(
            lease,
            TimeSpan.FromMinutes(2),
            SharedResourceSubscriptionIntent.PreloadEager);
        Assert.Equal(lease.SubscriptionSlot, renewed.SubscriptionSlot);
        Assert.True(owner.TryGetResource(id, out var renewedSnapshot));
        Assert.Equal(subscribed.SchedulingRevision + 1, renewedSnapshot!.SchedulingRevision);
        Assert.True(client.TryReleaseSubscription(renewed));
        Assert.True(owner.TryGetResource(id, out var released));
        Assert.Equal(0, released!.ActiveSubscriberCount);
        Assert.Equal(1.0d, released.SubscriptionMultiplier);
        Assert.Equal(renewedSnapshot.SchedulingRevision + 1, released.SchedulingRevision);
    }

    [Fact]
    public void PublicUseQueue_IsBaseScoreDescendingAndFifoForTies()
    {
        using var owner = CreateSession(resourceCapacity: 1, subscriptionCapacity: 3, taskCapacity: 8);
        var id = owner.Publish(CreatePublication(sizeBytes: 4096));
        var leaseDuration = TimeSpan.FromMinutes(1);

        var lowRequest = CreateUseRequest(id, 1, 1001, 5, leaseDuration);
        var firstHighRequest = CreateUseRequest(id, 2, 1002, 20, leaseDuration);
        var secondHighRequest = CreateUseRequest(id, 3, 1003, 20, leaseDuration);
        _ = SubscribeForUse(owner, lowRequest, leaseDuration);
        _ = SubscribeForUse(owner, firstHighRequest, leaseDuration);
        _ = SubscribeForUse(owner, secondHighRequest, leaseDuration);
        var low = owner.EnqueueUse(lowRequest);
        var firstHigh = owner.EnqueueUse(firstHighRequest);
        var secondHigh = owner.EnqueueUse(secondHighRequest);
        Assert.Equal(SharedResourceTaskState.Queued, low.State);
        Assert.Equal(SharedResourceTaskState.Queued, firstHigh.State);
        Assert.Equal(SharedResourceTaskState.Queued, secondHigh.State);

        Assert.True(owner.TryGetResource(id, out var queued));
        Assert.Equal(3, queued!.QueuedRequestCount);
        Assert.Equal(
            AdapterResourceActionMask.None,
            owner.FilterAllowedActions(id, DestructiveActions));

        AssertGrantOrder(owner, id, leaseDuration, 1002);
        AssertGrantOrder(owner, id, leaseDuration, 1003);
        AssertGrantOrder(owner, id, leaseDuration, 1001);
        Assert.False(owner.TryReserveNext(
            id,
            leaseDuration,
            out _));
    }

    [Fact]
    public void PublicUse_RequiresSubscriptionAndSubscriptionCannotLeaveQueuedWork()
    {
        using var owner = CreateHostSession(resourceCapacity: 1, subscriptionCapacity: 1, taskCapacity: 2);
        var id = owner.Publish(CreatePublication(sizeBytes: 4096));
        var duration = TimeSpan.FromMinutes(1);
        var request = CreateUseRequest(id, 1, 1001, 10, duration);
        var denied = Assert.Throws<SharedResourceNativeException>(() => owner.EnqueueUse(request));
        Assert.Equal(10, denied.ResultCode);
        Assert.Contains("AccessDenied", denied.Message, StringComparison.Ordinal);

        var subscription = SubscribeForUse(owner, request, duration);
        var task = owner.EnqueueUse(request);
        Assert.False(owner.TryReleaseSubscription(subscription));
        Assert.True(owner.TryCancelUse(task));
        Assert.True(owner.TryReleaseSubscription(subscription));
    }

    [Fact]
    public void DefaultHostPublicManager_UsesOnlySubscribersActivityAndHardTaskGate()
    {
        using var owner = CreateHostSession(resourceCapacity: 3, subscriptionCapacity: 2, taskCapacity: 4);
        var zero = owner.Publish(CreatePublication(4096));
        var subscribed = owner.Publish(CreatePublication(4096));
        var activeResource = owner.Publish(CreatePublication(4096));
        var duration = TimeSpan.FromMinutes(1);

        var subscribedUse = CreateUseRequest(subscribed, 1, 1001, 10, duration);
        _ = SubscribeForUse(owner, subscribedUse, duration);
        var activeUse = CreateUseRequest(activeResource, 2, 1002, 10, duration);
        _ = SubscribeForUse(owner, activeUse, duration);
        var activeTask = owner.EnqueueUse(activeUse);
        Assert.True(owner.TryReserveNext(activeResource, duration, out var reserved));
        var active = owner.BeginGrantedUse(reserved);
        owner.CompleteGrantedUse(active);

        var manager = new DefaultHostPublicResourceManager(owner);
        var normal = manager.Tick(default);
        var zeroCandidate = Assert.Single(normal.UnloadCandidates);
        Assert.Equal(zero, zeroCandidate.ResourceId);
        Assert.Equal(HostPublicResourceUnloadReason.ZeroSubscribers, zeroCandidate.Reason);

        var pressured = manager.Tick(new HostPublicResourceCapacityShortage(
            HostPublicResourceCapacityObservation.ShortageFresh(
                afterNormalReleaseRounds: true),
            HostPublicResourceCapacityObservation.HealthyFresh));
        Assert.Equal(3, pressured.UnloadCandidates.Count);
        Assert.Equal(zero, pressured.UnloadCandidates[0].ResourceId);
        Assert.Equal(subscribed, pressured.UnloadCandidates[1].ResourceId);
        Assert.Equal(activeResource, pressured.UnloadCandidates[2].ResourceId);
        Assert.Equal(28, pressured.UnloadCandidates[2].ActivityScore);

        var queued = owner.EnqueueUse(subscribedUse);
        var protectedPlan = manager.Tick(new HostPublicResourceCapacityShortage(
            HostPublicResourceCapacityObservation.ShortageFresh(
                afterNormalReleaseRounds: true),
            HostPublicResourceCapacityObservation.HealthyFresh));
        Assert.Equal(1, protectedPlan.ProtectedResourceCount);
        Assert.DoesNotContain(
            protectedPlan.UnloadCandidates,
            candidate => candidate.ResourceId == subscribed);
        Assert.True(owner.TryCancelUse(queued));
        Assert.False(owner.TryCancelUse(activeTask));
    }

    [Fact]
    public void DefaultHostPublicManager_CurrentPressureAloneDoesNotUnloadSubscribedResources()
    {
        using var owner = CreateHostSession(resourceCapacity: 2, subscriptionCapacity: 1, taskCapacity: 1);
        var zero = owner.Publish(CreatePublication(4096));
        var subscribed = owner.Publish(CreatePublication(4096));
        _ = SubscribeForUse(
            owner,
            CreateUseRequest(subscribed, 1, 1001, 10, TimeSpan.FromMinutes(1)),
            TimeSpan.FromMinutes(1));
        var manager = new DefaultHostPublicResourceManager(owner);

        var plan = manager.Tick(new HostPublicResourceCapacityShortage(
            HostPublicResourceCapacityObservation.ShortageFresh(),
            HostPublicResourceCapacityObservation.HealthyFresh));

        var candidate = Assert.Single(plan.UnloadCandidates);
        Assert.Equal(zero, candidate.ResourceId);
        Assert.Equal(0, plan.CapacityUnloadCount);
    }

    [Fact]
    public void DefaultHostPublicManager_FiltersGpuCapacityUnloadsByExactAdapter()
    {
        using var owner = CreateHostSession(
            resourceCapacity: 2,
            subscriptionCapacity: 2,
            taskCapacity: 2);
        var first = owner.Publish(CreatePublication(4096) with
        {
            AdapterKey = 101,
            Tier = AdapterResourceTier.Vram,
            Flags = SharedResourceFlags.ReadOnly | SharedResourceFlags.GpuBacked
        });
        var second = owner.Publish(CreatePublication(4096) with
        {
            AdapterKey = 202,
            Tier = AdapterResourceTier.Vram,
            Flags = SharedResourceFlags.ReadOnly | SharedResourceFlags.GpuBacked
        });
        var duration = TimeSpan.FromMinutes(1);
        _ = SubscribeForUse(
            owner,
            CreateUseRequest(first, 1, 1001, 10, duration),
            duration);
        _ = SubscribeForUse(
            owner,
            CreateUseRequest(second, 2, 1002, 10, duration),
            duration);
        var manager = new DefaultHostPublicResourceManager(owner);

        var exactPressure = manager.Tick(new HostPublicResourceCapacityShortage(
            HostPublicResourceCapacityObservation.HealthyFresh,
            HostPublicResourceCapacityObservation.ShortageFresh(
                afterNormalReleaseRounds: true),
            [
                new(
                    101,
                    HostPublicResourceCapacityObservation.ShortageFresh(
                        afterNormalReleaseRounds: true)),
                new(
                    202,
                    HostPublicResourceCapacityObservation.HealthyFresh)
            ]));

        var candidate = Assert.Single(exactPressure.UnloadCandidates);
        Assert.Equal(first, candidate.ResourceId);
        Assert.Equal(1, exactPressure.CapacityUnloadCount);

        var summaryOnly = manager.Tick(new HostPublicResourceCapacityShortage(
            HostPublicResourceCapacityObservation.HealthyFresh,
            HostPublicResourceCapacityObservation.ShortageFresh(
                afterNormalReleaseRounds: true)));
        Assert.Empty(summaryOnly.UnloadCandidates);
        Assert.Equal(0, summaryOnly.CapacityUnloadCount);
    }

    [Fact]
    public void DefaultHostPublicManager_RecallsSameIdsByDomainAndRetention()
    {
        using var owner = CreateHostSession(
            resourceCapacity: 3,
            subscriptionCapacity: 3,
            taskCapacity: 4);
        var activeMemory = owner.Publish(CreatePublication(4096));
        var idleMemory = owner.Publish(CreatePublication(4096));
        var idleGpu = owner.Publish(CreatePublication(4096) with
        {
            AdapterKey = 101,
            Tier = AdapterResourceTier.Vram,
            Flags = SharedResourceFlags.ReadOnly | SharedResourceFlags.GpuBacked
        });
        var duration = TimeSpan.FromMinutes(1);
        var activeRequest = CreateUseRequest(activeMemory, 1, 1001, 10, duration);
        _ = SubscribeForUse(owner, activeRequest, duration);
        _ = SubscribeForUse(
            owner,
            CreateUseRequest(idleMemory, 2, 1002, 10, duration),
            duration);
        _ = SubscribeForUse(
            owner,
            CreateUseRequest(idleGpu, 3, 1003, 10, duration),
            duration);
        var task = owner.EnqueueUse(activeRequest);
        Assert.True(owner.TryReserveNext(activeMemory, duration, out var reserved));
        owner.CompleteGrantedUse(owner.BeginGrantedUse(reserved));
        Assert.False(owner.TryCancelUse(task));

        CommitUnavailable(owner, activeMemory, CreatePublication(0) with
        {
            PublicResourceId = activeMemory.PublicResourceId,
            PayloadMappingId = 0,
            PayloadGeneration = 2,
            Availability = SharedResourceAvailability.Unavailable
        });
        CommitUnavailable(owner, idleMemory, CreatePublication(0) with
        {
            PublicResourceId = idleMemory.PublicResourceId,
            PayloadMappingId = 0,
            PayloadGeneration = 2,
            Availability = SharedResourceAvailability.Unavailable
        });
        CommitUnavailable(owner, idleGpu, CreatePublication(0) with
        {
            PublicResourceId = idleGpu.PublicResourceId,
            PayloadMappingId = 0,
            PayloadGeneration = 2,
            AdapterKey = 101,
            Tier = AdapterResourceTier.Vram,
            Availability = SharedResourceAvailability.Unavailable,
            Flags = SharedResourceFlags.ReadOnly | SharedResourceFlags.GpuBacked
        });

        var manager = new DefaultHostPublicResourceManager(owner);
        Assert.Empty(manager.Tick(default).RecallCandidates);
        Assert.Empty(manager.Tick(new HostPublicResourceCapacityShortage(
            HostPublicResourceCapacityObservation.Disabled,
            HostPublicResourceCapacityObservation.Disabled)).RecallCandidates);

        var currentVideoPressure = manager.Tick(new HostPublicResourceCapacityShortage(
            HostPublicResourceCapacityObservation.HealthyFresh,
            HostPublicResourceCapacityObservation.ShortageFresh()));
        Assert.Equal(
            [activeMemory, idleMemory],
            currentVideoPressure.RecallCandidates
                .Select(static candidate => candidate.ResourceId)
                .ToArray());

        var videoBlocked = manager.Tick(new HostPublicResourceCapacityShortage(
            HostPublicResourceCapacityObservation.HealthyFresh,
            HostPublicResourceCapacityObservation.ShortageFresh(
                afterNormalReleaseRounds: true)));
        Assert.Equal(
            [activeMemory, idleMemory],
            videoBlocked.RecallCandidates
                .Select(static candidate => candidate.ResourceId)
                .ToArray());
        Assert.True(
            videoBlocked.RecallCandidates[0].RetentionScoreQ16
            > videoBlocked.RecallCandidates[1].RetentionScoreQ16);

        var memoryBlocked = manager.Tick(new HostPublicResourceCapacityShortage(
            HostPublicResourceCapacityObservation.ShortageFresh(
                afterNormalReleaseRounds: true),
            HostPublicResourceCapacityObservation.HealthyFresh,
            [
                new(
                    101,
                    HostPublicResourceCapacityObservation.HealthyFresh)
            ]));
        var gpuRecall = Assert.Single(memoryBlocked.RecallCandidates);
        Assert.Equal(idleGpu, gpuRecall.ResourceId);
        Assert.Equal(101UL, gpuRecall.AdapterKey);

        var wrongAdapterHealthy = manager.Tick(new HostPublicResourceCapacityShortage(
            HostPublicResourceCapacityObservation.ShortageFresh(
                afterNormalReleaseRounds: true),
            HostPublicResourceCapacityObservation.HealthyFresh,
            [
                new(
                    202,
                    HostPublicResourceCapacityObservation.HealthyFresh)
            ]));
        Assert.Empty(wrongAdapterHealthy.RecallCandidates);
    }

    [Fact]
    public void CapacityObservation_RejectsReleaseProofWithoutFreshShortage()
    {
        Assert.Throws<ArgumentException>(() =>
            new HostPublicResourceCapacityObservation(
                HostPublicResourceCapacityObservationState.HealthyFresh,
                afterNormalReleaseRounds: true));
        Assert.Throws<ArgumentException>(() =>
            new HostPublicResourceCapacityObservation(
                HostPublicResourceCapacityObservationState.UnknownOrStale,
                afterNormalReleaseRounds: true));
        Assert.Throws<ArgumentException>(() =>
            new HostPublicResourceCapacityShortage(
                HostPublicResourceCapacityObservation.HealthyFresh,
                HostPublicResourceCapacityObservation.HealthyFresh,
                [
                    new(
                        101,
                        HostPublicResourceCapacityObservation.ShortageFresh())
                ]));
        Assert.Throws<ArgumentException>(() =>
            new HostPublicResourceCapacityShortage(
                HostPublicResourceCapacityObservation.HealthyFresh,
                HostPublicResourceCapacityObservation.HealthyFresh,
                [
                    new(
                        101,
                        HostPublicResourceCapacityObservation.HealthyFresh),
                    new(
                        101,
                        HostPublicResourceCapacityObservation.HealthyFresh)
                ]));
    }

    [Fact]
    public void RecallTransaction_AtomicallyRejectsStaleSubscriptionsAndPreservesId()
    {
        using var owner = CreateHostSession(
            resourceCapacity: 1,
            subscriptionCapacity: 2,
            taskCapacity: 2);
        var id = owner.Publish(CreatePublication(4096));
        var duration = TimeSpan.FromMinutes(1);
        _ = SubscribeForUse(
            owner,
            CreateUseRequest(id, 1, 1001, 10, duration),
            duration);
        Assert.True(owner.TryGetResource(id, out var loaded));
        var unloadToken = owner.BeginDestructiveExpected(
            new SharedResourceDestructiveExpected(
                id,
                loaded!.SchedulingRevision,
                new SharedResourceAuthorityId(8001, 8002),
                AdapterResourceActionMask.Discard));
        owner.CommitDestructive(unloadToken, CreatePublication(0) with
        {
            PublicResourceId = id.PublicResourceId,
            PayloadMappingId = 0,
            PayloadGeneration = 2,
            Availability = SharedResourceAvailability.Unavailable
        });
        Assert.True(owner.TryGetResource(id, out var unloaded));

        var stale = new SharedResourceRecallExpected(
            id,
            unloaded!.SchedulingRevision,
            unloaded.GateEpoch,
            new SharedResourceAuthorityId(8101, 8102),
            unloaded.PayloadGeneration,
            unloaded.ActiveSubscriberCount,
            unloaded.ActivityScore,
            unloaded.Flags);
        _ = SubscribeForUse(
            owner,
            CreateUseRequest(id, 2, 1002, 10, duration),
            duration);
        Assert.Throws<SharedResourceNativeException>(() =>
            owner.BeginRecallExpected(stale));

        Assert.True(owner.TryGetResource(id, out unloaded));
        var token = owner.BeginRecallExpected(stale with
        {
            SchedulingRevision = unloaded!.SchedulingRevision,
            GateEpoch = unloaded.GateEpoch,
            SubscriberCount = unloaded.ActiveSubscriberCount,
            ActivityScore = unloaded.ActivityScore
        });
        Assert.True(owner.TryGetResource(id, out var preparing));
        Assert.Equal(id, preparing!.Id);
        Assert.Equal(SharedResourceAvailability.Preparing, preparing.Availability);
        Assert.True(preparing.DestructiveActionActive);

        owner.CommitRecall(token, CreatePublication(4096) with
        {
            PublicResourceId = id.PublicResourceId,
            PayloadMappingId = 99,
            PayloadGeneration = 3,
            Availability = SharedResourceAvailability.Available
        });
        Assert.True(owner.TryGetResource(id, out var restored));
        Assert.Equal(id, restored!.Id);
        Assert.Equal(SharedResourceAvailability.Available, restored.Availability);
        Assert.Equal(3UL, restored.PayloadGeneration);
        Assert.False(restored.DestructiveActionActive);
    }

    [Fact]
    public void QueuedReservedAndActiveUse_AllBlockDestructiveMutation()
    {
        using var owner = CreateSession(resourceCapacity: 1, subscriptionCapacity: 1, taskCapacity: 8);
        var id = owner.Publish(CreatePublication(sizeBytes: 4096));
        var leaseDuration = TimeSpan.FromMinutes(1);
        var request = CreateUseRequest(id, 1, 1001, 10, leaseDuration);
        var subscription = SubscribeForUse(owner, request, leaseDuration);
        var queued = owner.EnqueueUse(request);
        Assert.False(owner.TryRevoke(id));

        Assert.True(owner.TryReserveNext(
            id,
            leaseDuration,
            out var reserved));
        Assert.False(owner.TryRevoke(id));
        var active = owner.BeginGrantedUse(reserved);
        Assert.False(owner.TryRevoke(id));
        owner.CompleteGrantedUse(active);

        Assert.True(owner.TryReleaseSubscription(subscription));
        Assert.True(owner.TryRevoke(id));
        Assert.False(owner.TryCancelUse(queued));
    }

    [Fact]
    public void DestructiveAuthority_RequiresExactTokenAndTypedCompletion()
    {
        using var owner = CreateSession(resourceCapacity: 2, subscriptionCapacity: 0, taskCapacity: 4);
        var id = owner.Publish(CreatePublication(sizeBytes: 4096));
        Assert.True(owner.TryGetResource(id, out var initial));
        Assert.NotNull(initial);
        var attempt = new SharedResourceAuthorityId(7001, 7002);
        var token = owner.BeginDestructiveExpected(new SharedResourceDestructiveExpected(
            id,
            initial.SchedulingRevision,
            attempt,
            AdapterResourceActionMask.Discard));

        Assert.True(owner.TryGetResource(id, out var gated));
        Assert.NotNull(gated);
        Assert.True(gated.DestructiveActionActive);
        Assert.Equal(
            AdapterResourceActionMask.None,
            gated.AllowedActions & DestructiveActions);

        var wrongAttempt = token with
        {
            ActionAttemptId = new SharedResourceAuthorityId(
                token.ActionAttemptId.High,
                token.ActionAttemptId.Low + 1)
        };
        Assert.Throws<SharedResourceNativeException>(() =>
            owner.CommitDestructive(
                wrongAttempt,
                CreatePublication(sizeBytes: 2048)));
        owner.CommitDestructive(token, CreatePublication(sizeBytes: 2048));

        Assert.True(owner.TryGetResource(id, out var committed));
        Assert.NotNull(committed);
        Assert.Equal(initial.SchedulingRevision + 1, committed.SchedulingRevision);
        Assert.Equal(2048UL, committed.SizeBytes);
        Assert.False(committed.DestructiveActionActive);
        Assert.Throws<SharedResourceNativeException>(() =>
            owner.CommitDestructive(token, CreatePublication(sizeBytes: 1024)));

        var abortId = owner.Publish(CreatePublication(sizeBytes: 8192) with
        {
            ResourceKey = 102,
            ResourceId = 8
        });
        Assert.True(owner.TryGetResource(abortId, out var abortInitial));
        Assert.NotNull(abortInitial);
        var abortAttempt = new SharedResourceAuthorityId(7101, 7102);
        var abortToken = owner.BeginDestructiveExpected(new SharedResourceDestructiveExpected(
            abortId,
            abortInitial.SchedulingRevision,
            abortAttempt,
            AdapterResourceActionMask.Trim));
        owner.AbortDestructive(
            abortToken,
            new SharedResourceDestructiveNoEffectReceipt(
                abortAttempt,
                new SharedResourceAuthorityId(7201, 7202),
                NativeSharedResourceSession.MonotonicNow(),
                SharedResourceNoEffectProof.NotInvoked,
                SharedResourceNoEffectReason.ExecutorRejectedBeforeEffect));
        Assert.True(owner.TryGetResource(abortId, out var aborted));
        Assert.NotNull(aborted);
        Assert.Equal(abortInitial.SchedulingRevision, aborted.SchedulingRevision);
        Assert.False(aborted.DestructiveActionActive);
    }

    [Fact]
    public void SharedResourceAuthorityV11_HasFixedAbiSizesOffsetsAndNoPrivateUseSurface()
    {
        Assert.Equal(11U, SharedResourceProtocol.Version);
        Assert.Equal(
            0x8DF79DD673189108UL,
            NativeSharedResourceSession.PublishedLayoutFingerprint);
        var assembly = typeof(NativeSharedResourceSession).Assembly;
        var config = assembly.GetType(
            "ResourceManager.Adapter.SharedMemory.NativeLedgerConfig",
            throwOnError: true)!;
        Assert.Equal(96, Marshal.SizeOf(config));
        Assert.Equal(60, Marshal.OffsetOf(config, "LeaseClockDomain").ToInt32());
        Assert.Equal(64, Marshal.OffsetOf(config, "LeaseClockFrequencyHz").ToInt32());
        Assert.Equal(72, Marshal.OffsetOf(config, "MaximumSubscriptionTtl").ToInt32());
        Assert.Equal(80, Marshal.OffsetOf(config, "MaximumQueueTtl").ToInt32());
        Assert.Equal(88, Marshal.OffsetOf(config, "MaximumGrantTtl").ToInt32());

        var subscriptionRequest = assembly.GetType(
            "ResourceManager.Adapter.SharedMemory.NativeSubscriptionRequest",
            throwOnError: true)!;
        Assert.Equal(88, Marshal.SizeOf(subscriptionRequest));
        Assert.Equal(64, Marshal.OffsetOf(subscriptionRequest, "LeaseDuration").ToInt32());

        var useRequest = assembly.GetType(
            "ResourceManager.Adapter.SharedMemory.NativeUseRequest",
            throwOnError: true)!;
        Assert.Equal(104, Marshal.SizeOf(useRequest));
        Assert.Equal(80, Marshal.OffsetOf(useRequest, "QueueLeaseDuration").ToInt32());

        var publication = assembly.GetType(
            "ResourceManager.Adapter.SharedMemory.NativeResourcePublication",
            throwOnError: true)!;
        Assert.Equal(176, Marshal.SizeOf(publication));
        Assert.Equal(24, Marshal.OffsetOf(publication, "OwnerInstanceIdLow").ToInt32());
        Assert.Equal(40, Marshal.OffsetOf(publication, "OwnerContextGeneration").ToInt32());
        Assert.Equal(72, Marshal.OffsetOf(publication, "ExecutorIdLow").ToInt32());
        Assert.Equal(88, Marshal.OffsetOf(publication, "ResourceKey").ToInt32());
        Assert.Equal(96, Marshal.OffsetOf(publication, "AdapterKey").ToInt32());

        var snapshot = assembly.GetType(
            "ResourceManager.Adapter.SharedMemory.NativeResourceSnapshot",
            throwOnError: true)!;
        Assert.Equal(240, Marshal.SizeOf(snapshot));
        Assert.Equal(48, Marshal.OffsetOf(snapshot, "OwnerInstanceIdLow").ToInt32());
        Assert.Equal(96, Marshal.OffsetOf(snapshot, "ExecutorIdLow").ToInt32());
        Assert.Equal(120, Marshal.OffsetOf(snapshot, "AdapterKey").ToInt32());
        Assert.Equal(200, Marshal.OffsetOf(snapshot, "SchedulingRevision").ToInt32());
        Assert.Equal(208, Marshal.OffsetOf(snapshot, "GateEpoch").ToInt32());
        Assert.Null(snapshot.GetField("PrivateUseCount"));
        Assert.Null(assembly.GetType(
            "ResourceManager.Adapter.SharedMemory.NativePrivateUseReceipt",
            throwOnError: false));

        var nativeLibrary = NativeLibrary.Load(
            Path.Combine(AppContext.BaseDirectory, "ResourceManager.Adapter.Native.dll"));
        try
        {
            Assert.False(NativeLibrary.TryGetExport(
                nativeLibrary,
                "rm_shared_private_use_begin",
                out _));
            Assert.False(NativeLibrary.TryGetExport(
                nativeLibrary,
                "rm_shared_private_use_end",
                out _));
        }
        finally
        {
            NativeLibrary.Free(nativeLibrary);
        }

        var expected = assembly.GetType(
            "ResourceManager.Adapter.SharedMemory.NativeDestructiveExpectedRequest",
            throwOnError: true)!;
        Assert.Equal(72, Marshal.SizeOf(expected));
        Assert.Equal(40, Marshal.OffsetOf(expected, "SchedulingRevision").ToInt32());
        Assert.Equal(48, Marshal.OffsetOf(expected, "ActionAttemptIdLow").ToInt32());
        var token = assembly.GetType(
            "ResourceManager.Adapter.SharedMemory.NativeDestructiveToken",
            throwOnError: true)!;
        Assert.Equal(72, Marshal.SizeOf(token));
        Assert.Equal(32, Marshal.OffsetOf(token, "SchedulingRevision").ToInt32());
        Assert.Equal(48, Marshal.OffsetOf(token, "ActionAttemptIdLow").ToInt32());
        var noEffect = assembly.GetType(
            "ResourceManager.Adapter.SharedMemory.NativeDestructiveNoEffectReceipt",
            throwOnError: true)!;
        Assert.Equal(56, Marshal.SizeOf(noEffect));
        Assert.Equal(40, Marshal.OffsetOf(noEffect, "ObservedMonotonicTimestamp").ToInt32());

    }

    [Fact]
    public void PublicLeaseDurations_AreBoundedBeforeEnteringNativeCode()
    {
        using var owner = CreateSession(resourceCapacity: 1, subscriptionCapacity: 1, taskCapacity: 2);
        var id = owner.Publish(CreatePublication(4096));
        var maximum = TimeSpan.FromMinutes(5);
        var overMaximum = maximum + TimeSpan.FromTicks(1);

        Assert.Equal(
            SharedResourceLeaseClockDomain.WindowsPerformanceCounter,
            owner.Descriptor.LeaseClockDomain);
        Assert.Equal(
            checked((ulong)System.Diagnostics.Stopwatch.Frequency),
            owner.Descriptor.LeaseClockFrequency);
        Assert.Equal(
            checked((ulong)System.Diagnostics.Stopwatch.Frequency * 300UL),
            owner.Descriptor.MaximumSubscriptionTtl);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            owner.Subscribe(
                id,
                subscriberApplicationKey: 701,
                subscriberInstanceId: 7001,
                subscriberSessionId: 8001,
                subscriberProcessId: Environment.ProcessId,
                overMaximum,
                SharedResourceSubscriptionIntent.ReadySoon));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            owner.EnqueueUse(CreateUseRequest(id, 1, 1001, 10, overMaximum)));

        var use = CreateUseRequest(id, 2, 1002, 10, maximum);
        _ = SubscribeForUse(owner, use, maximum);
        owner.EnqueueUse(use);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            owner.TryReserveNext(id, overMaximum, out _));
    }

    [Fact]
    public void AuthorityIdentity_WithOnlyHighHalf_RoundTripsWithoutImplicitLowValue()
    {
        using var owner = CreateSession(resourceCapacity: 1, subscriptionCapacity: 0, taskCapacity: 2);
        var publication = CreatePublication(4096) with
        {
            OwnerInstanceId = new SharedResourceAuthorityId(0xA001, 0),
            ExecutorId = new SharedResourceAuthorityId(0xB001, 0)
        };
        var id = owner.Publish(publication);
        Assert.True(owner.TryGetResource(id, out var snapshot));
        Assert.NotNull(snapshot);
        Assert.Equal(publication.OwnerInstanceId, snapshot.OwnerInstanceId);
        Assert.Equal(publication.ExecutorId, snapshot.ExecutorId);
        Assert.Equal(0UL, snapshot.OwnerInstanceId.Low);
        Assert.Equal(0UL, snapshot.ExecutorId.Low);
    }


    private static void AssertGrantOrder(
        NativeSharedResourceSession session,
        SharedResourceId id,
        TimeSpan leaseDuration,
        ulong expectedInstanceId)
    {
        Assert.True(session.TryReserveNext(
            id,
            leaseDuration,
            out var reserved));
        Assert.Equal(expectedInstanceId, reserved.Task.RequesterInstanceId);
        var active = session.BeginGrantedUse(reserved);
        session.CompleteGrantedUse(active);
    }

    private static SharedResourceSubscriptionReceipt SubscribeForUse(
        NativeSharedResourceSession session,
        SharedResourceUseRequest request,
        TimeSpan leaseDuration)
        => session.Subscribe(
            request.ResourceId,
            request.RequesterApplicationKey,
            request.RequesterInstanceId,
            request.RequesterSessionId,
            request.RequesterProcessId,
            leaseDuration,
            SharedResourceSubscriptionIntent.ReadySoon);

    private static NativeSharedResourceSession CreateSession(
        int resourceCapacity,
        int subscriptionCapacity,
        int taskCapacity)
    {
        var nonce = Guid.NewGuid().ToString("N");
        return NativeSharedResourceSession.Create(
            $"Local\\ResourceManager.Tests.SharedResource.Ledger.{nonce}",
            $"Local\\ResourceManager.Tests.SharedResource.Mutex.{nonce}",
            new SharedResourceSessionOptions(
                resourceCapacity,
                subscriptionCapacity,
                taskCapacity,
                SubscriptionCoefficient: 0.25d,
                OwnerApplicationKey: 41,
                OwnerProcessId: Environment.ProcessId,
                OwnerProcessCreatedAt: DateTimeOffset.UtcNow,
                MaximumSubscriptionLeaseDuration: TimeSpan.FromMinutes(5),
                MaximumQueueDuration: TimeSpan.FromMinutes(5),
                MaximumGrantDuration: TimeSpan.FromMinutes(5)));
    }

    private static NativeSharedResourceSession CreateHostSession(
        int resourceCapacity,
        int subscriptionCapacity,
        int taskCapacity)
    {
        var nonce = Guid.NewGuid().ToString("N");
        return NativeSharedResourceSession.Create(
            $"Local\\ResourceManager.Tests.HostSharedResource.Ledger.{nonce}",
            $"Local\\ResourceManager.Tests.HostSharedResource.Mutex.{nonce}",
            new SharedResourceSessionOptions(
                resourceCapacity,
                subscriptionCapacity,
                taskCapacity,
                SubscriptionCoefficient: 0.25d,
                OwnerApplicationKey: 8002,
                OwnerProcessId: Environment.ProcessId,
                OwnerProcessCreatedAt: DateTimeOffset.UtcNow,
                MaximumSubscriptionLeaseDuration: TimeSpan.FromMinutes(5),
                MaximumQueueDuration: TimeSpan.FromMinutes(5),
                MaximumGrantDuration: TimeSpan.FromMinutes(5)));
    }

    private static SharedResourcePublication CreatePublication(ulong sizeBytes)
        => new(
            PublicResourceId: 0,
            OwnerApplicationKey: 41,
            OwnerInstanceId: new SharedResourceAuthorityId(98, 99),
            OwnerContextGeneration: 1,
            LeaseGeneration: 2,
            BindingGeneration: 3,
            CapabilityGeneration: 4,
            ExecutorId: new SharedResourceAuthorityId(100, 101),
            OwnerProcessId: Environment.ProcessId,
            ResourceKey: 101,
            AdapterKey: 0,
            ResourceId: 7,
            SizeBytes: sizeBytes,
            ContentIdentityHash: 33,
            PayloadMappingId: 55,
            PayloadGeneration: 1,
            MaxParallelGrants: 1,
            AdapterResourceTier.PhysicalMemory,
            AdapterResourceKind.ModelWeights,
            AdapterResourceRecoveryKind.DiskCopy,
            AdapterResourceGranularity.PartialUsable,
            AdapterResourceActionMask.None,
            AdapterResourceActionRoute.AdapterHandler,
            AdapterResourceDemandMask.None,
            AdapterSoftwareSurfaceState.PureBackground,
            SharedResourceAvailability.Available,
            SharedResourceFlags.ReadOnly);

    private static void CommitUnavailable(
        NativeSharedResourceSession owner,
        SharedResourceId resourceId,
        SharedResourcePublication publication)
    {
        Assert.True(owner.TryGetResource(resourceId, out var current));
        var token = owner.BeginDestructiveExpected(new SharedResourceDestructiveExpected(
            resourceId,
            current!.SchedulingRevision,
            new SharedResourceAuthorityId(resourceId.ResourceGeneration, resourceId.PublicResourceId),
            AdapterResourceActionMask.Discard));
        owner.CommitDestructive(token, publication);
    }

    private static SharedResourceUseRequest CreateUseRequest(
        SharedResourceId id,
        ulong requestKey,
        ulong requesterInstanceId,
        double baseScore,
        TimeSpan leaseDuration)
        => new(
            id,
            requestKey,
            RequesterApplicationKey: requesterInstanceId + 10_000,
            requesterInstanceId,
            RequesterSessionId: requesterInstanceId + 20_000,
            RequesterProcessId: Environment.ProcessId,
            ScorePlanGeneration: 1,
            baseScore,
            leaseDuration);
}
