using System.Buffers;
using System.Runtime.CompilerServices;
using ResourceManager.App.Infrastructure.Adaptation;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.Ledger;
using ResourceManager.Subscription.Native;
using Tracker = ResourceManager.App.Infrastructure.Monitoring.NativeItemSamplingSubscriptionTracker<string[]>;

namespace Resource_Manager_APP.Tests;

public sealed class SamplingSessionScratchTests
{
    [Fact]
    public unsafe void ConcurrentResidentSlicesUseSeparateSdkLeasesAndReturnEachReferenceOnlyOnce()
    {
        using var ledger = new ResourceManagerLedgerOwner();
        var resource = ledger.CreateScratch(8199);
        using var group = new SamplingSessionScratch(resource);
        using (var initial = group.Acquire(0, 4096)) initial.Complete();
        using var first = group.Acquire(0, 4096);
        using var second = group.Acquire(4096, 4096);
        Assert.Equal(2, resource.Active); // Both operations issued a real SDK use.
        using (var firstPin = first.Pin())
        using (var secondPin = second.Pin())
        {
            Assert.Equal((nuint)firstPin.Pointer + 4096, (nuint)secondPin.Pointer);
            ((byte*)firstPin.Pointer)[0] = 19;
            ((byte*)secondPin.Pointer)[0] = 73;
            Assert.Equal(19, ((byte*)firstPin.Pointer)[0]);
            Assert.Equal(73, ((byte*)secondPin.Pointer)[0]);
            Assert.Equal(0UL, ledger.Tick().Released);
        }
        first.Complete(); second.Complete();
        first.Dispose(); first.Dispose();
        Assert.Equal(0UL, ledger.Tick().Released); // The second slice still owns one reference.
        Assert.Throws<InvalidOperationException>(() => group.Dispose());
        second.Dispose();
        group.Dispose();
        Assert.Equal(0UL, ledger.ReadTable().RegisteredBytes);
    }

    [Fact]
    public async Task ConcurrentColdBorrowWaitsForTheActualLoaderToPublishThenUsesItsOwnLease()
    {
        using var ledger = new ResourceManagerLedgerOwner();
        var resource = ledger.CreateScratch(8199);
        using var group = new SamplingSessionScratch(resource);
        using var first = group.Acquire(0, 4096);
        using var entered = new ManualResetEventSlim(false);
        var waiting = Task.Run(() => { entered.Set(); return group.Acquire(4096, 4096); });
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.False(waiting.IsCompleted);
            Assert.Equal(1, resource.Active);
        }
        finally { first.Complete(); first.Dispose(); }
        using var second = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, resource.Active);
        second.Complete(); second.Dispose();
        Assert.Equal(0, resource.Active);
    }

    [Fact]
    public async Task FailedLoaderReturnWakesOtherBorrowersAndTheSameLeaseCanBeExplicitlyRetried()
    {
        using var ledger = new ResourceManagerLedgerOwner();
        var recorded = new RecordedResource(ledger.CreateScratch(8199)) { FailEnd = true };
        using var group = new SamplingSessionScratch(recorded);
        using var first = group.Acquire(0, 4096);
        using var entered = new ManualResetEventSlim(false);
        var waiting = Task.Run(() => { entered.Set(); return group.Acquire(4096, 4096); });
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            first.Complete();
            Assert.Throws<IOException>(() => first.Dispose());
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await waiting.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(0, recorded.ActualEnds);
        }
        finally { first.Dispose(); }
        Assert.Equal(1, recorded.ActualEnds);
        using var next = group.Acquire(4096, 4096);
        next.Complete(); next.Dispose();
        Assert.Equal(2, recorded.ActualEnds);
    }

    [Fact]
    public async Task SevenRolesShareOneRealRegionAndSettleExactTriggersAfterIdleReclaim()
    {
        using var ledger = new ResourceManagerLedgerOwner();
        using var host = new HostManagerSamplingSubscriptionTestFixture(ledgerOwner: ledger);
        var now = DateTimeOffset.Parse("2026-10-19T00:00:00Z");
        var trackers = new List<Tracker>();
        var requests = new List<string[]>();
        var dueTimes = new List<DateTimeOffset>();
        ulong expected = 7;
        for (var role = 1; role <= 7; role++)
        {
            using var session = host.Owner.Acquire(role);
            expected += 32UL * session.Session.Capacity.ItemCapacity;
            var tracker = new Tracker(host.Owner, role, static _ => "source", static items => items,
                static (items, _) => items.ToArray(), false);
            trackers.Add(tracker);
            // The owner's command clock may already be ahead of the caller UTC.
            Assert.False(tracker.Remove("not-tracked", now.AddMinutes(1)));
            tracker.TrackPersistent("source", [$"item-{role}"], now, TimeSpan.FromSeconds(1),
                NativeSamplingSubscriptionSourceFlags.PeriodicTriggers |
                NativeSamplingSubscriptionSourceFlags.FreshOnTrigger);
            dueTimes.Add(DateTimeOffset.FromUnixTimeMilliseconds(
                checked((long)Snapshot(session.Session).NextWakeMilliseconds)));
        }
        Assert.Equal(expected, ledger.ReadTable().RegisteredBytes);
        for (var index = 0; index < trackers.Count; index++)
        {
            var plan = trackers[index].CreatePlan(dueTimes[index]);
            Assert.Single(plan.DueItemIds);
            Assert.Single(plan.Triggers);
            requests.Add(plan.Request!);
        }
        ledger.SetMode(LedgerTableMode.Optimize);
        Assert.Equal(1UL, ledger.Tick().Released); // Seven roles, one region; no active Native pointers.
        Assert.Equal(0UL, ledger.ReadTable().RegisteredBytes);
        for (var i = 0; i < trackers.Count; i++)
            trackers[i].MarkSampled(requests[i], dueTimes[i]);
        Assert.Equal(expected, ledger.ReadTable().RegisteredBytes);
        for (var index = 0; index < trackers.Count; index++)
        {
            var tracker = trackers[index];
            using var session = host.Owner.Acquire(index + 1);
            var nextAt = DateTimeOffset.FromUnixTimeMilliseconds(
                checked((long)Snapshot(session.Session).NextWakeMilliseconds));
            var next = tracker.CreatePlan(nextAt);
            Assert.Single(next.Triggers);
            Assert.Equal(2UL, next.Triggers[0].Sequence);
            tracker.MarkSampled(next.Request!, nextAt);
            tracker.CloseOutputScratch();
        }
        await host.Owner.StopAsync(CancellationToken.None);
        Assert.Equal(0UL, ledger.ReadTable().RegisteredBytes);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void FailedEndOrCloseRetainsTheSameWorkspaceUntilExplicitRetry(bool failEnd)
    {
        using var ledger = new ResourceManagerLedgerOwner();
        var plan = HostManagerTestPlanFactory.CreatePlan().SamplingSubscription;
        var workspace = new NativeSamplingSubscriptionWorkspace();
        RecordedResource? resource = null;
        workspace.Initialize(plan, length => resource = new RecordedResource(ledger.CreateScratch(length)));
        var recorded = resource!;
        using var closing = workspace;
        recorded.FailEnd = failEnd;
        recorded.FailClose = !failEnd;
        if (failEnd)
            Assert.Throws<IOException>(() => Snapshot(workspace.GetSession(1)));
        else
            Snapshot(workspace.GetSession(1));
        Assert.True(ledger.ReadTable().RegisteredBytes > 0);
        if (!failEnd) Assert.Throws<IOException>(() => workspace.Dispose());
        Assert.False(recorded.Closed);
        workspace.Dispose(); // Same lease/region; no new UID or Native session.
        Assert.True(recorded.Closed);
        Assert.Equal(0UL, ledger.ReadTable().RegisteredBytes);
        Assert.Equal(1, recorded.ActualEnds);
        workspace.Dispose();
    }

    [Fact]
    public void FailedBorrowDoesNotMutateNativeStateAndTheSameWorkspaceCanRetry()
    {
        using var ledger = new ResourceManagerLedgerOwner();
        using var workspace = new NativeSamplingSubscriptionWorkspace();
        RecordedResource? resource = null;
        workspace.Initialize(HostManagerTestPlanFactory.CreatePlan().SamplingSubscription,
            length => resource = new RecordedResource(ledger.CreateScratch(length)));
        resource!.FailAcquire = true;
        Assert.Throws<IOException>(() => Snapshot(workspace.GetSession(7)));
        Assert.Equal(0UL, ledger.ReadTable().RegisteredBytes);
        Snapshot(workspace.GetSession(7));
        Assert.Equal(1, resource.ActualEnds);
    }

    internal static NativeSamplingSubscriptionSnapshotHeader Snapshot(NativeSamplingSubscriptionSession session)
    {
        var capacity = session.Capacity;
        var header = new NativeSamplingSubscriptionSnapshotHeader
        { AbiVersion = NativeSamplingSubscriptionAbi.Version,
          StructSize = (uint)Unsafe.SizeOf<NativeSamplingSubscriptionSnapshotHeader>() };
        Assert.Equal(NativeSamplingSubscriptionStatus.Ok, session.Snapshot(ref header,
            new NativeSamplingSubscriptionSourceView[checked((int)capacity.SourceCapacity)],
            new NativeSamplingSubscriptionItemState[checked((int)capacity.ItemCapacity)]));
        return header;
    }

    private sealed class RecordedResource(ResourceManagerLedgerOwner.ComputeScratch resource) : IComputeScratch
    {
        internal bool FailAcquire, FailEnd, FailClose, Closed;
        internal int ActualEnds;
        public ulong Length => resource.Length;
        public ComputeScratchLease Acquire()
        {
            if (FailAcquire) { FailAcquire = false; throw new IOException("recorded borrow failure"); }
            return new Lease(this, resource.Acquire());
        }
        public void Dispose()
        {
            if (FailClose) { FailClose = false; throw new IOException("recorded close failure"); }
            resource.Dispose(); Closed = true;
        }
        private sealed class Lease(RecordedResource owner, ComputeScratchLease use) : ComputeScratchLease
        {
            private bool ended;
            internal override ulong Length => use.Length;
            internal override ulong AllocationNumber => use.AllocationNumber;
            internal override bool IsLoading => use.IsLoading;
            internal override Memory<byte> GetMemoryChunk(ulong offset, int count) => use.GetMemoryChunk(offset, count);
            internal override void Complete() => use.Complete();
            public override void Dispose()
            {
                if (ended) return;
                if (owner.FailEnd) { owner.FailEnd = false; throw new IOException("recorded endUse failure"); }
                use.Dispose(); ended = true; owner.ActualEnds++;
            }
        }
    }
}
