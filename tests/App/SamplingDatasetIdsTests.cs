using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Infrastructure.ResourceBreakdown;

namespace Resource_Manager_APP.Tests;

public sealed class SamplingDatasetIdsTests
{
    [Fact]
    public void AggregateAndPerProcessCpuAreDistinctLogicalDatasets()
    {
        Assert.Equal(
            SamplingDatasetIds.SystemCpuUsage,
            SamplingDatasetIds.ForSystemMetric("cpu.usage"));
        Assert.Equal(
            SamplingDatasetIds.ProcessCpuUsage,
            SamplingDatasetIds.ForProcessMetric("cpu.usage"));
        Assert.NotEqual(
            SamplingDatasetIds.SystemCpuUsage,
            SamplingDatasetIds.ProcessCpuUsage);
    }

    [Fact]
    public void EveryDisplayedGpuMetricHasAnIndependentSubscriptionIdentity()
    {
        Assert.Equal(
            "gpu.0.usage",
            SamplingDatasetIds.ForSystemMetric("gpu.0.usage"));
        Assert.Equal(
            "gpu.7.usage",
            SamplingDatasetIds.ForSystemMetric("gpu.7.usage"));
        Assert.Equal(
            SamplingDatasetIds.ProcessGpuVram,
            SamplingDatasetIds.ForProcessMetric("gpu.0.vram"));
        Assert.Equal(
            SamplingDatasetIds.ProcessGpuVram,
            SamplingDatasetIds.ForProcessMetric("gpu.7.vram"));
        Assert.Equal(
            "gpu.7.temperature",
            SamplingDatasetIds.ForSystemMetric("gpu.7.temperature"));
        Assert.NotEqual(
            SamplingDatasetIds.ForSystemMetric("gpu.0.usage"),
            SamplingDatasetIds.ForSystemMetric("gpu.7.usage"));
    }

    [Fact]
    public void VirtualMemoryHasOneCanonicalRuntimeDataset()
    {
        Assert.Equal(
            SamplingDatasetIds.SystemVirtualMemoryUsage,
            SamplingDatasetIds.ForSystemMetric("virtualMemory.usage"));
        Assert.Equal(
            "virtualMemory.usage",
            SamplingDatasetIds.SystemVirtualMemoryUsage);
    }

    [Fact]
    public void SchedulingGpuMetricsUseStableAggregateDatasets()
    {
        var datasets = SamplingDatasetIds.ForSchedulingMetrics(
            SchedulingProcessMetricMask.GpuUsage
            | SchedulingProcessMetricMask.GpuDedicatedMemory);

        Assert.Equal(
            [
                SamplingDatasetIds.ProcessGpuUsage,
                SamplingDatasetIds.ProcessGpuVram
            ],
            datasets);
        Assert.Equal(
            SchedulingProcessMetricMask.GpuUsage,
            SamplingDatasetIds.ToSchedulingMetric(
                SamplingDatasetIds.ProcessGpuUsage));
        Assert.Equal(
            SchedulingProcessMetricMask.GpuDedicatedMemory,
            SamplingDatasetIds.ToSchedulingMetric(
                SamplingDatasetIds.ProcessGpuVram));
    }

    [Fact]
    public void AllRequestExpandsToRealDatasetsWithoutPseudoMembership()
    {
        var datasets = SamplingDatasetIds.ResolveAllSystemDatasets(
        [
            "cpu.usage",
            "gpu.0.temperature"
        ]);

        Assert.Contains(SamplingDatasetIds.SystemCpuUsage, datasets);
        Assert.Contains(SamplingDatasetIds.SystemGpuInventory, datasets);
        Assert.Contains("gpu.0.temperature", datasets);
        Assert.DoesNotContain("gpu.0.usage", datasets);
        Assert.DoesNotContain("gpu.0.vram", datasets);
        Assert.DoesNotContain("system.all", datasets);
        Assert.DoesNotContain("system.gpu.all.core", datasets);
    }

    [Fact]
    public void ExplicitAllGpuCoreRequestResolvesIndependentGpuDatasets()
    {
        var datasets = SamplingDatasetIds.ResolveFailedSystemDatasets(
            MetricSampleRequest.ForIdsAndAllGpuCoreMetrics([]),
            [
                "gpu.0.usage",
                "gpu.0.vram",
                "gpu.0.temperature"
            ]);

        Assert.Equal(3, datasets.Count);
        Assert.Contains(SamplingDatasetIds.SystemGpuInventory, datasets);
        Assert.Contains("gpu.0.usage", datasets);
        Assert.Contains("gpu.0.vram", datasets);
        Assert.DoesNotContain("gpu.0.temperature", datasets);
    }

    [Fact]
    public void MemoryFieldsDoNotShareOnePublicationIdentity()
    {
        Assert.Equal(
            "memory.usage",
            SamplingDatasetIds.ForSystemMetric("memory.usage"));
        Assert.Equal(
            "memory.percent",
            SamplingDatasetIds.ForSystemMetric("memory.percent"));
        Assert.NotEqual(
            SamplingDatasetIds.ForSystemMetric("memory.usage"),
            SamplingDatasetIds.ForSystemMetric("memory.percent"));
    }

    [Fact]
    public void ProcessMetricSubscriptionsIncludeIndependentFoundations()
    {
        var scheduling = WindowsResourceBreakdownSampler
            .GetSubscriptionDatasetIds(new ResourceBreakdownSampleRequest(
                [],
                new Dictionary<string, string>(),
                ProcessSampleDetailLevel.SmartSchedulingLite,
                SchedulingProcessMetricMask.CpuUsage))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var resourceTable = WindowsResourceBreakdownSampler
            .GetSubscriptionDatasetIds(
                ResourceBreakdownSampleRequest.ForResourceTable(
                    [ResourceBreakdownMetricIds.CpuUsage],
                    new Dictionary<string, string>()))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            SamplingDatasetIds.ProcessInventory,
            SamplingDatasetIds.ProcessAttribution,
            SamplingDatasetIds.ProcessCpuUsage
        };
        Assert.True(expected.SetEquals(scheduling));
        Assert.True(expected.SetEquals(resourceTable));
    }

    [Fact]
    public void HostedMetricCaptureDoesNotRepublishAttributionAsAnImplicitDependency()
    {
        var cpuDue = new ResourceBreakdownSampleRequest(
            [],
            new Dictionary<string, string>(),
            ProcessSampleDetailLevel.SmartSchedulingLite,
            SchedulingProcessMetricMask.CpuUsage)
        {
            PublicationDatasetIds = [SamplingDatasetIds.ProcessCpuUsage]
        };
        var attributionDue = cpuDue with
        {
            SchedulingMetricMask = SchedulingProcessMetricMask.None,
            PublicationDatasetIds = [SamplingDatasetIds.ProcessAttribution]
        };
        var resourceTableCpu = ResourceBreakdownSampleRequest.ForResourceTable(
            [ResourceBreakdownMetricIds.CpuUsage],
            new Dictionary<string, string>());

        Assert.False(WindowsResourceBreakdownSampler
            .RequiresProcessAttribution(cpuDue));
        Assert.True(WindowsResourceBreakdownSampler
            .RequiresProcessAttribution(attributionDue));
        Assert.True(WindowsResourceBreakdownSampler
            .RequiresProcessAttribution(resourceTableCpu));
    }
}
