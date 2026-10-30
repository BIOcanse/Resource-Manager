using System.Runtime.CompilerServices;
using ResourceManager.App.Infrastructure.Adaptation;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.Subscription.Native;
using Tracker = ResourceManager.App.Infrastructure.Monitoring.NativeItemSamplingSubscriptionTracker<string[]>;

namespace Resource_Manager_APP.Tests;

public sealed class SamplingSessionScratchTests
{
    private const ulong CommandAt = 1_000;

    [Fact]
    public void ARoleBuildsItsSessionOnTheFirstSubscriptionAndTheLastOneLeavingReleasesIt()
    {
        using var ledger = new ResourceManagerLedgerOwner();
        using var host = new HostManagerSamplingSubscriptionTestFixture(ledgerOwner: ledger);
        var now = DateTimeOffset.Parse("2026-10-19T00:00:00Z");
        Assert.Empty(SamplingRows(ledger));
        using (var tokenOnly = host.Owner.Acquire(1)) Assert.True(tokenOnly.OwnerToken.IsActive);
        Assert.Empty(SamplingRows(ledger)); // A lease that never reads its session builds none.
        var tracker = new Tracker(host.Owner, 1, static _ => "source", static items => items,
            static (items, _) => items.ToArray(), false);
        // The owner's command clock may already be ahead of the caller UTC.
        Assert.False(tracker.Remove("not-tracked", now.AddMinutes(1)));
        Assert.Empty(SamplingRows(ledger)); // Still no subscription, so nothing is held.
        tracker.TrackPersistent("source", ["item-a", "item-b"], now, TimeSpan.FromSeconds(1),
            NativeSamplingSubscriptionSourceFlags.PeriodicTriggers);
        var row = Assert.Single(SamplingRows(ledger));
        Assert.Equal("sampling-role1-native", row.WorkArea);
        Assert.StartsWith("sources=1/live=1, items=2/live=2, memberships=", row.Rows);
        Assert.False(row.InLedger);
        Assert.True(row.Bytes > 0);
        Assert.True(tracker.Remove("source", now.AddMinutes(1)));
        Assert.Empty(SamplingRows(ledger));
        Assert.Equal(0UL, ledger.ReadTable().RegisteredBytes);
    }

    [Fact]
    public void SevenRolesHoldOnlyTheirLiveRowsAndSettleExactTriggersWithPerOperationBorrows()
    {
        using var ledger = new ResourceManagerLedgerOwner();
        using var host = new HostManagerSamplingSubscriptionTestFixture(ledgerOwner: ledger);
        var now = DateTimeOffset.Parse("2026-10-19T00:00:00Z");
        var trackers = new List<Tracker>();
        var requests = new List<string[]>();
        var dueTimes = new List<DateTimeOffset>();
        for (var role = 1; role <= 7; role++)
        {
            var tracker = new Tracker(host.Owner, role, static _ => "source", static items => items,
                static (items, _) => items.ToArray(), false);
            trackers.Add(tracker);
            tracker.TrackPersistent("source", [$"item-{role}"], now, TimeSpan.FromSeconds(1),
                NativeSamplingSubscriptionSourceFlags.PeriodicTriggers |
                NativeSamplingSubscriptionSourceFlags.FreshOnTrigger);
            using var session = host.Owner.Acquire(role);
            dueTimes.Add(DateTimeOffset.FromUnixTimeMilliseconds(
                checked((long)Snapshot(session.Session).NextWakeMilliseconds)));
        }
        Assert.Equal(7, SamplingRows(ledger).Count);
        for (var index = 0; index < trackers.Count; index++)
        {
            var plan = trackers[index].CreatePlan(dueTimes[index]);
            Assert.Single(plan.DueItemIds);
            Assert.Single(plan.Triggers);
            requests.Add(plan.Request!);
        }
        for (var i = 0; i < trackers.Count; i++)
            trackers[i].MarkSampled(requests[i], dueTimes[i]);
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
        var rows = SamplingRows(ledger);
        Assert.Equal(Enumerable.Range(1, 7).Select(static role => $"sampling-role{role}-native"),
            rows.Select(static row => row.WorkArea).Order(StringComparer.Ordinal));
        Assert.All(rows, static row => Assert.StartsWith("sources=1/live=1, items=1/live=1, ", row.Rows));
        Assert.DoesNotContain(ledger.ReadWorkspaceCapacities(), static row => row.WorkArea == SamplingSessionScratch.WorkArea);
        foreach (var tracker in trackers) Assert.True(tracker.Remove("source", now.AddMinutes(1)));
        Assert.Empty(SamplingRows(ledger));
        Assert.Equal(0UL, ledger.ReadTable().RegisteredBytes);
    }

    [Fact]
    public void EachOperationBorrowsThirtyTwoBytesPerItemRowAndClosesItBeforeReturning()
    {
        using var ledger = new ResourceManagerLedgerOwner();
        var created = new List<ResourceManagerLedgerOwner.ComputeScratch>();
        using var workspace = new NativeSamplingSubscriptionWorkspace();
        workspace.Initialize(HostManagerTestPlanFactory.CreatePlan().SamplingSubscription,
            length => { var scratch = ledger.CreateScratch(length); created.Add(scratch); return scratch; });
        Assert.False(workspace.HasSession(1));
        using (workspace.AcquireRole(1)) { }
        Assert.False(workspace.HasSession(1));
        var role = workspace.AcquireRole(1);
        var session = role.Session;
        Assert.True(workspace.HasSession(1));
        Assert.Equal((0U, 0U), (session.WorkspaceCapacity.SourceRows, session.WorkspaceCapacity.ItemRows));
        ulong epoch = 0;
        Assert.Equal(NativeSamplingSubscriptionStatus.Ok, session.Reset(Control(session, ++epoch)));
        Snapshot(session);
        Assert.Empty(created); // No item row is read, so nothing is borrowed.

        var items = Items(3);
        Assert.Equal(NativeSamplingSubscriptionStatus.Ok, session.Track(TrackInput(session, ++epoch, 1, items.Length), items));
        var track = Assert.Single(created);
        Assert.Equal(NativeSamplingSubscriptionSession.ScratchRowBytes * 3 + 7, track.Length);
        Assert.Equal((SamplingSessionScratch.WorkArea, "items=3/live=0"), (track.WorkArea, track.Rows));
        Assert.True(track.Closed);
        var stored = session.WorkspaceCapacity;
        Assert.Equal((1U, 1U, 3U, 3U), (stored.SourceRows, stored.ActiveSourceCount, stored.ItemRows, stored.KnownItemCount));
        Assert.Equal(3U, NativeSamplingSubscriptionSession.ScratchRowsFor(stored.ItemRows, 0, false, session.Capacity.ItemCapacity));

        Snapshot(session);
        Assert.Equal(2, created.Count);
        Assert.Equal((NativeSamplingSubscriptionSession.ScratchRowBytes * 3 + 7, "items=3/live=3"),
            (created[1].Length, created[1].Rows));
        Assert.True(created[1].Closed);
        Assert.Equal(0UL, ledger.ReadTable().RegisteredBytes);

        Assert.Equal(NativeSamplingSubscriptionStatus.Ok, session.Remove(RemoveInput(session, ++epoch, 1)));
        var removed = session.WorkspaceCapacity;
        Assert.Equal((0U, 0U, 0U, 0U), (removed.SourceRows, removed.ActiveSourceCount, removed.ItemRows, removed.KnownItemCount));
        role.Dispose();
        Assert.False(workspace.HasSession(1)); // The last lease returned with no subscription left.
        Assert.Throws<ObjectDisposedException>(() => session.Capacity);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void AFailedReturnOrCloseKeepsThatBorrowUntilTheNextOperationRetriesIt(bool failEnd)
    {
        using var ledger = new ResourceManagerLedgerOwner();
        var recorded = new List<RecordedResource>();
        var arm = false;
        using var workspace = new NativeSamplingSubscriptionWorkspace();
        workspace.Initialize(HostManagerTestPlanFactory.CreatePlan().SamplingSubscription, length =>
        {
            var resource = new RecordedResource(ledger.CreateScratch(length)) { FailEnd = arm && failEnd, FailClose = arm && !failEnd };
            arm = false;
            recorded.Add(resource);
            return resource;
        });
        using var role = workspace.AcquireRole(1);
        var session = role.Session;
        ulong epoch = 0;
        Assert.Equal(NativeSamplingSubscriptionStatus.Ok, session.Reset(Control(session, ++epoch)));
        var items = Items(1);
        Assert.Equal(NativeSamplingSubscriptionStatus.Ok, session.Track(TrackInput(session, ++epoch, 1, items.Length), items));
        Assert.True(recorded[0].Closed);
        arm = true;
        Assert.Throws<IOException>(() => Snapshot(session));
        Assert.False(recorded[1].Closed);
        Snapshot(session); // Retries the same borrow's return and close first, then borrows anew.
        Assert.True(recorded[1].Closed);
        Assert.Equal(1, recorded[1].ActualEnds);
        Assert.Equal(3, recorded.Count);
        Assert.True(recorded[2].Closed);
        Assert.Equal(0UL, ledger.ReadTable().RegisteredBytes);
    }

    [Fact]
    public void AFailedBorrowClosesItsScratchAndLeavesTheNativeSessionUnchanged()
    {
        using var ledger = new ResourceManagerLedgerOwner();
        var recorded = new List<RecordedResource>();
        var failAcquire = false;
        using var workspace = new NativeSamplingSubscriptionWorkspace();
        workspace.Initialize(HostManagerTestPlanFactory.CreatePlan().SamplingSubscription, length =>
        {
            var resource = new RecordedResource(ledger.CreateScratch(length)) { FailAcquire = failAcquire };
            failAcquire = false;
            recorded.Add(resource);
            return resource;
        });
        using var role = workspace.AcquireRole(7);
        var session = role.Session;
        ulong epoch = 0;
        Assert.Equal(NativeSamplingSubscriptionStatus.Ok, session.Reset(Control(session, ++epoch)));
        failAcquire = true;
        var items = Items(2);
        Assert.Throws<IOException>(() => session.Track(TrackInput(session, ++epoch, 1, items.Length), items));
        Assert.True(recorded[0].Closed);
        Assert.Equal(0, recorded[0].ActualEnds);
        Assert.Equal(0U, session.WorkspaceCapacity.ActiveSourceCount);
        Assert.Equal(NativeSamplingSubscriptionStatus.Ok, session.Track(TrackInput(session, ++epoch, 1, items.Length), items));
        Assert.Equal(2, recorded.Count);
        Assert.Equal(1, recorded[1].ActualEnds);
        Assert.Equal(2U, session.WorkspaceCapacity.KnownItemCount);
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

    private static List<ResourceManagerWorkspaceCapacity> SamplingRows(ResourceManagerLedgerOwner ledger)
        => ledger.ReadWorkspaceCapacities()
            .Where(static row => row.WorkArea.StartsWith("sampling-role", StringComparison.Ordinal)
                && row.WorkArea.EndsWith("-native", StringComparison.Ordinal))
            .ToList();

    private static NativeSamplingSubscriptionItemReference[] Items(int count)
        => Enumerable.Range(1, count).Select(static index => new NativeSamplingSubscriptionItemReference
        {
            StructSize = (uint)Unsafe.SizeOf<NativeSamplingSubscriptionItemReference>(),
            ItemHandle = checked((ulong)index + 100)
        }).ToArray();

    private static NativeSamplingSubscriptionControlInput Control(NativeSamplingSubscriptionSession session, ulong epoch) => new()
    {
        AbiVersion = NativeSamplingSubscriptionAbi.Version,
        StructSize = (uint)Unsafe.SizeOf<NativeSamplingSubscriptionControlInput>(),
        ConfigurationGeneration = session.ConfigurationGeneration,
        OperationEpoch = epoch,
        CommandAtMilliseconds = CommandAt,
        ValidMask = (ulong)NativeSamplingSubscriptionControlValidity.Required
    };

    private static NativeSamplingSubscriptionTrackInput TrackInput(
        NativeSamplingSubscriptionSession session, ulong epoch, ulong sourceHandle, int itemCount) => new()
    {
        AbiVersion = NativeSamplingSubscriptionAbi.Version,
        StructSize = (uint)Unsafe.SizeOf<NativeSamplingSubscriptionTrackInput>(),
        ConfigurationGeneration = session.ConfigurationGeneration,
        OperationEpoch = epoch,
        CommandAtMilliseconds = CommandAt,
        ObservedAtMilliseconds = CommandAt,
        SourceHandle = sourceHandle,
        ValidMask = (ulong)NativeSamplingSubscriptionTrackValidity.Required,
        ItemCount = checked((uint)itemCount),
        Flags = (uint)NativeSamplingSubscriptionSourceFlags.Persistent
    };

    private static NativeSamplingSubscriptionRemoveInput RemoveInput(
        NativeSamplingSubscriptionSession session, ulong epoch, ulong sourceHandle) => new()
    {
        AbiVersion = NativeSamplingSubscriptionAbi.Version,
        StructSize = (uint)Unsafe.SizeOf<NativeSamplingSubscriptionRemoveInput>(),
        ConfigurationGeneration = session.ConfigurationGeneration,
        OperationEpoch = epoch,
        CommandAtMilliseconds = CommandAt,
        SourceHandle = sourceHandle,
        ValidMask = (ulong)NativeSamplingSubscriptionRemoveValidity.Required
    };

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
