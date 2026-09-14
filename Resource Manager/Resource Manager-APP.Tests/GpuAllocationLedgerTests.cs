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
        ledger.SetAllocation(1, 2, 8UL * 1024 * 1024 * 1024);
        ledger.Open(1, 2, 11, 41, 200);
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
        ledger.Open(1, 3, 11, 41, 260);
        Assert.Equal(1024, ledger.Read(Births).Adapters[23][41].PrivateBytes);
    }

    [Fact]
    public void PageTableCreationAndRundownOwnerDifferenceNeverChangesSoftwareBytes()
    {
        var ledger = Ledger();
        ledger.SetAllocation(1, 3, 4096, kernelMetadata: true);
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
    public void SchedulingUsesSameSharesAndNeverFallsBackToOldPdhBytes()
    {
        var ledger = Ledger();
        ledger.Open(1, 2, 12, 42, 220);
        var old = SchedulingProcessGpuRead.NotRequested with
        {
            DedicatedMemoryBytes = new Dictionary<(ulong, int, ulong), double> { [(23, 41, 100)] = 999 },
            DedicatedMemoryStatus = SamplingObservationStatus.Current
        };
        ProcessInstanceKey[] processes = [new(41, 100), new(42, 120)];
        var actual = GpuAllocationSchedulingProjection.Apply(old, ledger.Read(Births), processes, 9, 500);
        Assert.Equal(4D * 1024 * 1024 * 1024, actual.DedicatedMemoryBytes[(23, 41, 100)]);
        Assert.Equal(actual.DedicatedMemoryBytes[(23, 41, 100)], actual.AllocationAmounts[(23, 41, 100)].SharedBytes);
        Assert.Equal(9UL, actual.DedicatedMemoryGeneration);
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
        Assert.Equal(fact.AllocatedMemoryBytes, actual.DedicatedMemoryBytes[(23, 41, 100)]);
        var empty = GpuAllocationSchedulingProjection.Apply(old, null, processes, 10, 600);
        Assert.Empty(empty.DedicatedMemoryBytes);
        Assert.Empty(empty.AllocationAmounts);
        Assert.Equal(SamplingObservationStatus.Unavailable, empty.DedicatedMemoryStatus);
    }
}
