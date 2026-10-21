using System.Buffers;
using System.Runtime.CompilerServices;
using ResourceManager.App.Infrastructure.Adaptation;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.Ledger;
using ResourceManager.Subscription;
using ResourceManager.Subscription.Native;

namespace Resource_Manager_APP.Tests;

public sealed class SamplingOutputScratchTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-16T00:00:00Z");

    [Fact]
    public void LayoutChecksEachSpanElementCountWithoutTruncatingTheCombinedUlongExtent()
    {
        var capacity = new NativeSamplingSubscriptionCapacity
        {
            DueItemCapacity = int.MaxValue, SourceViewCapacity = int.MaxValue,
            ExpiredSourceCapacity = 1, TriggerCapacity = 1
        };
        var layout = new NativeSamplingOutputLayout(in capacity);
        Assert.Equal(128UL * int.MaxValue + 96, layout.Length);
        Assert.True(layout.Length > int.MaxValue);
        Assert.Equal(layout.Length + 7, layout.StorageLength);
        Assert.Equal(0UL, layout.SourceOffset % 8);
        Assert.Equal(0UL, layout.ExpiredOffset % 8);
        Assert.Equal(0UL, layout.TriggerOffset % 8);
        capacity.DueItemCapacity = uint.MaxValue;
        Assert.Throws<OverflowException>(() => new NativeSamplingOutputLayout(in capacity));
    }

    [Fact]
    public async Task RealPlanWritesTheSdkRegionAndPendingSettlesAfterItsIdleReleaseAcrossAwait()
    {
        using var host = new HostManagerSamplingSubscriptionTestFixture();
        using var owner = new ResourceManagerLedgerOwner();
        var stores = new List<RecordedScratch>();
        var layout = Layout(host);
        var tracker = Tracker(host, Factory(owner, stores, store => store.Layout = layout));
        try
        {
            // A caller UTC can lag the existing monotonic command clock.
            Assert.False(tracker.Remove("not-tracked", Now.AddMinutes(1)));
            tracker.TrackPersistent("source", ["item"], Now, TimeSpan.FromSeconds(1),
                NativeSamplingSubscriptionSourceFlags.PeriodicTriggers | NativeSamplingSubscriptionSourceFlags.FreshOnTrigger);
            var tracked = Snapshot(host);
            Assert.True(tracked.LastCommandAtMilliseconds >= (ulong)Now.AddMinutes(1).ToUnixTimeMilliseconds());
            Assert.True(tracked.NextWakeMilliseconds > (ulong)Now.AddSeconds(1).ToUnixTimeMilliseconds());
            var firstAt = DateTimeOffset.FromUnixTimeMilliseconds(checked((long)tracked.NextWakeMilliseconds));
            var first = tracker.CreatePlan(firstAt);
            var store = Assert.Single(stores);
            Assert.Equal(layout.StorageLength, owner.ReadTable().RegisteredBytes);
            Assert.Equal(0, store.LiveLeases);
            Assert.Equal(1, store.CompleteCalls);
            Assert.True(store.WrittenDueHandle > 0);
            Assert.True(store.WrittenSourceHandle > 0);
            Assert.Equal(64U, store.WrittenDueStructSize);
            Assert.Equal(48U, store.WrittenTriggerStructSize);
            Assert.Equal("item", Assert.Single(first.DueItemIds));
            Assert.Single(first.Triggers);
            var uid = store.Resource.Uid;
            var firstAllocation = Assert.Single(store.Allocations);

            owner.SetMode(LedgerTableMode.Optimize);
            Assert.Equal(1UL, owner.Tick().Released); // The Native pending plan still has its session lease.
            Assert.Equal(0UL, owner.ReadTable().RegisteredBytes);
            await Task.Yield(); // Represents the collection await; no output region remains borrowed.
            tracker.MarkSampled(first.Request!, firstAt);
            var nextAt = DateTimeOffset.FromUnixTimeMilliseconds(checked((long)Snapshot(host).NextWakeMilliseconds));
            var next = tracker.CreatePlan(nextAt);
            Assert.Same(store, Assert.Single(stores));
            Assert.Equal(uid, store.Resource.Uid);
            Assert.Equal(2, store.Allocations.Count);
            Assert.NotEqual(firstAllocation, store.Allocations[1]);
            Assert.Equal(first.DueItemIds, next.DueItemIds);
            tracker.MarkSampled(next.Request!, nextAt);
        }
        finally { tracker.CloseOutputScratch(); }
    }

    [Fact]
    public void AcquireFailureDoesNotIssueANativePlanOrConsumeTheNextPlanEpoch()
    {
        using var host = new HostManagerSamplingSubscriptionTestFixture();
        using var owner = new ResourceManagerLedgerOwner();
        var stores = new List<RecordedScratch>();
        var tracker = Tracker(host, Factory(owner, stores, store => store.FailAcquireOnce = stores.Count == 1));
        try
        {
            tracker.Track(["item"], Now);
            var before = Snapshot(host);
            Assert.Throws<IOException>(() => tracker.CreatePlan(Now));
            Assert.Equal(before.LastPlanEpoch, Snapshot(host).LastPlanEpoch);
            Assert.Equal(0UL, owner.ReadTable().RegisteredBytes);
            var plan = tracker.CreatePlan(Now);
            Assert.Equal(1UL, plan.Schedule.PlanEpoch);
            tracker.MarkSampled(plan.Request!, Now);
        }
        finally { tracker.CloseOutputScratch(); }
    }

    [Theory]
    [InlineData("complete")]
    [InlineData("endUse")]
    public void SuccessfulNativePlanWithFailedOutputHandoffIsSettledBeforeItCanEscape(string failure)
    {
        using var host = new HostManagerSamplingSubscriptionTestFixture();
        using var owner = new ResourceManagerLedgerOwner();
        var stores = new List<RecordedScratch>();
        var tracker = Tracker(host, Factory(owner, stores, store =>
        {
            store.FailCompleteOnce = failure == "complete";
            store.FailEndOnce = failure == "endUse";
        }));
        try
        {
            tracker.Track(["item"], Now);
            Assert.Throws<IOException>(() => tracker.CreatePlan(Now));
            var store = Assert.Single(stores);
            Assert.True(store.WrittenDueHandle > 0); // The failure follows a real successful Plan/projection.
            var state = Snapshot(host);
            Assert.Equal(1UL, state.LastPlanEpoch);
            Assert.Equal(0UL, ReadOnlyItem(host).LastPlannedEpoch); // Original failed settlement ran.
            Assert.Throws<InvalidOperationException>(() => tracker.MarkSampled(["item"], Now));
            if (failure == "endUse")
            {
                Assert.Equal(1, store.LiveLeases);
                Assert.Throws<InvalidOperationException>(() => tracker.CreatePlan(Now.AddSeconds(1)));
                Assert.Equal(1UL, Snapshot(host).LastPlanEpoch);
            }
            else
            {
                Assert.Equal(0, store.LiveLeases);
                Assert.Equal(0UL, owner.ReadTable().RegisteredBytes); // No Complete: new allocation was discarded.
                var next = tracker.CreatePlan(Now.AddSeconds(2));
                Assert.Equal(2UL, next.Schedule.PlanEpoch);
                tracker.MarkSampled(next.Request!, Now.AddSeconds(2));
            }
        }
        finally { tracker.CloseOutputScratch(); }
        Assert.All(stores, store => Assert.Equal(0, store.LiveLeases));
        Assert.Equal(LedgerStatus.Ok, owner.TryClose());
    }

    [Fact]
    public void ProjectionFailureLeavesNewScratchUnpublishedAndKeepsTheSameIdentityForRetry()
    {
        using var host = new HostManagerSamplingSubscriptionTestFixture();
        using var owner = new ResourceManagerLedgerOwner();
        var stores = new List<RecordedScratch>();
        var reject = true;
        var tracker = Tracker(host, Factory(owner, stores), items =>
            reject ? throw new IOException("projection") : items.ToArray());
        try
        {
            tracker.Track(["item"], Now);
            Assert.Throws<IOException>(() => tracker.CreatePlan(Now));
            var store = Assert.Single(stores);
            Assert.Equal(0, store.CompleteCalls);
            Assert.Equal(0, store.LiveLeases);
            Assert.Equal(0UL, owner.ReadTable().RegisteredBytes);
            Assert.Equal(0UL, ReadOnlyItem(host).LastPlannedEpoch);
            reject = false;
            var next = tracker.CreatePlan(Now.AddSeconds(2));
            Assert.Same(store, Assert.Single(stores));
            tracker.MarkSampled(next.Request!, Now.AddSeconds(2));
        }
        finally { tracker.CloseOutputScratch(); }
    }

    [Fact]
    public void CapacityCandidateFailureKeepsTheOldAllocationAndUsesNoPlanEpoch()
    {
        using var host = new HostManagerSamplingSubscriptionTestFixture();
        using var owner = new ResourceManagerLedgerOwner();
        var stores = new List<RecordedScratch>();
        var tracker = Tracker(host, Factory(owner, stores, store => store.FailAcquireOnce = stores.Count == 2));
        try
        {
            tracker.Track(["item"], Now);
            var first = tracker.CreatePlan(Now);
            tracker.MarkSampled(first.Request!, Now);
            var old = Assert.Single(stores);
            var oldBytes = owner.ReadTable().RegisteredBytes;
            var changed = HostManagerTestPlanFactory.CreatePlan(root =>
            {
                root["profile_revision"] = 89;
                root["host_recreate"]!["sampling_subscription"]!["roles"]![2]!["maximum_membership_count"] = 32768;
            });
            host.Provider.Publish(ResourceManager.App.Domain.RuntimeSpecialization.CompiledRuntimePlan.Default with
            { Version = 2, HostManager = changed });
            Assert.Throws<IOException>(() => tracker.CreatePlan(Now.AddSeconds(2)));
            Assert.False(old.Closed);
            Assert.Equal(oldBytes, owner.ReadTable().RegisteredBytes);
            var next = tracker.CreatePlan(Now.AddSeconds(2));
            Assert.Equal(1UL, next.Schedule.PlanEpoch); // New session replay reset its plan epoch; failed allocation did not consume it.
            tracker.MarkSampled(next.Request!, Now.AddSeconds(2));
            Assert.Equal(3, stores.Count);
            Assert.True(stores[1].Closed); // Failed candidate was explicitly closed before allocating another.
            Assert.False(old.Closed); // One pending old close; never a third live resource.
            Assert.Equal(oldBytes + stores[2].Length, owner.ReadTable().RegisteredBytes);
        }
        finally { tracker.CloseOutputScratch(); }
        Assert.All(stores, store => Assert.True(store.Closed));
    }

    [Fact]
    public void TinyCombinedOutputsStayOrdinaryAndOriginalPublicConsumerRequiresNoStorageInjection()
    {
        var plan = HostManagerTestPlanFactory.CreatePlan(root =>
        {
            var role = root["host_recreate"]!["sampling_subscription"]!["roles"]![2]!;
            role["maximum_source_count"] = 2;
            role["maximum_item_count"] = 4;
            role["maximum_membership_count"] = 8;
            role["maximum_due_item_count"] = 4;
            role["maximum_source_view_count"] = 2;
            role["maximum_expired_source_count"] = 2;
        });
        using var host = new HostManagerSamplingSubscriptionTestFixture(plan);
        using var owner = new ResourceManagerLedgerOwner();
        var stores = new List<RecordedScratch>();
        var tracker = Tracker(host, Factory(owner, stores));
        try
        {
            tracker.Track(["item"], Now);
            var first = tracker.CreatePlan(Now);
            Assert.Equal(0UL, owner.ReadTable().RegisteredBytes);
            Assert.Equal(0UL, Assert.Single(Assert.Single(stores).Allocations));
            tracker.MarkSampled(first.Request!, Now);
            tracker.Clear(Now);
        }
        finally { tracker.CloseOutputScratch(); }
        using var ordinaryHost = new HostManagerSamplingSubscriptionTestFixture(plan);
        var ordinary = new ResourceManager.Subscription.NativeItemSamplingSubscriptionTracker<string[], SamplingOwnerToken>(
            new HostSamplingSessionOwnerAdapter(ordinaryHost.Owner), 3, static _ => "ordinary", static items => items,
            static (items, _) => items.ToArray(), false); // Existing public constructor, as in ManagedTrackerConsumer.
        ordinary.Track(["ordinary-item"], Now.AddSeconds(1));
        var ordinaryPlan = ordinary.CreatePlan(Now.AddSeconds(1));
        Assert.Equal("ordinary-item", Assert.Single(ordinaryPlan.DueItemIds));
        ordinary.MarkSampled(ordinaryPlan.Request!, Now.AddSeconds(1));
        Assert.Equal(0UL, owner.ReadTable().RegisteredBytes);
    }

    [Fact]
    public void OwnerCloseIsRefusedDuringTheRealOutputBorrowAndPendingCanStillSettleAfterward()
    {
        using var host = new HostManagerSamplingSubscriptionTestFixture();
        using var owner = new ResourceManagerLedgerOwner();
        var stores = new List<RecordedScratch>();
        var tracker = Tracker(host, Factory(owner, stores), items =>
        {
            Assert.Equal(LedgerStatus.InUse, owner.TryClose());
            Assert.True(owner.ReadTable().RegisteredBytes > 0);
            return items.ToArray();
        });
        tracker.Track(["item"], Now);
        var plan = tracker.CreatePlan(Now);
        tracker.MarkSampled(plan.Request!, Now);
        tracker.CloseOutputScratch();
        Assert.Equal(LedgerStatus.Ok, owner.TryClose());
    }

    private static NativeItemSamplingSubscriptionTracker<string[]> Tracker(HostManagerSamplingSubscriptionTestFixture host,
        Func<ulong, uint, INativeSamplingOutputScratch> factory, Func<IReadOnlyList<string>, string[]>? project = null)
        => new(host.Owner, 3, static _ => "source", static items => items,
            (items, _) => project is null ? items.ToArray() : project(items), false, factory);

    internal static Func<ulong, uint, INativeSamplingOutputScratch> Factory(ResourceManagerLedgerOwner owner,
        List<RecordedScratch> stores, Action<RecordedScratch>? configure = null)
        => (length, alignment) =>
        {
            var resource = owner.CreateScratch(checked(length + alignment - 1));
            var store = new RecordedScratch(resource, length);
            stores.Add(store);
            configure?.Invoke(store);
            return store;
        };

    private static NativeSamplingOutputLayout Layout(HostManagerSamplingSubscriptionTestFixture host)
    {
        using var lease = host.Owner.Acquire(3);
        return new NativeSamplingOutputLayout(lease.Session.Capacity);
    }

    private static NativeSamplingSubscriptionSnapshotHeader Snapshot(HostManagerSamplingSubscriptionTestFixture host)
        => ReadSnapshot(host).Header;
    private static NativeSamplingSubscriptionItemState ReadOnlyItem(HostManagerSamplingSubscriptionTestFixture host)
        => Assert.Single(ReadSnapshot(host).Items);
    private static (NativeSamplingSubscriptionSnapshotHeader Header, NativeSamplingSubscriptionItemState[] Items)
        ReadSnapshot(HostManagerSamplingSubscriptionTestFixture host)
    {
        using var lease = host.Owner.Acquire(3);
        var cap = lease.Session.Capacity;
        var header = new NativeSamplingSubscriptionSnapshotHeader
        { AbiVersion = NativeSamplingSubscriptionAbi.Version, StructSize = (uint)Unsafe.SizeOf<NativeSamplingSubscriptionSnapshotHeader>() };
        var sources = new NativeSamplingSubscriptionSourceView[checked((int)cap.SourceCapacity)];
        var items = new NativeSamplingSubscriptionItemState[checked((int)cap.ItemCapacity)];
        Assert.Equal(NativeSamplingSubscriptionStatus.Ok, lease.Session.Snapshot(ref header, sources, items));
        return (header, items.AsSpan(0, checked((int)header.ItemOutputCount)).ToArray());
    }

    // A fault wrapper around actual SDK-owned storage, never a fake Native session.
    internal sealed class RecordedScratch(ResourceManagerLedgerOwner.ComputeScratch resource, ulong payloadLength) : INativeSamplingOutputScratch
    {
        private readonly ulong payloadBytes = payloadLength;
        internal ResourceManagerLedgerOwner.ComputeScratch Resource => resource;
        public ulong Length => resource.Length;
        internal bool FailAcquireOnce, FailCompleteOnce, FailEndOnce, FailCloseOnce;
        internal bool Closed;
        internal int LiveLeases, CompleteCalls, CloseCalls;
        internal ulong WrittenDueHandle, WrittenSourceHandle;
        internal uint WrittenDueStructSize, WrittenTriggerStructSize;
        internal Action? Completing, Closing;
        internal NativeSamplingOutputLayout? Layout;
        internal List<ulong> Allocations { get; } = [];

        public NativeSamplingOutputScratchLease Acquire()
        {
            if (FailAcquireOnce) { FailAcquireOnce = false; throw new IOException("acquire"); }
            var lease = resource.Acquire();
            Allocations.Add(lease.AllocationNumber);
            LiveLeases++;
            return new Lease(this, lease);
        }
        public void Dispose()
        {
            CloseCalls++;
            Closing?.Invoke();
            if (FailCloseOnce) { FailCloseOnce = false; throw new IOException("close"); }
            resource.Dispose();
            Closed = true;
        }
        private sealed class Lease(RecordedScratch owner, ComputeScratchLease lease) : NativeSamplingOutputScratchLease
        {
            private bool ended;
            internal override ulong Length => lease.Length;
            internal override MemoryHandle Pin() => lease.GetMemoryChunk(0, 1).Pin();
            internal override unsafe void Complete()
            {
                // Observe bytes written by real Plan through the SAME SDK allocation.
                using var pin = Pin();
                var address = (byte*)(((nuint)pin.Pointer + 7) & ~(nuint)7);
                if (owner.payloadBytes >= 64)
                {
                    var due = (NativeSamplingSubscriptionDueItem*)address;
                    owner.WrittenDueHandle = due->ItemHandle;
                    owner.WrittenDueStructSize = due->StructSize;
                }
                if (owner.Layout is { } layout)
                {
                    owner.WrittenSourceHandle = ((NativeSamplingSubscriptionSourceView*)(address + checked((nint)layout.SourceOffset)))->SourceHandle;
                    owner.WrittenTriggerStructSize = ((NativeSamplingSubscriptionTrigger*)(address + checked((nint)layout.TriggerOffset)))->StructSize;
                }
                owner.Completing?.Invoke();
                if (owner.FailCompleteOnce) { owner.FailCompleteOnce = false; throw new IOException("complete"); }
                owner.CompleteCalls++;
                lease.Complete();
            }
            public override void Dispose()
            {
                if (ended) return;
                if (owner.FailEndOnce) { owner.FailEndOnce = false; throw new IOException("endUse"); }
                lease.Dispose();
                ended = true;
                owner.LiveLeases--;
            }
        }
    }
}
