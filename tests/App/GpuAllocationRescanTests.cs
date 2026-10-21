using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Infrastructure.Telemetry.Etw;

namespace Resource_Manager_APP.Tests;

public sealed class GpuAllocationRescanTests
{
    private static readonly DateTime At = new(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);
    private static readonly IReadOnlyDictionary<int, long> Processes = new Dictionary<int, long> { [41] = At.AddMinutes(-1).ToFileTimeUtc() };

    [Fact]
    public void HistoricalEventLossDoesNotPermanentlyRejectACompleteRescan()
    {
        using var collector = NewCollector(); // Does not acquire an ETW subscription or touch hardware.
        collector.BeginRescan(1, 0); Populate(collector, 1024); collector.CompleteRescan(1, 0);
        Assert.Equal(1024, collector.ReadVerifiedCurrent(Processes, 0)!.Adapters[23][41].PrivateBytes);
        Assert.Null(collector.ReadVerifiedCurrent(Processes, 1));
        collector.BeginRescan(2, 1); Populate(collector, 2048); collector.CompleteRescan(2, 1);
        Assert.Equal(2048, collector.ReadVerifiedCurrent(Processes, 1)!.Adapters[23][41].PrivateBytes);
        Assert.Equal(2, collector.CompletedRescans);
    }

    [Fact]
    public void LossDuringRescanOrBadEventsCannotPublishPartialValues()
    {
        using var collector = NewCollector();
        collector.BeginRescan(1, 0); Populate(collector, 1024); collector.CompleteRescan(1, 1);
        Assert.Null(collector.ReadVerifiedCurrent(Processes, 1));
        collector.BeginRescan(2, 1);
        collector.ObserveAllocation(33, _ => throw new InvalidDataException("owned malformed event"), At);
        collector.CompleteRescan(2, 1);
        Assert.Null(collector.ReadVerifiedCurrent(Processes, 1));
        Assert.Equal(0, collector.CompletedRescans);
        collector.BeginRescan(3, 1); Populate(collector, 4096);
        collector.CompleteRescan(2, 1); Assert.Null(collector.ReadVerifiedCurrent(Processes, 1));
        collector.CompleteRescan(3, 1);
        Assert.Equal(4096, collector.ReadVerifiedCurrent(Processes, 1)!.Adapters[23][41].PrivateBytes);
    }

    private static GpuAllocationCollector NewCollector()
        => new(NullLogger<GpuAllocationCollector>.Instance, new ConfigurationBuilder().Build());
    private static void Populate(GpuAllocationCollector collector, ulong bytes)
    {
        Event(110, ("pDxgAdapter", 1UL), ("AdapterLuid", 23UL));
        Event(78, ("pDxgAdapter", 1UL), ("ulSegmentId", 2U), ("Flags", 0U));
        Event(33, ("pDxgAdapter", 1UL), ("hVidMmGlobalAlloc", 2UL), ("allocSize", bytes), ("PageTableOrDirectory", false));
        Event(36, ("pDxgAdapter", 1UL), ("hVidMmGlobalAlloc", 2UL), ("hVidMmAlloc", 11UL), ("hProcessId", 41U));
        Event(391, ("hGlobalAllocationHandle", 2UL), ("ulSegmentId", 2U), ("SegmentOffset", 0UL));
        void Event(int id, params (string Key, object Value)[] data)
        {
            var payload = data.ToDictionary(item => item.Key, item => item.Value);
            collector.ObserveAllocation(id, key => payload[key], At);
        }
    }
}
