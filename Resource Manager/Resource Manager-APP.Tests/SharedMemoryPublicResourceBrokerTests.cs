using System.Reflection;
using ResourceManager.Adapter;
using ResourceManager.Adapter.SharedMemory;
using ResourceManager.App.Application.PublicResources;
using ResourceManager.App.Application.Settings;
using ResourceManager.App.Domain.PublicResources;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.PublicResources;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;

namespace Resource_Manager_APP.Tests;

public sealed class SharedMemoryPublicResourceBrokerTests
{

    [Fact]
    public async Task Capability_RemainsUnavailableUntilAHostPublisherSucceeds()
    {
        using var broker = CreateBroker();
        await broker.StartAsync(CancellationToken.None);
        var capability = (IHostPublicResourceCapability)broker;

        var before = capability.GetCapability();
        Assert.False(before.Available);
        Assert.Equal(HostPublicResourceCapabilityStates.Unavailable, before.State);

        _ = broker.PublishHostResource(CreateDefinition(), new TestHostLifecycle());

        var after = capability.GetCapability();
        Assert.True(after.Available);
        Assert.Equal(HostPublicResourceCapabilityStates.Available, after.State);

        await broker.StopAsync(CancellationToken.None);
        Assert.False(capability.GetCapability().Available);
    }

    [Fact]
    public async Task HostPublication_RequiresExactGpuAdapterIdentity()
    {
        using var broker = CreateBroker();
        await broker.StartAsync(CancellationToken.None);

        Assert.Throws<ArgumentException>(() => broker.PublishHostResource(
            CreateDefinition() with
            {
                Tier = AdapterResourceTier.Vram,
                Flags = SharedResourceFlags.ReadOnly | SharedResourceFlags.GpuBacked
            },
            new TestHostLifecycle()));
        Assert.Throws<ArgumentException>(() => broker.PublishHostResource(
            CreateDefinition() with { AdapterKey = 101 },
            new TestHostLifecycle()));

        await broker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task HostResourceLifecycle_IsProjectedThroughReadOnlyQueries()
    {
        using var broker = CreateBroker();
        await broker.StartAsync(CancellationToken.None);
        var definition = CreateDefinition();

        var id = broker.PublishHostResource(definition, new TestHostLifecycle());
        var catalog = ((IPublicResourceDirectoryQueries)broker).GetCatalog();

        var resource = Assert.Single(catalog.Resources);
        Assert.Equal(id.PublicResourceId, resource.PublicResourceId);
        Assert.Equal(broker.HostOwnerApplicationKey, resource.OwnerApplicationKey);
        Assert.Equal(0, resource.ActiveSubscriberCount);
        Assert.Equal(0, resource.QueuedRequestCount);
        Assert.Equal(0, resource.ReservedGrantCount);
        Assert.Equal(0, resource.ActiveUseCount);

        var transport = ((IPublicResourceDirectoryQueries)broker).GetTransportSummary();
        Assert.Equal("shared-memory-zig", transport.Transport);
        Assert.False(transport.DirectMappingAvailable);
        Assert.Equal("broker-query-only", transport.AccessMode);

        Assert.True(broker.TryUpdateHostResource(id, definition with { SizeBytes = 8192 }));
        Assert.Equal(
            8192UL,
            ((IPublicResourceDirectoryQueries)broker).Find(id.PublicResourceId)!.SizeBytes);
        Assert.True(broker.RemoveHostResource(id));
        Assert.Null(((IPublicResourceDirectoryQueries)broker).Find(id.PublicResourceId));

        await broker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task OrdinaryHostUpdateCannotBypassUnloadOrRecallLifecycle()
    {
        using var broker = CreateBroker();
        await broker.StartAsync(CancellationToken.None);
        var definition = CreateDefinition();
        var id = broker.PublishHostResource(definition, new TestHostLifecycle());

        Assert.False(broker.TryUpdateHostResource(
            id,
            definition with
            {
                SizeBytes = 0,
                PayloadMappingId = 0,
                PayloadGeneration = 1,
                Availability = SharedResourceAvailability.Unavailable
            }));
        var stillAvailable = ((IPublicResourceDirectoryQueries)broker).Find(
            id.PublicResourceId);
        Assert.NotNull(stillAvailable);
        Assert.Equal(SharedResourceAvailability.Available, stillAvailable!.Availability);
        Assert.Equal(4096UL, stillAvailable.SizeBytes);

        var unloaded = Tick(
            (IHostPublicResourceSelfManager)broker,
            HealthyCapacity);
        Assert.Equal(1, unloaded.UnloadedCount);
        Assert.False(broker.TryUpdateHostResource(id, definition));
        var stillUnloaded = ((IPublicResourceDirectoryQueries)broker).Find(
            id.PublicResourceId);
        Assert.NotNull(stillUnloaded);
        Assert.Equal(SharedResourceAvailability.Unavailable, stillUnloaded!.Availability);
        Assert.Equal(0UL, stillUnloaded.SizeBytes);

        await broker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task OrdinaryHostUpdateCannotRebindGpuAdapterIdentity()
    {
        using var broker = CreateBroker();
        await broker.StartAsync(CancellationToken.None);
        var definition = CreateDefinition() with
        {
            AdapterKey = 101,
            Tier = AdapterResourceTier.Vram,
            Flags = SharedResourceFlags.ReadOnly | SharedResourceFlags.GpuBacked
        };
        var id = broker.PublishHostResource(definition, new TestHostLifecycle());

        Assert.False(broker.TryUpdateHostResource(
            id,
            definition with { AdapterKey = 202 }));
        Assert.False(broker.TryUpdateHostResource(
            id,
            definition with
            {
                AdapterKey = 0,
                Tier = AdapterResourceTier.PhysicalMemory,
                Flags = SharedResourceFlags.ReadOnly
            }));

        var unchanged = ((IPublicResourceDirectoryQueries)broker).Find(
            id.PublicResourceId);
        Assert.NotNull(unchanged);
        Assert.Equal(101UL, unchanged!.AdapterKey);
        Assert.True(unchanged.Flags.HasFlag(SharedResourceFlags.GpuBacked));

        await broker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task HostPublicSelfManager_UnloadsZeroSubscribersThenPressureCandidates()
    {
        using var broker = CreateBroker();
        await broker.StartAsync(CancellationToken.None);
        var lifecycle = new RecordingHostLifecycle();
        var zeroDefinition = CreateDefinition();
        var zeroSubscriber = broker.PublishHostResource(
            zeroDefinition,
            lifecycle);
        lifecycle.Track(zeroSubscriber, zeroDefinition);
        var subscribedDefinition = CreateDefinition() with
        {
            ResourceKey = 502,
            ResourceId = 6,
            ContentIdentityHash = 12
        };
        var subscribed = broker.PublishHostResource(
            subscribedDefinition,
            lifecycle);
        lifecycle.Track(subscribed, subscribedDefinition);
        var subscriber = GetSession(broker);
        var subscription = subscriber.Subscribe(
            subscribed,
            subscriberApplicationKey: 81,
            subscriberInstanceId: 9001,
            subscriberSessionId: 7001,
            subscriberProcessId: Environment.ProcessId,
            leaseDuration: TimeSpan.FromSeconds(10),
            SharedResourceSubscriptionIntent.ReadySoon);

        var manager = (IHostPublicResourceSelfManager)broker;
        var ordinary = Tick(manager, HealthyCapacity);
        Assert.Equal(1, ordinary.PlannedCount);
        Assert.Equal(1, ordinary.UnloadedCount);
        Assert.Equal(0, ordinary.RecalledCount);
        Assert.Equal(0, ordinary.RejectedCount);
        Assert.Equal(1U, ordinary.NewEffectAttemptCount);
        Assert.Equal(0U, ordinary.RecoveryAttemptCount);
        Assert.Equal(4096UL, ordinary.ReleasedBytes);
        var unloaded = ((IPublicResourceDirectoryQueries)broker).Find(
            zeroSubscriber.PublicResourceId);
        Assert.NotNull(unloaded);
        Assert.Equal(SharedResourceAvailability.Unavailable, unloaded!.Availability);
        Assert.Equal(0UL, unloaded.SizeBytes);
        var stillResident = ((IPublicResourceDirectoryQueries)broker).Find(
            subscribed.PublicResourceId);
        Assert.NotNull(stillResident);
        Assert.Equal(
            SharedResourceAvailability.Available,
            stillResident!.Availability);

        var pressured = Tick(manager, MemoryShortageAfterNormalReleaseRounds);
        Assert.Equal(1, pressured.PlannedCount);
        Assert.Equal(1, pressured.UnloadedCount);
        Assert.Equal(0, pressured.RecalledCount);
        Assert.Equal(1U, pressured.NewEffectAttemptCount);
        Assert.Equal(8192UL, ordinary.ReleasedBytes + pressured.ReleasedBytes);
        Assert.Equal(
            [
                HostPublicResourceUnloadReason.ZeroSubscribers,
                HostPublicResourceUnloadReason.CapacityShortage
            ],
            lifecycle.Contexts.Select(static item => item.Reason).ToArray());
        Assert.False(broker.RemoveHostResource(subscribed));

        var recalled = Tick(manager, HealthyCapacity);
        Assert.Equal(1, recalled.PlannedCount);
        Assert.Equal(0, recalled.UnloadedCount);
        Assert.Equal(1, recalled.RecalledCount);
        Assert.Equal(0, recalled.RejectedCount);
        Assert.Equal(1U, recalled.NewEffectAttemptCount);
        Assert.Equal(4096UL, recalled.RestoredBytes);
        var restored = ((IPublicResourceDirectoryQueries)broker).Find(
            subscribed.PublicResourceId);
        Assert.NotNull(restored);
        Assert.Equal(SharedResourceAvailability.Available, restored!.Availability);
        Assert.Equal(4096UL, restored.SizeBytes);
        Assert.Equal(subscribed, Assert.Single(lifecycle.RecallContexts).ResourceId);
        Assert.True(subscriber.TryReleaseSubscription(subscription));
        Assert.True(broker.RemoveHostResource(subscribed));

        await broker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task HostPublicSelfManager_RecoversUncertainRecallWithoutChangingId()
    {
        using var broker = CreateBroker();
        await broker.StartAsync(CancellationToken.None);
        var definition = CreateDefinition();
        var lifecycle = new RecoveringRecallLifecycle(definition);
        var id = broker.PublishHostResource(definition, lifecycle);
        lifecycle.Track(id);
        var subscriber = GetSession(broker);
        var subscription = subscriber.Subscribe(
            id,
            subscriberApplicationKey: 81,
            subscriberInstanceId: 9001,
            subscriberSessionId: 7001,
            subscriberProcessId: Environment.ProcessId,
            leaseDuration: TimeSpan.FromSeconds(10),
            SharedResourceSubscriptionIntent.ReadySoon);

        var manager = (IHostPublicResourceSelfManager)broker;
        var unloaded = Tick(manager, MemoryShortageAfterNormalReleaseRounds);
        Assert.Equal(1, unloaded.UnloadedCount);
        var uncertain = Tick(manager, HealthyCapacity);
        Assert.Equal(0, uncertain.RecalledCount);
        Assert.Equal(1, uncertain.RejectedCount);
        Assert.Equal(1U, uncertain.NewEffectAttemptCount);
        Assert.Equal(0U, uncertain.RecoveryAttemptCount);
        Assert.True(subscriber.TryGetResource(id, out var preparing));
        Assert.Equal(SharedResourceAvailability.Preparing, preparing!.Availability);
        Assert.True(preparing.DestructiveActionActive);

        var recovered = Tick(manager, HealthyCapacity);
        Assert.Equal(1, recovered.RecalledCount);
        Assert.Equal(0U, recovered.NewEffectAttemptCount);
        Assert.Equal(1U, recovered.RecoveryAttemptCount);
        Assert.Equal(4096UL, recovered.RestoredBytes);
        Assert.True(subscriber.TryGetResource(id, out var restored));
        Assert.Equal(id, restored!.Id);
        Assert.Equal(SharedResourceAvailability.Available, restored.Availability);
        Assert.Equal(1, lifecycle.RecallAttempts);
        Assert.Equal(1, lifecycle.RecoveryAttempts);
        Assert.True(subscriber.TryReleaseSubscription(subscription));

        await broker.StopAsync(CancellationToken.None);

    }

    [Fact]
    public async Task HostPublicSelfManager_BoundsNewPonrIncludingKnownNoEffect()
    {
        using var broker = CreateBroker();
        await broker.StartAsync(CancellationToken.None);
        var lifecycle = new RejectingHostLifecycle();
        for (var index = 0; index < 3; index++)
        {
            _ = broker.PublishHostResource(
                CreateDefinition() with
                {
                    ResourceKey = checked((ulong)(600 + index)),
                    ResourceId = checked((uint)(60 + index)),
                    ContentIdentityHash = checked((ulong)(6000 + index))
                },
                lifecycle);
        }

        var manager = (IHostPublicResourceSelfManager)broker;
        var first = Tick(
            manager,
            HealthyCapacity,
            maximumNewEffectAttempts: 1,
            maximumRecoveryAttempts: 0);

        Assert.Equal(1, first.PlannedCount);
        Assert.Equal(0, first.UnloadedCount);
        Assert.Equal(1, first.RejectedCount);
        Assert.Equal(1U, first.NewEffectAttemptCount);
        Assert.Equal(0U, first.RecoveryAttemptCount);
        Assert.Equal(1, lifecycle.UnloadAttempts);

        var second = Tick(
            manager,
            HealthyCapacity,
            maximumNewEffectAttempts: 1,
            maximumRecoveryAttempts: 0);
        Assert.Equal(1U, second.NewEffectAttemptCount);
        Assert.Equal(2, lifecycle.UnloadAttempts);

        await broker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task HostPublicSelfManager_BoundsPendingRecoveryIndependently()
    {
        using var broker = CreateBroker();
        await broker.StartAsync(CancellationToken.None);
        var lifecycles = new[]
        {
            new RecoveringUnloadLifecycle(),
            new RecoveringUnloadLifecycle()
        };
        for (var index = 0; index < lifecycles.Length; index++)
        {
            _ = broker.PublishHostResource(
                CreateDefinition() with
                {
                    ResourceKey = checked((ulong)(700 + index)),
                    ResourceId = checked((uint)(70 + index)),
                    ContentIdentityHash = checked((ulong)(7000 + index))
                },
                lifecycles[index]);
        }

        var manager = (IHostPublicResourceSelfManager)broker;
        var uncertain = Tick(
            manager,
            HealthyCapacity,
            maximumNewEffectAttempts: 2,
            maximumRecoveryAttempts: 0);
        Assert.Equal(2U, uncertain.NewEffectAttemptCount);
        Assert.Equal(0U, uncertain.RecoveryAttemptCount);
        Assert.Equal(2, uncertain.RejectedCount);

        var firstRecovery = Tick(
            manager,
            HealthyCapacity,
            maximumNewEffectAttempts: 0,
            maximumRecoveryAttempts: 1);
        Assert.Equal(0U, firstRecovery.NewEffectAttemptCount);
        Assert.Equal(1U, firstRecovery.RecoveryAttemptCount);
        Assert.Equal(1, firstRecovery.UnloadedCount);
        Assert.Equal(1, lifecycles.Sum(static item => item.RecoveryAttempts));

        var secondRecovery = Tick(
            manager,
            HealthyCapacity,
            maximumNewEffectAttempts: 0,
            maximumRecoveryAttempts: 1);
        Assert.Equal(1U, secondRecovery.RecoveryAttemptCount);
        Assert.Equal(1, secondRecovery.UnloadedCount);
        Assert.Equal(2, lifecycles.Sum(static item => item.RecoveryAttempts));

        await broker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task HostPublicSelfManager_RecallsAtMostOneResourcePerCapacityDomain()
    {
        using var broker = CreateBroker();
        await broker.StartAsync(CancellationToken.None);
        var lifecycle = new RecordingHostLifecycle();
        var definitions = new[]
        {
            CreateDefinition(),
            CreateDefinition() with
            {
                ResourceKey = 502,
                ResourceId = 6,
                ContentIdentityHash = 12
            },
            CreateDefinition() with
            {
                ResourceKey = 503,
                ResourceId = 7,
                ContentIdentityHash = 13,
                AdapterKey = 101,
                Tier = AdapterResourceTier.Vram,
                Flags = SharedResourceFlags.ReadOnly
                    | SharedResourceFlags.GpuBacked
            }
        };
        var ids = definitions
            .Select(definition =>
            {
                var id = broker.PublishHostResource(definition, lifecycle);
                lifecycle.Track(id, definition);
                return id;
            })
            .ToArray();
        var subscriber = GetSession(broker);
        var subscriptions = ids.Select((id, index) => subscriber.Subscribe(
            id,
            subscriberApplicationKey: 81,
            subscriberInstanceId: checked((ulong)(9001 + index)),
            subscriberSessionId: checked((ulong)(7001 + index)),
            subscriberProcessId: Environment.ProcessId,
            leaseDuration: TimeSpan.FromSeconds(10),
            SharedResourceSubscriptionIntent.ReadySoon)).ToArray();

        var manager = (IHostPublicResourceSelfManager)broker;
        var unloaded = Tick(manager, AllShortageAfterNormalReleaseRounds);
        Assert.Equal(3, unloaded.UnloadedCount);
        var memoryRecallOrder = ids.Take(2)
            .OrderBy(static id => id.PublicResourceId)
            .ToArray();
        var firstRecall = Tick(manager, HealthyCapacity);
        Assert.Equal(2, firstRecall.RecalledCount);
        Assert.Equal(
            [memoryRecallOrder[0], ids[2]],
            lifecycle.RecallContexts
                .Select(static context => context.ResourceId)
                .ToArray());
        Assert.Equal(101UL, lifecycle.RecallContexts[1].AdapterKey);
        var secondRecall = Tick(manager, HealthyCapacity);
        Assert.Equal(1, secondRecall.RecalledCount);
        Assert.Equal(memoryRecallOrder[1], lifecycle.RecallContexts[2].ResourceId);

        foreach (var subscription in subscriptions)
        {
            Assert.True(subscriber.TryReleaseSubscription(subscription));
        }
        await broker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task HostPublicSelfManager_RecoversUncertainUnloadBeforePublishingTombstone()
    {
        using var broker = CreateBroker();
        await broker.StartAsync(CancellationToken.None);
        var lifecycle = new RecoveringUnloadLifecycle();
        var id = broker.PublishHostResource(CreateDefinition(), lifecycle);
        var manager = (IHostPublicResourceSelfManager)broker;

        var uncertain = Tick(manager, HealthyCapacity);
        Assert.Equal(0, uncertain.UnloadedCount);
        Assert.Equal(1, uncertain.RejectedCount);
        var reader = GetSession(broker);
        Assert.True(reader.TryGetResource(id, out var gated));
        Assert.Equal(SharedResourceAvailability.Available, gated!.Availability);
        Assert.True(gated.DestructiveActionActive);

        var recovered = Tick(manager, HealthyCapacity);
        Assert.Equal(1, recovered.UnloadedCount);
        Assert.Equal(4096UL, recovered.ReleasedBytes);
        Assert.True(reader.TryGetResource(id, out var unloaded));
        Assert.Equal(id, unloaded!.Id);
        Assert.Equal(SharedResourceAvailability.Unavailable, unloaded.Availability);
        Assert.False(unloaded.DestructiveActionActive);
        Assert.Equal(1, lifecycle.UnloadAttempts);
        Assert.Equal(1, lifecycle.RecoveryAttempts);

        await broker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task SubscriptionMaintenance_ExpiresInterestWithoutInventingUse()
    {
        using var broker = CreateBroker();
        await broker.StartAsync(CancellationToken.None);
        var id = broker.PublishHostResource(
            CreateDefinition(),
            new TestHostLifecycle());
        var subscriber = GetSession(broker);
        var receipt = subscriber.Subscribe(
            id,
            subscriberApplicationKey: 81,
            subscriberInstanceId: 9001,
            subscriberSessionId: 7001,
            subscriberProcessId: Environment.ProcessId,
            leaseDuration: TimeSpan.FromMilliseconds(1),
            SharedResourceSubscriptionIntent.ReadySoon);

        var subscribed = ((IPublicResourceDirectoryQueries)broker).Find(id.PublicResourceId)!;
        Assert.Equal(1, subscribed.ActiveSubscriberCount);
        Assert.Equal(0, subscribed.QueuedRequestCount);
        Assert.Equal(0, subscribed.ReservedGrantCount);
        Assert.Equal(0, subscribed.ActiveUseCount);
        Assert.True(subscribed.SubscriptionMultiplier > 1);

        Assert.True(SpinWait.SpinUntil(
            () => NativeSharedResourceSession.MonotonicNow()
                > receipt.DeadlineTimestamp,
            TimeSpan.FromSeconds(1)));
        var maintenance = ((ISharedResourceSubscriptionMaintenance)broker).Refresh(
            NativeSharedResourceSession.MonotonicNow());
        Assert.Equal(1, maintenance.ExpiredSubscriptions);
        Assert.Equal(0, maintenance.ExpiredUseTasks);
        var expired = ((IPublicResourceDirectoryQueries)broker).Find(id.PublicResourceId)!;
        Assert.Equal(0, expired.ActiveSubscriberCount);
        Assert.Equal(1, expired.SubscriptionMultiplier);

        await broker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task HostPublicSelfManager_DoesNotUnloadAQueuedUse()
    {
        using var broker = CreateBroker();
        await broker.StartAsync(CancellationToken.None);
        var id = broker.PublishHostResource(
            CreateDefinition(),
            new TestHostLifecycle());
        var user = GetSession(broker);
        var leaseDuration = TimeSpan.FromSeconds(10);
        var subscription = user.Subscribe(
            id,
            subscriberApplicationKey: 81,
            subscriberInstanceId: 9001,
            subscriberSessionId: 7001,
            subscriberProcessId: Environment.ProcessId,
            leaseDuration,
            SharedResourceSubscriptionIntent.ReadySoon);
        var queued = user.EnqueueUse(new SharedResourceUseRequest(
            id,
            RequestKey: 1,
            RequesterApplicationKey: 81,
            RequesterInstanceId: 9001,
            RequesterSessionId: 7001,
            RequesterProcessId: Environment.ProcessId,
            ScorePlanGeneration: 1,
            BaseScore: 10,
            leaseDuration));

        var manager = (IHostPublicResourceSelfManager)broker;
        var protectedTick = Tick(manager, MemoryShortageAfterNormalReleaseRounds);
        Assert.Equal(0, protectedTick.PlannedCount);
        Assert.Equal(0, protectedTick.UnloadedCount);
        Assert.Equal(
            SharedResourceAvailability.Available,
            ((IPublicResourceDirectoryQueries)broker)
                .Find(id.PublicResourceId)!.Availability);

        Assert.True(user.TryCancelUse(queued));
        Assert.True(user.TryReleaseSubscription(subscription));
        Assert.Equal(1, Tick(manager, HealthyCapacity).UnloadedCount);
        await broker.StopAsync(CancellationToken.None);
    }

    private static SharedMemoryPublicResourceBroker CreateBroker()
    {
        var hostIdentity = new HostManagerRuntimeIdentity();
        var deploymentState = new HostManagerDeploymentState();
        var runtimePlanProvider = CreateRuntimePlanProvider(deploymentState);
        return new SharedMemoryPublicResourceBroker(
            runtimePlanProvider,
            deploymentState,
            hostIdentity);
    }

    private static NativeSharedResourceSession GetSession(
        SharedMemoryPublicResourceBroker broker)
    {
        var state = typeof(SharedMemoryPublicResourceBroker)
            .GetField("state", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(broker)
            ?? throw new InvalidOperationException("The test broker is not started.");
        return (NativeSharedResourceSession)(state.GetType()
            .GetProperty("Session", BindingFlags.Instance | BindingFlags.Public)!
            .GetValue(state)
            ?? throw new InvalidOperationException("The test broker session is unavailable."));
    }

    private static RuntimePlanProvider CreateRuntimePlanProvider(
        HostManagerDeploymentState deploymentState)
    {
        var loaded = StrictHostManagerProfileLoader.LoadBytes(
            ReadDefaultProfile(),
            "test/default.json");
        var hostManager = new HostManagerPlanCompiler().Compile(
            loaded,
            HostManagerTestPlanFactory.CreateSettingsInput(),
            1,
            HostManagerTestPlanFactory.CreateCpuTopology(1),
            HostManagerTestPlanFactory.CreateCpuScoring(HostManagerTestPlanFactory.CreateCpuTopology(1)));
        var runtimePlanProvider = new RuntimePlanProvider(deploymentState);
        runtimePlanProvider.Publish(CompiledRuntimePlan.Default with
        {
            Version = 1,
            CompiledAt = DateTimeOffset.UtcNow,
            Reason = "shared-resource-broker-test",
            HostManager = hostManager
        });
        return runtimePlanProvider;
    }

    private static HostPublicResourceSelfManagerTickResult Tick(
        IHostPublicResourceSelfManager manager,
        HostPublicResourceCapacityShortage shortage,
        uint maximumNewEffectAttempts = uint.MaxValue,
        uint maximumRecoveryAttempts = uint.MaxValue)
        => manager.Tick(new HostPublicResourceSelfManagerTickRequest(
            shortage,
            maximumNewEffectAttempts,
            maximumRecoveryAttempts));

    private static HostPublicResourceCapacityShortage HealthyCapacity { get; } = new(
        HostPublicResourceCapacityObservation.HealthyFresh,
        HostPublicResourceCapacityObservation.HealthyFresh,
        [
            new(
                101,
                HostPublicResourceCapacityObservation.HealthyFresh)
        ]);

    private static HostPublicResourceCapacityShortage MemoryShortageAfterNormalReleaseRounds { get; } = new(
        HostPublicResourceCapacityObservation.ShortageFresh(
            afterNormalReleaseRounds: true),
        HostPublicResourceCapacityObservation.HealthyFresh);

    private static HostPublicResourceCapacityShortage AllShortageAfterNormalReleaseRounds { get; } = new(
        HostPublicResourceCapacityObservation.ShortageFresh(
            afterNormalReleaseRounds: true),
        HostPublicResourceCapacityObservation.ShortageFresh(
            afterNormalReleaseRounds: true),
        [
            new(
                101,
                HostPublicResourceCapacityObservation.ShortageFresh(
                    afterNormalReleaseRounds: true))
        ]);

    private static HostPublicResourceDefinition CreateDefinition()
        => new(
            PublicResourceId: 0,
            ResourceKey: 501,
            ResourceId: 5,
            SizeBytes: 4096,
            ContentIdentityHash: 11,
            PayloadMappingId: 0,
            PayloadGeneration: 0,
            AdapterResourceTier.PhysicalMemory,
            AdapterResourceKind.Cache,
            AdapterResourceRecoveryKind.BuiltData,
            AdapterResourceGranularity.PartialUsable,
            AdapterResourceActionMask.None,
            AdapterResourceActionRoute.ManagerDirect,
            AdapterResourceDemandMask.None,
            AdapterSoftwareSurfaceState.PureBackground,
            SharedResourceAvailability.Available,
            SharedResourceFlags.ReadOnly);

    private sealed class TestHostLifecycle : IHostPublicResourceLifecycle
    {
        public bool TryUnload(HostPublicResourceUnloadContext context)
            => true;
    }

    private sealed class RejectingHostLifecycle : IHostPublicResourceLifecycle
    {
        public int UnloadAttempts { get; private set; }

        public bool TryUnload(HostPublicResourceUnloadContext context)
        {
            UnloadAttempts++;
            return false;
        }
    }

    private sealed class RecordingHostLifecycle : IHostPublicResourceLifecycle
    {
        private readonly Dictionary<SharedResourceId, HostPublicResourceDefinition>
            definitions = [];

        public List<HostPublicResourceUnloadContext> Contexts { get; } = [];
        public List<HostPublicResourceRecallContext> RecallContexts { get; } = [];

        public bool SupportsRecall => true;

        public void Track(
            SharedResourceId id,
            HostPublicResourceDefinition definition)
            => definitions.Add(
                id,
                definition with { PublicResourceId = id.PublicResourceId });

        public bool TryUnload(HostPublicResourceUnloadContext context)
        {
            Contexts.Add(context);
            return true;
        }

        public bool TryRecall(
            HostPublicResourceRecallContext context,
            out HostPublicResourceDefinition? definition)
        {
            RecallContexts.Add(context);
            if (!definitions.TryGetValue(context.ResourceId, out var expected))
            {
                definition = null;
                return false;
            }
            definition = expected with
            {
                PayloadMappingId = checked(expected.PayloadMappingId + 1),
                PayloadGeneration = checked(expected.PayloadGeneration + 2),
                Availability = SharedResourceAvailability.Available
            };
            definitions[context.ResourceId] = definition;
            return true;
        }
    }

    private sealed class RecoveringRecallLifecycle(
        HostPublicResourceDefinition definition)
        : IHostPublicResourceLifecycle
    {
        private SharedResourceId resourceId;

        public bool SupportsRecall => true;
        public int RecallAttempts { get; private set; }
        public int RecoveryAttempts { get; private set; }

        public void Track(SharedResourceId id) => resourceId = id;

        public bool TryUnload(HostPublicResourceUnloadContext context) => true;

        public bool TryRecall(
            HostPublicResourceRecallContext context,
            out HostPublicResourceDefinition? restored)
        {
            RecallAttempts++;
            restored = null;
            throw new InvalidOperationException("effect outcome is intentionally uncertain");
        }

        public HostPublicResourceLifecycleResult Recover(
            HostPublicResourceLifecycleOperation operation)
        {
            RecoveryAttempts++;
            Assert.Equal(resourceId, operation.ResourceId);
            return new HostPublicResourceLifecycleResult(
                HostPublicResourceEffectKnowledge.KnownEffect,
                definition with
                {
                    PublicResourceId = resourceId.PublicResourceId,
                    PayloadMappingId = 1,
                    PayloadGeneration = 2,
                    Availability = SharedResourceAvailability.Available
                });
        }
    }

    private sealed class RecoveringUnloadLifecycle : IHostPublicResourceLifecycle
    {
        public int UnloadAttempts { get; private set; }
        public int RecoveryAttempts { get; private set; }

        public bool TryUnload(HostPublicResourceUnloadContext context)
        {
            UnloadAttempts++;
            throw new InvalidOperationException("effect outcome is intentionally uncertain");
        }

        public HostPublicResourceLifecycleResult Recover(
            HostPublicResourceLifecycleOperation operation)
        {
            RecoveryAttempts++;
            Assert.Equal(HostPublicResourceLifecycleOperationKind.Unload, operation.Kind);
            return new HostPublicResourceLifecycleResult(
                HostPublicResourceEffectKnowledge.KnownEffect,
                null);
        }
    }

    private static byte[] ReadDefaultProfile()
    {
        using var stream = typeof(StrictHostManagerProfileLoader).Assembly
            .GetManifestResourceStream("ResourceManager.Configuration.HostManager.default.json")
            ?? throw new InvalidOperationException("Embedded Host Manager profile was not found.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

}
