using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Domain.Metrics;

namespace Resource_Manager_APP.Tests;

public sealed class AttributionProviderObservationStatusTests
{
    [Theory]
    [InlineData(NetworkAttributionProviderStates.Active, SamplingObservationStatus.Current)]
    [InlineData(NetworkAttributionProviderStates.Running, SamplingObservationStatus.Current)]
    [InlineData(NetworkAttributionProviderStates.Degraded, SamplingObservationStatus.Current)]
    [InlineData(NetworkAttributionProviderStates.Starting, SamplingObservationStatus.Warming)]
    [InlineData(NetworkAttributionProviderStates.Idle, SamplingObservationStatus.Warming)]
    [InlineData(NetworkAttributionProviderStates.Unavailable, SamplingObservationStatus.Unavailable)]
    [InlineData(NetworkAttributionProviderStates.Failed, SamplingObservationStatus.Unavailable)]
    [InlineData(NetworkAttributionProviderStates.NotRequested, SamplingObservationStatus.NotRequested)]
    public void Resolve_MapsProviderStateToFailClosedObservation(
        string state,
        SamplingObservationStatus expected)
    {
        Assert.Equal(expected, AttributionProviderObservationStatus.Resolve(state));
    }

    [Theory]
    [InlineData(PhysicalDiskIoProviderStates.Running, SamplingObservationStatus.Current)]
    [InlineData(PhysicalDiskIoProviderStates.Starting, SamplingObservationStatus.Warming)]
    [InlineData(PhysicalDiskIoProviderStates.Idle, SamplingObservationStatus.Warming)]
    [InlineData(PhysicalDiskIoProviderStates.Warming, SamplingObservationStatus.Warming)]
    [InlineData(PhysicalDiskIoProviderStates.Unavailable, SamplingObservationStatus.Unavailable)]
    [InlineData(PhysicalDiskIoProviderStates.NotRequested, SamplingObservationStatus.NotRequested)]
    [InlineData("Running", SamplingObservationStatus.Unavailable)]
    public void ResolvePhysicalDiskIo_MapsOwnedWireStateAndRejectsPascalCase(
        string state,
        SamplingObservationStatus expected)
    {
        Assert.Equal(expected, AttributionProviderObservationStatus.Resolve(state));
    }
}
