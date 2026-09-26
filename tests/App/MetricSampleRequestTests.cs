using ResourceManager.App.Domain.Metrics;

namespace Resource_Manager_APP.Tests;

public sealed class MetricSampleRequestTests
{
    [Fact]
    public void GpuWildcardWithoutExplicitIdsIsStillARequest()
    {
        var gpu = MetricSampleRequest.ForIdsAndAllGpuCoreMetrics([]);
        Assert.False(gpu.IsEmpty);
        Assert.Empty(gpu.Ids);
        Assert.True(gpu.Includes("gpu.0.usage"));
        Assert.True(gpu.Includes("gpu.1.vram"));
        Assert.False(gpu.Includes("cpu.usage"));
        Assert.False(MetricSampleRequest.Merge([gpu, MetricSampleRequest.ForIds([])]).IsEmpty);
        Assert.True(MetricSampleRequest.ForIds([]).IsEmpty);
        Assert.True(MetricSampleRequest.CatalogProbe.IsEmpty);
    }

    [Fact]
    public void CacheKey_IsStableForSameMetricSet()
    {
        var left = MetricSampleRequest.ForIds(["gpu.1.fanRpm", "cpu.usage"]);
        var right = MetricSampleRequest.ForIds(["cpu.usage", "gpu.1.fanRpm"]);

        Assert.Equal(left.CacheKey, right.CacheKey);
    }

    [Fact]
    public void Merge_CombinesRequestedMetricIds()
    {
        var merged = MetricSampleRequest.Merge([
            MetricSampleRequest.ForIds(["cpu.usage", "cpu.fanRpm"]),
            MetricSampleRequest.ForIds(["gpu.1.fanRpm"])
        ]);

        Assert.True(merged.Includes("cpu.usage"));
        Assert.True(merged.Includes("cpu.fanRpm"));
        Assert.True(merged.Includes("gpu.1.fanRpm"));
        Assert.False(merged.Includes("gpu.0.fanRpm"));
    }
}
