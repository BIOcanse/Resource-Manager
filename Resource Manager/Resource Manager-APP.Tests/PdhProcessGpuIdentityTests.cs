using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.ResourceBreakdown;

namespace Resource_Manager_APP.Tests;

public sealed class PdhProcessGpuIdentityTests
{
    [Fact]
    public void SchedulingReadBindsRowsOnlyToTheValidatedProcessInstance()
    {
        const ulong adapterKey = 23;
        var source = new FixedSnapshotSource(new NativePdhSnapshot(
            DateTimeOffset.UtcNow,
            7,
            17,
            [
                new NativePdhGpuEngineRow
                {
                    AdapterLuid = adapterKey,
                    ProcessId = 42,
                    EngineClass = NativePdhEngineClass.Compute,
                    UsagePercent = 25
                }
            ],
            [
                new NativePdhGpuMemoryRow
                {
                    AdapterLuid = adapterKey,
                    ProcessId = 42,
                    DedicatedBytes = 4096
                }
            ],
            default));
        var expected = new ProcessInstanceKey(42, 100);
        var inventory = CreateInventory(adapterKey);

        var matchingReader = new PdhProcessGpuReader(
            source,
            _ => new HashSet<ProcessInstanceKey> { expected });
        var matching = matchingReader.ReadSchedulingSnapshot(inventory, [expected]);

        Assert.Equal(25, matching.UsagePercent[(adapterKey, 42, 100)]);
        Assert.Equal(4096, matching.DedicatedMemoryBytes[(adapterKey, 42, 100)]);

        var reusedPidReader = new PdhProcessGpuReader(
            source,
            _ => new HashSet<ProcessInstanceKey> { new(42, 200) });
        var reusedPid = reusedPidReader.ReadSchedulingSnapshot(inventory, [expected]);

        Assert.Empty(reusedPid.UsagePercent);
        Assert.Empty(reusedPid.DedicatedMemoryBytes);
    }

    private static SchedulingGpuInventorySnapshot CreateInventory(ulong adapterKey)
    {
        const ulong generation = 9;
        var observedAt = DateTimeOffset.UtcNow.UtcTicks;
        return new SchedulingGpuInventorySnapshot(
            SamplingObservationStatus.Current,
            generation,
            observedAt,
            1,
            0,
            0,
            17,
            [
                new SchedulingGpuAdapterObservation(
                    0,
                    adapterKey,
                    SchedulingGpuCapabilityMask.Usage | SchedulingGpuCapabilityMask.DedicatedMemory,
                    SchedulingGpuMetricMask.Usage |
                        SchedulingGpuMetricMask.UsedDedicatedMemory |
                        SchedulingGpuMetricMask.TotalDedicatedMemory,
                    SamplingObservationStatus.Current,
                    SamplingObservationStatus.Current,
                    0,
                    0,
                    8192,
                    generation,
                    observedAt)
            ]);
    }

    private sealed class FixedSnapshotSource(NativePdhSnapshot snapshot)
        : INativePdhSnapshotSource
    {
        public NativePdhReadResult Read(ulong? adapterTopologyFingerprint = null)
            => new(
                snapshot,
                NativePdhProviderAvailability.Available,
                NativePdhResultCode.Ok,
                0);
    }
}
