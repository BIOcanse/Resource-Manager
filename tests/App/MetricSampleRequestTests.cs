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
        Assert.False(MetricSampleRequest.CatalogProbe.IsEmpty);
    }

    [Fact]
    public void CatalogueDemandDoesNotExpandNumericDemandWhenMerged()
    {
        var catalogue = MetricSampleRequest.CatalogProbe;
        Assert.True(catalogue.IncludesCatalog);
        Assert.False(catalogue.Includes("cpu.usage"));
        Assert.False(catalogue.IncludesPrefix("gpu."));
        Assert.False(catalogue.IncludesAllGpuCoreMetrics);
        var cpu = MetricSampleRequest.ForIds(["cpu.usage"]);
        var merged = MetricSampleRequest.Merge([catalogue, cpu]);
        Assert.True(merged.IncludesCatalog);
        Assert.False(merged.IsCatalogProbe);
        Assert.False(merged.IsAll);
        Assert.True(merged.Includes("cpu.usage"));
        Assert.False(merged.Includes("gpu.0.usage"));
        Assert.NotEqual(cpu.CacheKey, merged.CacheKey);
        Assert.True(MetricSampleRequest.Merge([]).IsEmpty);
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
