using Microsoft.Diagnostics.Tracing.Parsers;
using Resource_Manager_APP.Tests.KernelEtwFixtures;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Infrastructure.GpuPlacement.External;
using ResourceManager.App.Infrastructure.Telemetry.Etw;

namespace Resource_Manager_APP.Tests;

public sealed class RendererAbsenceCacheTests
{
    [Fact]
    public void MissingNativePublicationNeverQualifiesANegative()
    {
        using var broker = new NativeRendererTestBroker(publishOnAcquire: false);
        using var cache = new RendererAbsenceCache(broker);
        using var run = cache.Acquire();
        var identity = Process(701, 7001, @"C:\Apps\renderer.exe");
        var observation = cache.Read(identity);
        Assert.False(observation.CanPublishAbsent);
        cache.PublishAbsent(identity, observation);
        Assert.False(cache.Read(identity).IsAbsent);
        Assert.Equal(1, broker.SubscribeCount);
    }

    [Fact]
    public void NativeHistoryResetRejectsOldNegativesAndPendingPublicationStamps()
    {
        using var broker = new NativeRendererTestBroker();
        using var cache = new RendererAbsenceCache(broker);
        using var run = cache.Acquire();
        var identity = Process(702, 7002, @"C:\Apps\renderer.exe");
        var old = cache.Read(identity);
        cache.PublishAbsent(identity, old);
        Assert.True(cache.Read(identity).IsAbsent);
        var beforeHistory = KernelEtwNativeRendererHistory.Read(broker.Source, identity.ProcessId)!.Value;
        broker.ResetHistory();
        var afterHistory = KernelEtwNativeRendererHistory.Read(broker.Source, identity.ProcessId)!.Value;
        Assert.NotEqual(beforeHistory.Epoch, afterHistory.Epoch);
        cache.PublishAbsent(identity, old);
        Assert.False(cache.Read(identity).IsAbsent);
        cache.PublishAbsent(identity, cache.Read(identity));
        Assert.True(cache.Read(identity).IsAbsent);
    }

    [Fact]
    public void PassiveReadsAndPublicationNeverAcquireAndRunReleaseRejectsOldEvidence()
    {
        using var broker = new NativeRendererTestBroker();
        using var cache = new RendererAbsenceCache(broker);
        var identity = Process(601, 6001, @"C:\Apps\renderer.exe");
        var inactive = cache.Read(identity);
        cache.PublishAbsent(identity, inactive);
        Assert.False(inactive.IsAbsent);
        Assert.False(inactive.CanPublishAbsent);
        Assert.Equal(0, broker.SubscribeCount);

        var first = cache.Acquire();
        var second = cache.Acquire();
        var old = cache.Read(identity);
        cache.PublishAbsent(identity, old);
        Assert.True(cache.Read(identity).IsAbsent);
        first.Dispose();
        Assert.Equal(0, broker.DisposeCount);
        Assert.True(cache.Read(identity).IsAbsent);
        second.Dispose();
        Assert.Equal(1, broker.DisposeCount);
        Assert.False(broker.Source.IsActive);
        cache.PublishAbsent(identity, old);
        Assert.False(cache.Read(identity).CanPublishAbsent);
        Assert.Equal(1, broker.SubscribeCount);

        using var next = cache.Acquire();
        cache.PublishAbsent(identity, old);
        Assert.False(cache.Read(identity).IsAbsent);
        Assert.True(cache.Read(identity).CanPublishAbsent);
        Assert.Equal(2, broker.SubscribeCount);
    }

    [Fact]
    public void TypedUnknownArgumentsNeverPublishNegativeAndAreRetriedNextCycle()
    {
        using var broker = new NativeRendererTestBroker();
        using var cache = new RendererAbsenceCache(broker);
        using var observationRun = cache.Acquire();
        var api = new RendererQualificationReadCycleTests.Api();
        var parent = api.Add(90, 133_899_999_999_999_990);
        var gpu = api.Add(100);
        api.Parents[100] = parent.ProcessId;
        api.Arguments[100] = [gpu.ExecutablePath, "--type=gpu-process"];
        api.UnreadableArguments.Add(100);
        for (var cycle = 0; cycle < 2; cycle++)
        {
            using var reads = new RendererQualificationReadCycle(api);
            Assert.Null(WindowsExternalGpuPlacementRuntime.ReadQualifiedRendererPlan(gpu, reads,
                _ => [new(1, 0, 11)], true, true, cache));
            Assert.False(WindowsExternalGpuPlacementRuntime.HasNoRendererMarkers(gpu, reads));
            Assert.False(cache.Read(gpu).IsAbsent);
            Assert.Equal(0, reads.NegativeHits);
        }
        Assert.Equal(2, api.ArgumentReads);
        Assert.Equal(2, api.ModuleReads);
        api.UnreadableArguments.Clear();
        using (var reads = new RendererQualificationReadCycle(api))
        {
            Assert.NotNull(WindowsExternalGpuPlacementRuntime.ReadQualifiedRendererPlan(gpu, reads,
                _ => [new(1, 0, 11)], true, true, cache));
            Assert.Equal(0, reads.NegativeHits);
        }
        Assert.False(cache.Read(gpu).IsAbsent);
        Assert.Equal(api.Opens, api.Closes);
    }

    [Fact]
    public void RuntimeSkipsAbsentInstanceUntilImageLoadThenReverifiesQualifiedResultsEveryCycle()
    {
        using var broker = new NativeRendererTestBroker();
        using var cache = new RendererAbsenceCache(broker);
        using var observationRun = cache.Acquire();
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
        broker.ProcessChanged(broker.Source, identity.ProcessId);
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
        using var broker = new NativeRendererTestBroker();
        using var cache = new RendererAbsenceCache(broker);
        using var observationRun = cache.Acquire();
        var api = new RendererQualificationReadCycleTests.Api();
        var identity = api.Add(100);
        using (var reads = new RendererQualificationReadCycle(api))
        {
            Assert.Null(WindowsExternalGpuPlacementRuntime.ReadQualifiedRendererPlan(identity, reads,
                _ => [new(1, 0, 12)], true, true, cache));
            broker.ProcessChanged(broker.Source, identity.ProcessId);
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
        using var broker = new NativeRendererTestBroker();
        using var cache = new RendererAbsenceCache(broker);
        using var observationRun = cache.Acquire();
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
        using var broker = new NativeRendererTestBroker();
        using var cache = new RendererAbsenceCache(broker);
        using var observationRun = cache.Acquire();
        var identity = Process(101, 1001, @"C:\Apps\browser.exe");

        Assert.Equal(1, broker.SubscribeCount);
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
        Assert.Equal(KernelEtwNativeOutputs.Renderer, broker.Request.NativeOutputs);
    }

    [Fact]
    public void ProcessAndImageEventsInvalidatePidAndRejectAnOlderPublicationStamp()
    {
        using var broker = new NativeRendererTestBroker();
        using var cache = new RendererAbsenceCache(broker);
        using var observationRun = cache.Acquire();
        var identity = Process(201, 2001, @"C:\Apps\renderer.exe");
        var first = cache.Read(identity);
        cache.PublishAbsent(identity, first);
        Assert.True(cache.Read(identity).IsAbsent);

        broker.ProcessChanged(broker.Source, identity.ProcessId, processStart: true);
        cache.PublishAbsent(identity, first);
        Assert.False(cache.Read(identity).IsAbsent);

        var afterEvent = cache.Read(identity);
        broker.ProcessChanged(broker.Source, identity.ProcessId);
        cache.PublishAbsent(identity, afterEvent);
        Assert.False(cache.Read(identity).IsAbsent);
    }

    [Fact]
    public void ProcessStartInvalidationDoesNotTransferNegativeAcrossPidReuse()
    {
        using var broker = new NativeRendererTestBroker();
        using var cache = new RendererAbsenceCache(broker);
        using var observationRun = cache.Acquire();
        var oldIdentity = Process(301, 3001, @"C:\Apps\renderer.exe");
        var newIdentity = Process(301, 3002, oldIdentity.ExecutablePath);
        var oldObservation = cache.Read(oldIdentity);
        cache.PublishAbsent(oldIdentity, oldObservation);

        broker.ProcessChanged(broker.Source, oldIdentity.ProcessId, processStart: true);
        Assert.False(cache.Read(oldIdentity).IsAbsent);
        Assert.False(cache.Read(newIdentity).IsAbsent);

        cache.PublishAbsent(newIdentity, cache.Read(newIdentity));
        Assert.True(cache.Read(newIdentity).IsAbsent);
        Assert.False(cache.Read(oldIdentity).IsAbsent);
    }

    [Fact]
    public void LossUnavailableAndRebindClearEntriesAndRejectOldStampsAndCallbacks()
    {
        using var broker = new NativeRendererTestBroker();
        using var cache = new RendererAbsenceCache(broker);
        using var observationRun = cache.Acquire();
        var identity = Process(401, 4001, @"C:\Apps\renderer.exe");
        var beforeLoss = cache.Read(identity);
        cache.PublishAbsent(identity, beforeLoss);
        broker.SetEventsLost(1);
        cache.PublishAbsent(identity, beforeLoss);
        Assert.False(cache.Read(identity).IsAbsent);
        cache.PublishAbsent(identity, cache.Read(identity));
        Assert.True(cache.Read(identity).IsAbsent);

        broker.ProcessChanged(broker.Source, identity.ProcessId);
        var beforeUnavailable = cache.Read(identity);
        broker.SetState("Unavailable");
        cache.PublishAbsent(identity, beforeUnavailable);
        Assert.False(cache.Read(identity).IsAbsent);
        broker.SetState("Running");
        Assert.False(cache.Read(identity).IsAbsent);

        cache.PublishAbsent(identity, cache.Read(identity));
        Assert.True(cache.Read(identity).IsAbsent);
        var oldSource = broker.Source;
        broker.ProcessChanged(oldSource, identity.ProcessId);
        var beforeRebind = cache.Read(identity);
        broker.Rebind(2);
        cache.PublishAbsent(identity, beforeRebind);
        Assert.False(cache.Read(identity).IsAbsent);
        cache.PublishAbsent(identity, cache.Read(identity));
        Assert.True(cache.Read(identity).IsAbsent);

        broker.ProcessChanged(oldSource, identity.ProcessId);
        Assert.True(cache.Read(identity).IsAbsent);

        broker.ProcessChanged(broker.Source, identity.ProcessId);
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
        using var broker = new NativeRendererTestBroker();
        using var cache = new RendererAbsenceCache(broker);
        using var observationRun = cache.Acquire();
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
    public void DisposalReleasesOnlyItsExplicitlyAcquiredLease()
    {
        using var broker = new NativeRendererTestBroker(keepSiblingLease: true);
        var cache = new RendererAbsenceCache(broker);
        using var observationRun = cache.Acquire();
        _ = cache.Read(Process(501, 5001, @"C:\Apps\renderer.exe"));

        cache.Dispose();
        cache.Dispose();

        Assert.Equal(1, broker.DisposeCount);
        Assert.True(broker.GetSnapshot().IsRunning);
        Assert.False(broker.Source.IsActive);
    }

    private static GpuPlacementProcessInstance Process(int processId, ulong startKey, string path)
        => new(processId, startKey, "renderer.exe", path);

}
