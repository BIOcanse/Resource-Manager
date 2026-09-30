using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.Windows;

namespace Resource_Manager_APP.Tests;

public sealed class WindowsVirtualMemoryMonitoringZoneTests
{
    [Theory]
    [InlineData(0UL, 1024UL, 4096UL, 0UL, 4194304UL, 0d)]
    [InlineData(768UL, 1024UL, 4096UL, 3145728UL, 4194304UL, 75d)]
    public void CommitUsesCurrentSystemPagesRatherThanConfiguredPagefileMaximum(
        ulong committed, ulong limit, ulong pageSize, ulong expectedUsed, ulong expectedLimit, double percent)
    {
        var result = WindowsVirtualMemoryMonitoringZone.CreateCommitMetrics(new PerformanceInformation
        {
            CommitTotal = (nuint)committed,
            CommitLimit = (nuint)limit,
            PageSize = (nuint)pageSize
        }, "动态页面文件，配置最大 48 GB");

        Assert.Equal(expectedUsed, result.UsedBytes);
        Assert.Equal(expectedLimit, result.TotalBytes);
        Assert.Equal(percent, result.UsagePercent);
        Assert.True(result.IsSelectable);
        Assert.Equal(SamplingObservationStatus.Current, result.ObservationStatus);
        Assert.Contains("当前提交上限", result.Detail);
        Assert.Contains("不是页面文件实际占用", result.Detail);
    }

    [Theory]
    [InlineData(1UL, 0UL, 4096UL)]
    [InlineData(1UL, 2UL, 0UL)]
    [InlineData(3UL, 2UL, 4096UL)]
    [InlineData(0UL, ulong.MaxValue, 4096UL)]
    public void InvalidCommitCountersAreUnavailable(ulong committed, ulong limit, ulong pageSize)
    {
        var result = WindowsVirtualMemoryMonitoringZone.CreateCommitMetrics(new PerformanceInformation
        {
            CommitTotal = (nuint)committed,
            CommitLimit = (nuint)limit,
            PageSize = (nuint)pageSize
        }, "test");

        Assert.False(result.IsSelectable);
        Assert.Equal(SamplingObservationStatus.Unavailable, result.ObservationStatus);
        Assert.Equal(0UL, result.TotalBytes);
    }

    [Fact]
    public void WindowsCommitApiProvidesCurrentCounters()
    {
        var result = new WindowsVirtualMemoryMonitoringZone().ReadMetrics();

        Assert.True(result.IsSelectable);
        Assert.Equal(SamplingObservationStatus.Current, result.ObservationStatus);
        Assert.True(result.TotalBytes > 0);
        Assert.InRange(result.UsedBytes, 0UL, result.TotalBytes);
        Assert.InRange(result.UsagePercent, 0d, 100d);
        Assert.Contains("系统提交量", result.Detail);
    }
}
