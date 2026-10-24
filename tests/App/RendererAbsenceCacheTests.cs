using Microsoft.Diagnostics.Tracing.Parsers;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Infrastructure.GpuPlacement.External;
using ResourceManager.App.Infrastructure.Telemetry.Etw;

namespace Resource_Manager_APP.Tests;

public sealed class RendererAbsenceCacheTests
{
    [Fact]
    public void RuntimeSkipsAbsentInstanceUntilImageLoadThenReverifiesQualifiedResultsEveryCycle()
    {
        var broker = new FakeBroker();
        using var cache = new RendererAbsenceCache(broker);
        var api = new RendererQualificationReadCycleTests.Api();
        var identity = api.Add(100);
        for (var cycle = 0; cycle < 2; cycle++)
        {
            using var reads = new RendererQualificationReadCycle(api);
            Assert.Null(WindowsExternalGpuPlacementRuntime.ReadQualifiedRendererPlan(identity, reads,
                _ => [new(1, 0, 12)], true, true, cache));
            Assert.Equal(cycle, reads.NegativeHits);
        }
        Assert.Equal(1, api.Opens);
        Assert.Equal(1, api.ModuleReads);
        cache.OnProcessChanged(broker.Source, identity.ProcessId);
        api.ConfigureQt(false);
        for (var cycle = 0; cycle < 2; cycle++)
        {
            using var reads = new RendererQualificationReadCycle(api);
            Assert.NotNull(WindowsExternalGpuPlacementRuntime.ReadQualifiedRendererPlan(identity, reads,
                _ => [new(1, 0, 12)], true, true, cache));
            Assert.Equal(0, reads.NegativeHits);
        }
        Assert.Equal(3, api.Opens);
        Assert.Equal(3, api.Closes);
        Assert.Equal(3, api.ModuleReads);
        Assert.Equal(4, api.VersionsRead.Count);
        Assert.Equal(8, api.HashesRead.Count);
        Assert.False(cache.Read(identity).IsAbsent);
    }

    [Fact]
    public void RuntimeReadsANewInstanceOnTheSamePidAndDoesNotPublishStaleCycleModuleFacts()
    {
        var broker = new FakeBroker();
        using var cache = new RendererAbsenceCache(broker);
        var api = new RendererQualificationReadCycleTests.Api();
        var identity = api.Add(100);
        using (var reads = new RendererQualificationReadCycle(api))
        {
            Assert.Null(WindowsExternalGpuPlacementRuntime.ReadQualifiedRendererPlan(identity, reads,
                _ => [new(1, 0, 12)], true, true, cache));
            cache.OnProcessChanged(broker.Source, identity.ProcessId);
            api.ConfigureQt(false);
            Assert.Null(WindowsExternalGpuPlacementRuntime.ReadQualifiedRendererPlan(identity, reads,
                _ => [new(1, 0, 12)], true, true, cache)); // Same-cycle names precede the image event.
            Assert.False(cache.Read(identity).IsAbsent); // They must not publish under a newer event stamp.
        }
        var next = identity with { ProcessStartKey = identity.ProcessStartKey + 1 };
        api.Identities[100] = api.Identities[100] with { StartKey = next.ProcessStartKey };
        using (var reads = new RendererQualificationReadCycle(api))
            Assert.NotNull(WindowsExternalGpuPlacementRuntime.ReadQualifiedRendererPlan(next, reads,
                _ => [new(1, 0, 12)], true, true, cache));
        Assert.Equal(2, api.Opens);
        Assert.Equal(2, api.ModuleReads);
    }

    [Fact]
    public void RuntimeReadsReusedPidEvenWhileOldInstanceHasAValidNegative()
    {
        var broker = new FakeBroker();
        using var cache = new RendererAbsenceCache(broker);
        var api = new RendererQualificationReadCycleTests.Api();
        var identity = api.Add(100);
        using (var reads = new RendererQualificationReadCycle(api))
            Assert.Null(WindowsExternalGpuPlacementRuntime.ReadQualifiedRendererPlan(identity, reads,
                _ => [new(1, 0, 12)], true, true, cache));
        Assert.True(cache.Read(identity).IsAbsent);
        var next = identity with { ProcessStartKey = identity.ProcessStartKey + 1 };
        api.Identities[100] = api.Identities[100] with { StartKey = next.ProcessStartKey };
        using (var reads = new RendererQualificationReadCycle(api))
            Assert.Null(WindowsExternalGpuPlacementRuntime.ReadQualifiedRendererPlan(next, reads,
                _ => [new(1, 0, 12)], true, true, cache));
        Assert.Equal(2, api.Opens);
        Assert.Equal(2, api.ModuleReads);
        Assert.Equal(2, api.Closes);
    }

    [Fact]
    public void PublishesAndHitsOnlyTheExactProcessIdentity()
    {
        var broker = new FakeBroker();
        using var cache = new RendererAbsenceCache(broker);
        var identity = Process(101, 1001, @"C:\Apps\browser.exe");

        Assert.Equal(0, broker.SubscribeCount);
        var observation = cache.Read(identity);
        Assert.False(observation.IsAbsent);
        Assert.True(observation.CanPublishAbsent);
        cache.PublishAbsent(identity, observation);

        Assert.True(cache.Read(identity).IsAbsent);
        Assert.False(cache.Read(Process(101, 1002, identity.ExecutablePath)).IsAbsent);
        Assert.False(cache.Read(Process(101, 1001, @"C:\Apps\other.exe")).IsAbsent);
        Assert.False(cache.Read(Process(102, 1001, identity.ExecutablePath)).IsAbsent);
        Assert.Equal(1, broker.SubscribeCount);
        Assert.Equal(KernelTraceEventParser.Keywords.Process | KernelTraceEventParser.Keywords.ImageLoad,
            broker.Request!.Keywords);
    }

    [Fact]
    public void ProcessAndImageEventsInvalidatePidAndRejectAnOlderPublicationStamp()
    {
        var broker = new FakeBroker();
        using var cache = new RendererAbsenceCache(broker);
        var identity = Process(201, 2001, @"C:\Apps\renderer.exe");
        var first = cache.Read(identity);
        cache.PublishAbsent(identity, first);
        Assert.True(cache.Read(identity).IsAbsent);

        cache.OnProcessChanged(broker.Source, identity.ProcessId);
        cache.PublishAbsent(identity, first);
        Assert.False(cache.Read(identity).IsAbsent);

        var afterEvent = cache.Read(identity);
        cache.OnProcessChanged(broker.Source, identity.ProcessId);
        cache.PublishAbsent(identity, afterEvent);
        Assert.False(cache.Read(identity).IsAbsent);
    }

    [Fact]
    public void ProcessStartInvalidationDoesNotTransferNegativeAcrossPidReuse()
    {
        var broker = new FakeBroker();
        using var cache = new RendererAbsenceCache(broker);
        var oldIdentity = Process(301, 3001, @"C:\Apps\renderer.exe");
        var newIdentity = Process(301, 3002, oldIdentity.ExecutablePath);
        var oldObservation = cache.Read(oldIdentity);
        cache.PublishAbsent(oldIdentity, oldObservation);

        cache.OnProcessChanged(broker.Source, oldIdentity.ProcessId);
        Assert.False(cache.Read(oldIdentity).IsAbsent);
        Assert.False(cache.Read(newIdentity).IsAbsent);

        cache.PublishAbsent(newIdentity, cache.Read(newIdentity));
        Assert.True(cache.Read(newIdentity).IsAbsent);
        Assert.False(cache.Read(oldIdentity).IsAbsent);
    }

    [Fact]
    public void LossUnavailableAndRebindClearEntriesAndRejectOldStampsAndCallbacks()
    {
        var broker = new FakeBroker();
        using var cache = new RendererAbsenceCache(broker);
        var identity = Process(401, 4001, @"C:\Apps\renderer.exe");
        var beforeLoss = cache.Read(identity);
        cache.PublishAbsent(identity, beforeLoss);
        broker.SetEventsLost(1);
        cache.PublishAbsent(identity, beforeLoss);
        Assert.False(cache.Read(identity).IsAbsent);
        cache.PublishAbsent(identity, cache.Read(identity));
        Assert.True(cache.Read(identity).IsAbsent);

        cache.OnProcessChanged(broker.Source, identity.ProcessId);
        var beforeUnavailable = cache.Read(identity);
        broker.SetState("Unavailable");
        cache.PublishAbsent(identity, beforeUnavailable);
        Assert.False(cache.Read(identity).IsAbsent);
        broker.SetState("Running");
        Assert.False(cache.Read(identity).IsAbsent);

        cache.PublishAbsent(identity, cache.Read(identity));
        Assert.True(cache.Read(identity).IsAbsent);
        var oldSource = broker.Source;
        cache.OnProcessChanged(oldSource, identity.ProcessId);
        var beforeRebind = cache.Read(identity);
        broker.Rebind(2);
        cache.PublishAbsent(identity, beforeRebind);
        Assert.False(cache.Read(identity).IsAbsent);
        cache.PublishAbsent(identity, cache.Read(identity));
        Assert.True(cache.Read(identity).IsAbsent);

        cache.OnProcessChanged(oldSource, identity.ProcessId);
        Assert.True(cache.Read(identity).IsAbsent);

        cache.OnProcessChanged(broker.Source, identity.ProcessId);
        var beforeCoverageLoss = cache.Read(identity);
        broker.SetKeywords(KernelTraceEventParser.Keywords.Process);
        cache.PublishAbsent(identity, beforeCoverageLoss);
        Assert.False(cache.Read(identity).IsAbsent);
        broker.SetKeywords(KernelTraceEventParser.Keywords.Process | KernelTraceEventParser.Keywords.ImageLoad);
        Assert.False(cache.Read(identity).IsAbsent);
    }

    [Fact]
    public void CapacityOverflowClearsExistingNegativesAndDeclinesTheOverflowInsert()
    {
        var broker = new FakeBroker();
        using var cache = new RendererAbsenceCache(broker);
        var first = Process(10_000, 10_000, @"C:\Apps\renderer.exe");

        for (var index = 0; index < RendererAbsenceCache.MaximumCachedEntries; index++)
        {
            var identity = first with { ProcessId = first.ProcessId + index, ProcessStartKey = (ulong)(first.ProcessStartKey + (uint)index) };
            cache.PublishAbsent(identity, cache.Read(identity));
        }

        Assert.True(cache.Read(first).IsAbsent);
        var overflowIdentity = first with
        {
            ProcessId = first.ProcessId + RendererAbsenceCache.MaximumCachedEntries,
            ProcessStartKey = (ulong)(first.ProcessStartKey + RendererAbsenceCache.MaximumCachedEntries)
        };
        cache.PublishAbsent(overflowIdentity, cache.Read(overflowIdentity));

        Assert.False(cache.Read(first).IsAbsent);
        Assert.False(cache.Read(overflowIdentity).IsAbsent);
    }

    [Fact]
    public void DisposalReleasesOnlyTheLazilyAcquiredLease()
    {
        var broker = new FakeBroker();
        var cache = new RendererAbsenceCache(broker);
        _ = cache.Read(Process(501, 5001, @"C:\Apps\renderer.exe"));

        cache.Dispose();
        cache.Dispose();

        Assert.Equal(1, broker.DisposeCount);
        Assert.True(broker.GetSnapshot().IsRunning);
        Assert.False(broker.Source.IsActive);
    }

    private static GpuPlacementProcessInstance Process(int processId, ulong startKey, string path)
        => new(processId, startKey, "renderer.exe", path);

    private sealed class FakeBroker : IKernelEtwSessionBroker
    {
        private KernelEtwSessionSnapshot snapshot = new("Running", "fixture", 1, DateTimeOffset.UnixEpoch,
            KernelTraceEventParser.Keywords.Process | KernelTraceEventParser.Keywords.ImageLoad, 1, 0);
        private KernelEtwSubscriptionRequest? request;
        private KernelEtwSessionContext context = new(1, DateTimeOffset.UnixEpoch);

        internal KernelEtwSubscriptionRequest? Request => request;
        internal KernelEtwEventSource Source { get; private set; } = new();
        internal int SubscribeCount { get; private set; }
        internal int DisposeCount { get; private set; }

        public IKernelEtwSubscription Subscribe(KernelEtwSubscriptionRequest subscriptionRequest)
        {
            request = subscriptionRequest;
            SubscribeCount++;
            BindNewSource();
            return new Lease(this);
        }

        public KernelEtwSessionSnapshot GetSnapshot() => snapshot;

        internal void SetEventsLost(int value) => snapshot = snapshot with { EventsLost = value };
        internal void SetState(string value) => snapshot = snapshot with { State = value };
        internal void SetKeywords(KernelTraceEventParser.Keywords value) => snapshot = snapshot with { EnabledKeywords = value };

        internal void Rebind(long generation)
        {
            context = context with { Generation = generation };
            snapshot = snapshot with { Generation = generation, StartedAt = context.StartedAt };
            BindNewSource();
        }

        private void BindNewSource()
        {
            Source = new KernelEtwEventSource();
            request!.Bind(Source, context);
            Source.Activate();
        }

        private sealed class Lease(FakeBroker owner) : IKernelEtwSubscription
        {
            private bool disposed;
            public KernelEtwSessionSnapshot GetSnapshot() => owner.snapshot;

            public void Dispose()
            {
                if (disposed) return;
                disposed = true;
                owner.DisposeCount++;
                owner.Source.Cancel();
            }
        }
    }
}
