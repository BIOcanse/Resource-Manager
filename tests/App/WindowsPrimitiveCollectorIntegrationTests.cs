using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Windows;

namespace Resource_Manager_APP.Tests;

public sealed class WindowsPrimitiveCollectorIntegrationTests
{
    [Fact]
    public void ProductMemoryAndCpuZonesConsumeIndependentReaders()
    {
        Assert.Equal("ResourceManager.ReadWriteCore", typeof(WindowsSystemCounters).Assembly.GetName().Name);
        var memory = new WindowsMemoryMonitoringZone();
        var available = memory.ReadMetrics();
        Assert.True(available.IsUsageAvailable);
        Assert.Equal(SamplingObservationStatus.Current, available.ObservationStatus);
        Assert.True(available.TotalBytes > 0);
        Assert.Equal(available.TotalBytes, checked(available.UsedBytes + available.AvailableBytes!.Value));
        var cpu = new WindowsCpuMonitoringZone();
        var counters = cpu.ReadCounters();
        Assert.True(counters.IsAvailable);
        Assert.True(counters.KernelTicks >= counters.IdleTicks);
        Assert.True(counters.MonotonicTicksPerSecond > 0);
        Assert.True(memory.ReadMetrics().IsUsageAvailable);
        Assert.True(cpu.ReadCounters().IsAvailable);
    }
}
