using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Windows;

namespace Resource_Manager_APP.Tests;

public sealed class WindowsPrimitiveCollectorIntegrationTests
{
    [Fact]
    public void ProductMemoryAndCpuZonesConsumeIndependentReadersAndKeepFreezePolicy()
    {
        Assert.Equal("ResourceManager.ReadWriteCore", typeof(WindowsSystemCounters).Assembly.GetName().Name);
        var memory = new WindowsMemoryMonitoringZone();
        var available = memory.ReadMetrics();
        Assert.True(available.IsUsageAvailable);
        Assert.Equal(SamplingObservationStatus.Current, available.ObservationStatus);
        Assert.True(available.TotalBytes > 0);
        Assert.Equal(available.TotalBytes, checked(available.UsedBytes + available.AvailableBytes!.Value));
        memory.ApplyMode(ResourceManagerComputeZoneMode.Freeze);
        var frozen = memory.ReadMetrics();
        Assert.False(frozen.IsUsageAvailable);
        Assert.Equal(SamplingObservationStatus.NotRequested, frozen.ObservationStatus);
        var cpu = new WindowsCpuMonitoringZone();
        var counters = cpu.ReadCounters();
        Assert.True(counters.IsAvailable);
        Assert.True(counters.KernelTicks >= counters.IdleTicks);
        Assert.True(counters.MonotonicTicksPerSecond > 0);
        cpu.ApplyMode(ResourceManagerComputeZoneMode.Freeze);
        Assert.False(cpu.ReadCounters().IsAvailable);
        memory.ApplyMode(ResourceManagerComputeZoneMode.Normal);
        cpu.ApplyMode(ResourceManagerComputeZoneMode.Normal);
        Assert.True(memory.ReadMetrics().IsUsageAvailable);
        Assert.True(cpu.ReadCounters().IsAvailable);
    }
}
