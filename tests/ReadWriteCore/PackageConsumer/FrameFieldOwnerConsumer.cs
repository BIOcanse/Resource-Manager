using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Windows.Frames;

internal static class FrameFieldOwnerConsumer
{
    internal static async Task RunAsync()
    {
        var query = new PresentFrameFieldQuery(21, 134056632000000000,
            FramePresentSource.Dxgi, 0xA0, TimeSpan.FromMilliseconds(1250), FrameTimePercentile.P99);
        var source = new RecordedPresentSource();
        using var owner = new WindowsPresentFrameFieldOwner(source, query);
        var backend = owner.CreateBackend();
        var fps = query.Field(FrameFieldRegistry.PresentedFps);
        Require(backend.ReadCached(fps).Status == FieldObservationStatus.Unavailable
            && source.Acquisitions == 0 && source.Reads == 0, "passive cached read");

        var direct = await backend.CollectAsync([fps], CancellationToken.None);
        Require(direct.Single().Value?.Number == 50 && source.Acquisitions == 1 && source.ActiveLeases == 1,
            "explicit direct frame read from the selected source");
        using (var borrow = owner.BorrowSubscriptionCapture())
        {
            borrow.SynchronizeDemand([fps]);
            var result = await borrow.CollectAsync([fps], new AcceptingPermit());
            Require(result.Committed && result.Readings.Single().Value?.Number == 50
                && source.ActiveLeases == 2, "subscription borrow shares the owner's current");
        }
        Require(source.ActiveLeases == 1, "borrow releases only its source lease");
        var display = backend.Catalog.Fields.Single(item => item.Field.FieldName == FrameFieldRegistry.DisplayedFps);
        Require(display.Read.Supported && display.Read.Reason is null
            && backend.ReadCached(display.Field).Status == FieldObservationStatus.Unavailable,
            "display completion is supported but missing completion evidence stays unavailable");
        owner.Dispose();
        Require(source.ActiveLeases == 0, "direct owner lease closes on explicit disposal");
        Console.WriteLine("ReadWriteCore package consumer passed: exact frame query, passive cache, direct read, shared borrow and lease ownership.");
    }

    private static void Require(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
    }

    private sealed class RecordedPresentSource : IFrameTimingObservationSource
    {
        private int acquisitions;
        private int activeLeases;
        public int Acquisitions => Volatile.Read(ref acquisitions);
        public int ActiveLeases => Volatile.Read(ref activeLeases);
        public int Reads { get; private set; }

        public TimeSpan Retention => TimeSpan.FromSeconds(30);
        public IFrameSubscriptionLease AcquireSubscription(FrameSubscriptionRequest request) => new FrameLeaseAdapter(AcquireSubscription());
        private sealed class FrameLeaseAdapter(IDisposable inner) : IFrameSubscriptionLease
        {
            public void Update(FrameSubscriptionRequest request) { }
            public void Dispose() => inner.Dispose();
        }

        public IDisposable AcquireSubscription()
        {
            Interlocked.Increment(ref acquisitions);
            Interlocked.Increment(ref activeLeases);
            return new Lease(() => Interlocked.Decrement(ref activeLeases));
        }

        public FrameTimingSnapshot? Read(TimeSpan window)
        {
            Reads++;
            var statistics = FrameIntervalStatistics.Compute([10d, 20d, 30d])!;
            return new(DateTimeOffset.FromUnixTimeMilliseconds(1_800_000_000_000), window, true,
                [new(21, 134056632000000000,
                    [new(FramePresentSource.Dxgi, 0xA0,
                        DateTimeOffset.FromUnixTimeMilliseconds(1_800_000_000_000), statistics)])]);
        }

        public FrameIntervalBatch? ReadIntervals(DateTimeOffset after, DateTimeOffset through)
            => new(through, true, []);

        private sealed class Lease(Action release) : IDisposable
        {
            private Action? release = release;
            public void Dispose() => Interlocked.Exchange(ref release, null)?.Invoke();
        }
    }

    private sealed class AcceptingPermit : ResourceManager.App.Infrastructure.Monitoring.IMetricPublicationPermit
    {
        public ulong WorkspaceIncarnation => 1;
        public ulong ConfigurationGeneration => 1;
        public bool IsActive => true;
        public bool TryPublish(Action commit) { commit(); return true; }
    }
}
