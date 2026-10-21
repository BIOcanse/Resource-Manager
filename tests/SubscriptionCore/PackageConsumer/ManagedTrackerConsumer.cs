using System.Runtime.CompilerServices;
using ResourceManager.Subscription;
using ResourceManager.Subscription.Native;

internal static class ManagedTrackerConsumer
{
    internal static void Run()
    {
        using var owner = new Owner();
        var tracker = new NativeItemSamplingSubscriptionTracker<string[], string>(owner, 1,
            static request => string.Join(',', request), static request => request,
            static (items, sources) => items.Count == 0 ? null : items.ToArray(), false);
        var now = DateTimeOffset.Parse("2026-10-07T00:00:00Z");
        tracker.TrackPersistent("slow", ["one", "two"], now, TimeSpan.FromSeconds(1));
        tracker.TrackPersistent("fast", ["one"], now, TimeSpan.FromMilliseconds(250));
        var first = tracker.CreatePlan(now);
        Require(first.DueItemIds.SequenceEqual(["one", "two"]) && owner.Leases == 1, "managed native plan and retained lease");
        Require(first.OwnerToken == "exact caller context", "typed publication context is unchanged");
        var completion = tracker.MarkSampled(first.Request!, now);
        Require(owner.Leases == 0, "completion releases the plan lease");
        var waiting = tracker.CreatePlan(now.AddMilliseconds(100));
        Require(waiting.Request is null && waiting.ActiveItemIds.SequenceEqual(["one", "two"]) && owner.Leases == 0,
            "active demand is independent of due demand");
        // Commands also advance the native monotonic clock. Poll after its
        // reported deadline instead of assuming completion occurred at 'now'.
        var fastAt = completion.Schedule.NextWakeAt!.Value.AddMilliseconds(100);
        var fast = tracker.CreatePlan(fastAt);
        Require(fast.DueItemIds.SequenceEqual(["one"]), "managed fastest-only plan");
        tracker.MarkSampled(fast.Request!, fastAt);
        Require(tracker.Remove("fast", fastAt.AddMilliseconds(1)), "managed source removal");
        var retained = tracker.CreatePlan(fastAt.AddMilliseconds(2));
        Require(retained.ActiveItemIds.SequenceEqual(["one", "two"]), "remaining shared demand");
        if (retained.Request is { } retainedRequest) tracker.MarkSampled(retainedRequest, fastAt.AddMilliseconds(2));
        Require(tracker.Remove("slow", fastAt.AddMilliseconds(3)), "last managed source removal");
        Require(!tracker.CreatePlan(fastAt.AddMilliseconds(4)).IsActive, "managed idle after last removal");
        tracker.Track(["transient"], now.AddSeconds(1));
        var transient = tracker.CreatePlan(now.AddSeconds(1));
        tracker.MarkSampled(transient.Request!, now.AddSeconds(1));
        var expired = tracker.CreatePlan(now.AddSeconds(5));
        Require(!expired.IsActive && expired.Schedule.ExpiredSourceCount == 1, "managed transient expiry");
        tracker.TrackPersistent("restarted", ["new"], now.AddSeconds(6), TimeSpan.FromSeconds(1));
        var restarted = tracker.CreatePlan(now.AddSeconds(6));
        Require(restarted.Request!.SequenceEqual(["new"]), "managed resubscription");
        tracker.MarkSampled(restarted.Request!, now.AddSeconds(6));
        tracker.Clear(now.AddSeconds(6));
        Require(owner.Leases == 0, "managed clear releases all borrows");
        Console.WriteLine("SubscriptionCore managed package consumer passed: original native-backed tracker, typed context, exact due requests and lease release.");
    }

    private static void Require(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); }

    internal sealed class Owner : ISamplingSubscriptionSessionOwner<string>, IDisposable
    {
        private readonly NativeSamplingSubscriptionSession session;
        private int leases;
        internal int Leases => Volatile.Read(ref leases);
        private readonly SubscriptionRoleSettings settings = new(1, 1_000, 1, 3_000, 1_000, 1_000);
        internal Owner()
        {
            var configuration = new NativeSamplingSubscriptionConfiguration
            {
                AbiVersion = NativeSamplingSubscriptionAbi.Version, StructSize = (uint)Unsafe.SizeOf<NativeSamplingSubscriptionConfiguration>(),
                Generation = 1, MaximumSourceCount = 8, MaximumItemCount = 8, MaximumMembershipCount = 16,
                MaximumDueItemCount = 8, MaximumSourceViewCount = 8, MaximumExpiredSourceCount = 8,
                DefaultIntervalMilliseconds = 1_000, MinimumIntervalMilliseconds = 1, ActiveTtlMilliseconds = 3_000,
                MaximumFutureSkewMilliseconds = 1_000
            };
            session = new(in configuration);
        }
        public ISamplingSubscriptionSessionLease<string> Acquire(int roleId)
        { Require(roleId == 1, "explicit owner role"); Interlocked.Increment(ref leases); return new Lease(this); }
        public ISamplingSubscriptionSessionLease<string>? TryAcquireForCleanup(int roleId) => Acquire(roleId);
        public void Dispose() { Require(Leases == 0, "owner has no unfinished borrow"); session.Dispose(); }
        private sealed class Lease : ISamplingSubscriptionSessionLease<string>
        {
            private readonly Owner owner;
            private Owner? holder;
            internal Lease(Owner owner) { this.owner = owner; holder = owner; }
            public NativeSamplingSubscriptionSession Session => owner.session;
            public ISubscriptionRoleSettings HotPublish => owner.settings;
            public ulong WorkspaceIncarnation => 1;
            public string OwnerToken => "exact caller context";
            public void Dispose() { var previous = Interlocked.Exchange(ref holder, null); if (previous is not null) Interlocked.Decrement(ref previous.leases); }
        }
    }
}
