using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Infrastructure.ResourceBreakdown;

namespace Resource_Manager_APP.Tests;

public sealed class GpuResidentUsageBarTests
{
    [Theory]
    [InlineData(ResourceBreakdownScaleModes.Active)]
    [InlineData(ResourceBreakdownScaleModes.Capacity)]
    public void DeviceReadingIsNeverReplacedByAllocationTotalsOrInventedSharedResidency(string scale)
    {
        var bar = WindowsResourceBreakdownSampler.CreateUnattributedMemoryUsageBar("gpu.1.vram", "GPU1", scale,
            7L << 30, 8L << 30);
        Assert.Equal(7L << 30, bar.TotalValue);
        Assert.Equal(8L << 30, bar.CapacityValue);
        Assert.Equal(87.5, bar.TotalSystemPercent);
        Assert.Equal(SamplingObservationStatus.Current, bar.ObservationStatus);
        Assert.Equal(SamplingObservationStatus.Unavailable, bar.AttributionStatus);
        Assert.Null(bar.SharedValue);
        var unknown = Assert.Single(bar.Software);
        Assert.Equal("unattributed", unknown.SoftwareId);
        Assert.Equal(bar.TotalValue, unknown.Value);
        Assert.Empty(unknown.Processes);
        Assert.Null(unknown.SharedValue);
    }

    [Fact]
    public void MissingDeviceReadingIsNotZeroOrAnAllocationFallback()
    {
        var bar = WindowsResourceBreakdownSampler.CreateUnattributedMemoryUsageBar("gpu.1.vram", "GPU1", "capacity", null, 8L << 30);
        Assert.Null(bar.TotalValue);
        Assert.Null(bar.TotalSystemPercent);
        Assert.Empty(bar.Software);
        Assert.Equal(SamplingObservationStatus.Unavailable, bar.ObservationStatus);
    }

    [Fact]
    public void PageFileOccupancyDoesNotInventPrivateCommitAttribution()
    {
        var bar = WindowsResourceBreakdownSampler.CreateUnattributedMemoryUsageBar(
            "virtualMemory.usage", "Page file", "capacity", 2L << 30, 16L << 30);
        Assert.Equal(12.5, bar.TotalSystemPercent);
        var segment = Assert.Single(bar.Software);
        Assert.Equal("unattributed", segment.SoftwareId);
        Assert.Equal(2L << 30, segment.Value);
        Assert.Empty(segment.Processes);
    }
}
