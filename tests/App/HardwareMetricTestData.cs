using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;

namespace Resource_Manager_APP.Tests;

internal static class HardwareMetricTestData
{
    internal static CpuMetrics EmptyCpu => new("recorded CPU", 0, false,
        CpuMetricObservationStatus.NotRequested, 0, 0, 0, 0, 0, string.Empty,
        new(new(string.Empty, HardwareSensorProviderStates.Unavailable, null), null, null, null, null));
    internal static MemoryMetrics EmptyMemory => new(0, 0, 0, false);
    internal static VirtualMemoryMetrics EmptyVirtualMemory => new(0, 0, 0, false);
    internal static SchedulingGpuInventorySnapshot EmptyGpuInventory =>
        new(SamplingObservationStatus.NotRequested, 0, 0, 0, 0, 0, 0, []);
}
