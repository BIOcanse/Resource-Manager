using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Domain.Metrics;

namespace Resource_Manager_APP.Tests;

public sealed class GpuSchedulingAvailabilityReaderTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, true)]
    public async Task LaunchInterceptionAvailabilityDependsOnlyOnGpuCount(
        int gpuCount,
        bool expectedEnabled)
    {
        var sampler = new CatalogSampler(new HardwareMetricSnapshot(
            DateTimeOffset.UtcNow,
            null!,
            null!,
            null!,
            Enumerable.Range(0, gpuCount)
                .Select(index => new GpuMetrics(index, $"GPU {index}", 0, 0, 0, 0, 0, 0, 0, 0, null!))
                .ToArray(),
            null!,
            new Dictionary<string, MetricValue>()));

        var availability = await new GpuSchedulingAvailabilityReader(sampler)
            .EvaluateAsync(CancellationToken.None);

        Assert.Equal(expectedEnabled, availability.Enabled);
        Assert.Equal(1, sampler.ProbeCount);
        Assert.Equal(expectedEnabled, availability.Reason is null);
    }

    private sealed class CatalogSampler(HardwareMetricSnapshot snapshot) : IMetricSampler
    {
        public int ProbeCount { get; private set; }

        public Task<HardwareMetricSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<HardwareMetricSnapshot> GetSnapshotAsync(
            MetricSampleRequest request,
            CancellationToken cancellationToken)
        {
            Assert.True(request.IsCatalogProbe);
            ProbeCount++;
            return Task.FromResult(snapshot);
        }

        public Task<HardwareMetricSnapshot> CaptureSnapshotAsync(
            MetricSampleRequest request,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
