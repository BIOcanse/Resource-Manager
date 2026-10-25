using ResourceManager.App.Application.Management;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using static Resource_Manager_APP.Tests.MetricSnapshotOwnerTestFixture;

namespace Resource_Manager_APP.Tests;

public sealed partial class MetricCatalogueSubscriptionTests
{
    [Fact]
    public async Task OneSecondCatalogSubscriptionPublishesThirtyIntervalsWithoutRecapturingTopology()
    {
        using var fixture = new Fixture(Topology([]));
        using var sampler = Sampler(fixture.Slots, fixture.Catalog, fixture.Owner);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(50));
        Assert.Equal(1, fixture.TopologyReads);
        await sampler.StartAsync(timeout.Token);
        try
        {
            await using var stream = sampler.SubscribeAsync("catalog-cadence", MetricSampleRequest.CatalogProbe,
                TimeSpan.FromMilliseconds(1000), timeout.Token).GetAsyncEnumerator(timeout.Token);
            Assert.True(await stream.MoveNextAsync());
            var revision = fixture.Catalog.Revision;
            var firstObservation = stream.Current.Datasets[SamplingDatasetIds.SystemMetricCatalog];
            for (var interval = 0; interval < 30; interval++)
            {
                Assert.True(await stream.MoveNextAsync());
                Assert.Equal(SamplingObservationStatus.Current,
                    stream.Current.Datasets[SamplingDatasetIds.SystemMetricCatalog].Status);
                Assert.Equal(revision, fixture.Catalog.Revision);
            }
            Assert.Equal(firstObservation,
                stream.Current.Datasets[SamplingDatasetIds.SystemMetricCatalog]);
            Assert.Equal(1, fixture.TopologyReads);
        }
        finally { await sampler.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task ComponentRevisionCapturesOnceAndPublishesChangedCatalogRevision()
    {
        var revisions = new ManagementInventoryRevisionOwner();
        using var fixture = new Fixture(Topology([]), readComponentRevision: () => revisions.Current.ComponentRevision);
        using var sampler = Sampler(fixture.Slots, fixture.Catalog, fixture.Owner);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Assert.Equal(1, fixture.TopologyReads);
        await sampler.StartAsync(timeout.Token);
        try
        {
            await using var stream = sampler.SubscribeAsync("component-catalog", MetricSampleRequest.CatalogProbe,
                TimeSpan.FromMilliseconds(100), timeout.Token).GetAsyncEnumerator(timeout.Token);
            Assert.True(await stream.MoveNextAsync());
            var originalRevision = fixture.Catalog.Revision;
            Assert.Equal(1, fixture.TopologyReads);
            fixture.Topology = Topology([new(7, "component-disk", 2) { SupportsNvmeHealth = true }]);
            revisions.ObserveComponentContentChanged();
            Assert.True(await stream.MoveNextAsync());
            Assert.Equal(2, fixture.TopologyReads);
            Assert.NotEqual(originalRevision, fixture.Catalog.Revision);
            Assert.Contains(fixture.Catalog.Current!.FieldBindings.Values,
                binding => binding.DeviceIdentity == "component-disk");
            var changedRevision = fixture.Catalog.Revision;
            for (var interval = 0; interval < 30; interval++)
                Assert.True(await stream.MoveNextAsync());
            Assert.Equal(2, fixture.TopologyReads);
            Assert.Equal(changedRevision, fixture.Catalog.Revision);

            // Unrelated inventory revisions must not rediscover hardware.
            revisions.ObserveSoftwareInventoryContentChanged();
            Assert.True(await stream.MoveNextAsync());
            Assert.Equal(2, fixture.TopologyReads);
        }
        finally { await sampler.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task ThirtyOneExplicitCatalogCapturesRetainStructureProbeSemantics()
    {
        using var fixture = new Fixture(Topology([]));
        using var sampler = Sampler(fixture.Slots, fixture.Catalog, fixture.Owner);
        fixture.Topology = Topology([new(7, "explicit-disk", 2)]);
        for (var probe = 0; probe < 31; probe++)
            await sampler.CaptureSnapshotAsync(MetricSampleRequest.CatalogProbe, CancellationToken.None);
        Assert.Equal(32, fixture.TopologyReads);
        Assert.Contains(fixture.Catalog.Current!.FieldBindings.Values,
            binding => binding.DeviceIdentity == "explicit-disk");
    }

    [Fact]
    public void FailedComponentRevisionCaptureWaitsForNewRevisionBeforeRetrying()
    {
        var revisions = new ManagementInventoryRevisionOwner();
        using var fixture = new Fixture(Topology([]), readComponentRevision: () => revisions.Current.ComponentRevision);
        fixture.TopologyFailure = new IOException("component unavailable");
        revisions.ObserveComponentContentChanged();
        Assert.NotNull(fixture.Owner.RefreshTopologyIfRequested());
        Assert.Equal(2, fixture.TopologyReads);
        for (var interval = 0; interval < 30; interval++)
            Assert.Null(fixture.Owner.RefreshTopologyIfRequested());
        Assert.Equal(2, fixture.TopologyReads);
        fixture.TopologyFailure = null;
        revisions.ObserveComponentContentChanged();
        Assert.NotNull(fixture.Owner.RefreshTopologyIfRequested());
        Assert.Equal(3, fixture.TopologyReads);
    }
}
