using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Domain.Metrics;

namespace Resource_Manager_APP.Tests;

public sealed class AttributionProviderObservationStatusTests
{
    [Theory]
    [InlineData("Active", SamplingObservationStatus.Current)]
    [InlineData("Running", SamplingObservationStatus.Current)]
    [InlineData("Degraded", SamplingObservationStatus.Current)]
    [InlineData("Starting", SamplingObservationStatus.Warming)]
    [InlineData("Idle", SamplingObservationStatus.Warming)]
    [InlineData("Frozen", SamplingObservationStatus.Unavailable)]
    [InlineData("Unavailable", SamplingObservationStatus.Unavailable)]
    [InlineData("Failed", SamplingObservationStatus.Unavailable)]
    [InlineData("NotRequested", SamplingObservationStatus.NotRequested)]
    public void Resolve_MapsProviderStateToFailClosedObservation(
        string state,
        SamplingObservationStatus expected)
    {
        Assert.Equal(expected, AttributionProviderObservationStatus.Resolve(state));
    }
}
