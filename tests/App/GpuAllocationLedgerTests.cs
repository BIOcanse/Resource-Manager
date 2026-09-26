using ResourceManager.App.Infrastructure.Telemetry.Etw;
using ResourceManager.App.Infrastructure.ResourceBreakdown;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.ResourceBreakdown;

namespace Resource_Manager_APP.Tests;

public sealed class GpuAllocationLedgerTests
{
    private static GpuAllocationLedger Ledger()
    {
        var ledger = new GpuAllocationLedger();
        ledger.SetAdapter(1, 23);
        ledger.SetSegment(1, 2, 0);
        ledger.SetAllocation(1, 2, 8UL * 1024 * 1024 * 1024);
        ledger.Open(1, 2, 11, 41, 200);
        ledger.SetCommittedGlobal(2, 2);
        return ledger;
    }
    private static readonly Dictionary<int, long> Births = new() { [41] = 100, [42] = 120 };

    [Fact]
    public void ShareUsesDistinctProcessesAndAllReferencesMustClose()
    {
        var ledger = Ledger();
        Assert.Equal(8D * 1024 * 1024 * 1024, ledger.Read(Births).Adapters[23][41].PrivateBytes);
        ledger.Open(1, 2, 12, 42, 220);
        ledger.Open(1, 2, 13, 42, 230);
        var split = ledger.Read(Births).Adapters[23];
        Assert.All(split.Values, v => { Assert.Equal(0, v.PrivateBytes); Assert.Equal(4D * 1024 * 1024 * 1024, v.SharedBytes); });
        ledger.Close(13);
        Assert.Equal(split, ledger.Read(Births).Adapters[23]);
        ledger.Close(12);
        ledger.Close(12);
        Assert.Equal(8D * 1024 * 1024 * 1024, ledger.Read(Births).Adapters[23][41].PrivateBytes);
        ledger.Delete(1, 2);
        Assert.Empty(ledger.Read(Births).Adapters);
    }

    [Fact]
    public void InvisibleOrReusedPidDoesNotIncreaseOtherUsersShare()
    {
        var ledger = Ledger();
        ledger.Open(1, 2, 12, 42, 220);
        var filtered = ledger.Read(new Dictionary<int, long> { [41] = 100, [42] = 300 }).Adapters[23];
        Assert.Single(filtered);
        Assert.Equal(4D * 1024 * 1024 * 1024, filtered[41].SharedBytes);
    }

    [Fact]
    public void RundownDoesNotDoubleCountAndReusedReferenceMovesAllocation()
    {
        var ledger = Ledger();
        ledger.SetAllocation(1, 2, 8UL * 1024 * 1024 * 1024);
        ledger.Open(1, 2, 11, 41, 250);
        ledger.SetAllocation(1, 3, 1024);
        ledger.SetCommittedGlobal(3, 2);
        ledger.Open(1, 3, 11, 41, 260);
        Assert.Equal(1024, ledger.Read(Births).Adapters[23][41].PrivateBytes);
    }

    [Fact]
    public void PageTableCreationAndRundownOwnerDifferenceNeverChangesSoftwareBytes()
    {
        var ledger = Ledger();
        ledger.SetAllocation(1, 3, 4096, kernelMetadata: true);
        ledger.SetCommittedGlobal(3, 2);
        ledger.Open(1, 3, 12, 4, 220);
        var created = ledger.Read(Births);
        ledger.Open(1, 3, 12, 41, 250);
        var rundown = ledger.Read(Births);
        Assert.Equal(created.Adapters[23], rundown.Adapters[23]);
        Assert.Equal(4096, rundown.KernelMetadataBytes[23]);
    }

    [Fact]
    public void MissingIdentityOrSizeCannotBecomeFabricatedZero()
    {
        var ledger = new GpuAllocationLedger();
        ledger.Open(1, 2, 3, 41, 200);
        Assert.Throws<InvalidDataException>(() => ledger.Read(Births));
        ledger.SetAdapter(1, 23);
        Assert.Throws<InvalidDataException>(() => ledger.Read(Births));
    }

    [Fact]
    public void ResidentSharesAreTheSameValuesUsedByScheduling()
    {
        var ledger = Ledger();
        ledger.Open(1, 2, 12, 42, 220);
        var old = SchedulingProcessGpuRead.NotRequested with
        {
            DedicatedMemoryBytes = new Dictionary<(ulong, int, ulong), double> { [(23, 41, 100)] = 999 },
            DedicatedMemoryStatus = SamplingObservationStatus.Current,
            DedicatedMemoryGeneration = 7,
            DedicatedMemoryObservedAtUtcTicks = 400
        };
        ProcessInstanceKey[] processes = [new(41, 100), new(42, 120)];
        var actual = GpuAllocationSchedulingProjection.Apply(old,
            ledger.Read(Births) with { Generation = 7, ObservedAtUtcTicks = 400 }, processes);
        Assert.Equal(4D * 1024 * 1024 * 1024, actual.DedicatedMemoryBytes[(23, 41, 100)]);
        Assert.Equal(4D * 1024 * 1024 * 1024, actual.AllocationAmounts[(23, 41, 100)].SharedBytes);
        Assert.Equal(7UL, actual.DedicatedMemoryGeneration);
        Assert.Equal(400, actual.DedicatedMemoryObservedAtUtcTicks);
        var observedAt = DateTimeOffset.UtcNow.UtcTicks;
        var inventory = new SchedulingGpuInventorySnapshot(SamplingObservationStatus.Current, 9, observedAt,
            1, 0, 0, 17,
            [new SchedulingGpuAdapterObservation(1, 23, SchedulingGpuCapabilityMask.DedicatedMemory,
                SchedulingGpuMetricMask.TotalDedicatedMemory | SchedulingGpuMetricMask.UsedDedicatedMemory,
                SamplingObservationStatus.NotRequested, SamplingObservationStatus.Current, 0, 0, 8UL * 1024 * 1024 * 1024,
                9, observedAt)]);
        var facts = WindowsResourceBreakdownSampler.CreateSchedulingGpuFacts(
            new SchedulingProcessFactRequest(SchedulingProcessMetricMask.GpuDedicatedMemory, null, inventory), actual, 41, 100);
        var fact = Assert.Single(facts);
        Assert.Equal(50, fact.DedicatedMemoryUsedPercent);
        Assert.Equal(0, fact.PrivateMemoryBytes);
        Assert.Equal(4D * 1024 * 1024 * 1024, fact.SharedMemoryBytes);
        Assert.Equal(fact.ResidentMemoryBytes, actual.DedicatedMemoryBytes[(23, 41, 100)]);
        var empty = GpuAllocationSchedulingProjection.Apply(old, null, processes);
        Assert.Empty(empty.DedicatedMemoryBytes);
        Assert.Empty(empty.AllocationAmounts);
        Assert.Equal(SamplingObservationStatus.Unavailable, empty.DedicatedMemoryStatus);
    }

    [Theory]
    [InlineData(SamplingObservationStatus.Unavailable)]
    [InlineData(SamplingObservationStatus.NotRequested)]
    public void UmaResidencyDoesNotRequireUsageOrDedicatedCapacity(SamplingObservationStatus usageStatus)
    {
        var now = DateTimeOffset.UtcNow.UtcTicks;
        var read = GpuAllocationSchedulingProjection.Apply(
            SchedulingProcessGpuRead.NotRequested with { UsageStatus = usageStatus },
            Ledger().Read(Births) with { Generation = 7, ObservedAtUtcTicks = now },
            [new ProcessInstanceKey(41, 100)]);
        var inventory = new SchedulingGpuInventorySnapshot(SamplingObservationStatus.Current, 9, now,
            1, 0, 0, 17,
            [new SchedulingGpuAdapterObservation(1, 23, SchedulingGpuCapabilityMask.None,
                SchedulingGpuMetricMask.None, usageStatus, SamplingObservationStatus.Unavailable,
                0, 0, 0, 9, now)]);
        var request = new SchedulingProcessFactRequest(SchedulingProcessMetricMask.GpuDedicatedMemory, null, inventory);
        var fact = Assert.Single(WindowsResourceBreakdownSampler.CreateSchedulingGpuFacts(request, read, 41, 100));
        Assert.Equal(read.AllocationAmounts[(23, 41, 100)].TotalBytes, fact.ResidentMemoryBytes);
        Assert.Equal(SchedulingProcessMetricMask.None, fact.ValidMetricMask);
        Assert.True(fact.HasMetric(SchedulingProcessMetricMask.GpuDedicatedMemory));
        Assert.Equal(7UL, fact.DedicatedMemorySourceGeneration);
        Assert.Equal(9UL, fact.DedicatedMemoryTopologyGeneration);

        var usageOnly = request with { RequestedMetricMask = SchedulingProcessMetricMask.GpuUsage };
        Assert.Empty(WindowsResourceBreakdownSampler.CreateSchedulingGpuFacts(usageOnly, read, 41, 100));
        Assert.Empty(WindowsResourceBreakdownSampler.CreateSchedulingGpuFacts(request,
            read with { DedicatedMemoryStatus = SamplingObservationStatus.Unavailable }, 41, 100));
    }

    [Fact]
    public void OnlyPhysicalRangesAreSharedNotTheRequestedAllocationSize()
    {
        var ledger = Ledger();
        ledger.DiscardResident(1, 2, 2);
        ledger.Open(1, 2, 12, 42, 220);
        ledger.AddResidentRange(1, 2, 2, 0, 1024);
        ledger.AddResidentRange(1, 2, 2, 512, 1024);
        ledger.AddResidentRange(1, 2, 2, 0, 1024);
        Assert.All(ledger.Read(Births).Adapters[23].Values, amount => Assert.Equal(768, amount.SharedBytes));
        ledger.DiscardResident(1, 2, 2);
        Assert.All(ledger.Read(Births).Adapters[23].Values, amount => Assert.Equal(0, amount.TotalBytes));
    }

    [Fact]
    public void TransferDestinationDoesNotInventSourceRetirementOrCountSystemMemoryAsVram()
    {
        var ledger = Ledger();
        ledger.DiscardResident(1, 2, 2);
        ledger.SetSegment(1, 3, 0x1000);
        ledger.AddResidentRange(1, 2, 3, 0, 4096);
        Assert.Equal(0, ledger.Read(Births).Adapters[23][41].TotalBytes);
        ledger.AddResidentRange(1, 2, 2, 0, 4096);
        Assert.Equal(4096, ledger.Read(Births).Adapters[23][41].TotalBytes);
        ledger.DiscardResident(1, 2, 3);
        Assert.Equal(4096, ledger.Read(Births).Adapters[23][41].TotalBytes);
        ledger.DiscardResident(1, 2, 2);
        Assert.Equal(0, ledger.Read(Births).Adapters[23][41].TotalBytes);
    }

    [Fact]
    public void PendingRundownResolvesWithoutAssigningKernelReferencesToApplications()
    {
        var ledger = new GpuAllocationLedger();
        ledger.SetAdapter(1, 23);
        ledger.SetSegment(1, 2, 0);
        ledger.SetCommittedReference(11, 41, 2, 1024);
        Assert.Throws<InvalidDataException>(() => ledger.Read(Births));
        ledger.SetAllocation(1, 2, 8192);
        ledger.Open(1, 2, 11, 41, 200);
        ledger.SetCommittedReference(99, 4, 2, 4096);
        Assert.Equal(1024, ledger.Read(Births).Adapters[23][41].TotalBytes);
        ledger.Delete(1, 2);
        ledger.SetAllocation(1, 2, 16384);
        ledger.Open(1, 2, 11, 41, 200);
        Assert.Equal(0, ledger.Read(Births).Adapters[23][41].TotalBytes);
    }

    [Fact]
    public void SameSegmentRelocationRetiresOnlyTheOldPhysicalPlacement()
    {
        var ledger = Ledger();
        ledger.DiscardResident(1, 2, 2);
        ledger.SetAllocation(1, 2, 4096);
        ledger.AddResidentRange(1, 2, 2, 0, 4096, 16384);
        ledger.AddResidentRange(1, 2, 2, 0, 4096, 32768);
        ledger.AddResidentRange(1, 2, 2, 0, 4096, 32768);
        Assert.Equal(8192, ledger.Read(Births).Adapters[23][41].TotalBytes);
        ledger.DiscardResident(1, 2, 2, 16384);
        ledger.DiscardResident(1, 2, 2, 16384);
        Assert.Equal(4096, ledger.Read(Births).Adapters[23][41].TotalBytes);
    }

    [Fact]
    public void DiscardCancelsPendingPlacementBeforeAllocationMetadataArrives()
    {
        var ledger = new GpuAllocationLedger();
        ledger.SetAdapter(1, 23);
        ledger.SetSegment(1, 2, 0);
        ledger.SetCommittedGlobal(2, 2, 16384);
        ledger.SetCommittedGlobal(2, 2, 32768);
        ledger.DiscardResident(1, 2, 2, 16384);
        ledger.SetAllocation(1, 2, 4096);
        ledger.Open(1, 2, 11, 41, 200);
        Assert.Equal(4096, ledger.Read(Births).Adapters[23][41].TotalBytes);
        ledger.DiscardResident(1, 2, 2, 32768);
        Assert.Equal(0, ledger.Read(Births).Adapters[23][41].TotalBytes);
    }

    [Theory]
    [InlineData(53)]
    [InlineData(306)]
    public void DecoderUsesTransferDestinationAddressAndDoesNotTreatEvictionAsRelease(int eventId)
    {
        var ledger = Ledger();
        ledger.DiscardResident(1, 2, 2);
        ledger.SetAllocation(1, 2, 4096);
        ledger.AddResidentRange(1, 2, 2, 0, 4096, 16384);
        var fields = new Dictionary<string, object>
        {
            ["pDxgAdapter"] = 1UL, ["hAllocationGlobalHandle"] = 2UL,
            ["SourceSegmentId"] = 2U, ["DestinationSegmentId"] = 2U,
            ["SourceSegmentOffset"] = 16384UL, ["DestinationSegmentOffset"] = 32768UL,
            ["TransferOffset"] = 0UL, ["AllocationOffset"] = 0UL, ["TransferSize"] = 4096UL
        };
        GpuAllocationEventDecoder.Apply(ledger, eventId, key => fields[key], DateTime.UtcNow);
        GpuAllocationEventDecoder.Apply(ledger, 74, _ => 2UL, DateTime.UtcNow);
        Assert.Equal(8192, ledger.Read(Births).Adapters[23][41].TotalBytes);
        fields["SegmentId"] = 2U;
        fields["SegmentOffset"] = 16384UL;
        GpuAllocationEventDecoder.Apply(ledger, 55, key => fields[key], DateTime.UtcNow);
        Assert.Equal(4096, ledger.Read(Births).Adapters[23][41].TotalBytes);
    }

    [Theory]
    [InlineData(53)]
    [InlineData(306)]
    [InlineData(307)]
    public void UnresolvedChunkOffsetCannotBecomeAnotherAdditivePhysicalPlacement(int eventId)
    {
        var ledger = Ledger();
        Assert.Throws<NotSupportedException>(() => GpuAllocationEventDecoder.Apply(ledger, eventId,
            name => name is "TransferOffset" or "AllocationOffset" ? 16384UL : throw new Exception(name), DateTime.UtcNow));
    }

    [Fact]
    public void WholeTransferToSystemThenEvictionRetiresBackingWithoutDiscard()
    {
        var ledger = Ledger();
        ledger.DiscardResident(1, 2, 2);
        ledger.SetAllocation(1, 2, 458752);
        ledger.AddResidentRange(1, 2, 2, 0, 458752, 2056257536);
        GpuAllocationEventDecoder.Apply(ledger, 74, _ => 2UL, DateTime.UtcNow);
        Assert.Equal(458752, ledger.Read(Births).Adapters[23][41].TotalBytes);
        var fields = new Dictionary<string, object>
        {
            ["pDxgAdapter"] = 1UL, ["hAllocationGlobalHandle"] = 2UL,
            ["SourceSegmentId"] = 2U, ["DestinationSegmentId"] = 0U,
            ["SourceSegmentOffset"] = 2056257536UL, ["DestinationSegmentOffset"] = 0UL,
            ["AllocationOffset"] = 0UL, ["TransferSize"] = 458752UL
        };
        GpuAllocationEventDecoder.Apply(ledger, 306, key => fields[key], DateTime.UtcNow);
        Assert.Equal(458752, ledger.Read(Births).Adapters[23][41].TotalBytes);
        GpuAllocationEventDecoder.Apply(ledger, 74, _ => 2UL, DateTime.UtcNow);
        Assert.Equal(0, ledger.Read(Births).Adapters[23][41].TotalBytes);
    }
}
