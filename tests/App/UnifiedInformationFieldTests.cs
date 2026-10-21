using ResourceManager.ReadWrite;
using ResourceManager.Subscription;

namespace Resource_Manager_APP.Tests;

public sealed class UnifiedInformationFieldTests
{
    [Fact]
    public async Task PublicCpuDirectCaptureSharesOriginalCurrentAndSubscriptionWithoutWorker()
    {
        await using var fixture = new CpuTopologyProviderTestFixture();
        var backend = new InformationReadWriteBackend(fixture.Reader.GetInformationSnapshot,
            fixture.Reader.CaptureInformationAsync, static _ => new(true, null, []));
        fixture.Sampler.Complete(CpuTopologyInformationFieldTests.Snapshot());
        await backend.DiscoverAsync(default); Assert.Equal(1, await fixture.Sampler.WaitForCaptureAsync());
        var field = backend.Catalog.Fields.Single(item => item.Field.FieldName == DeviceInformationFieldRegistry.CpuAllPhysicalCoreCountReported.Name).Field;
        Assert.Equal("2", backend.ReadCached(field).Value?.Integer); Assert.Equal(1, fixture.Sampler.CaptureCount);
        var subscriptions = new InformationFieldSubscriptionBackend(backend,
            (id, interval) => fixture.Reader.AcquireInformationSubscription(id, interval), fixture.Reader.SubscribeInformationAsync);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var stream = subscriptions.SubscribeAsync("public", [field], TimeSpan.FromSeconds(1), cancellation.Token).GetAsyncEnumerator();
        var next = stream.MoveNextAsync().AsTask(); fixture.Sampler.Complete(CpuTopologyInformationFieldTests.Snapshot());
        var read = Assert.Single(await backend.CollectAsync([field], cancellation.Token));
        Assert.True(await next); Assert.Equal(read, Assert.Single(stream.Current));
        Assert.Equal(read, backend.ReadCached(field)); Assert.Equal(2, await fixture.Sampler.WaitForCaptureAsync());
        using var canceled = new CancellationTokenSource();
        var previous = fixture.Reader.GetInformationSnapshot();
        var capture = Task.Run(async () => await backend.CollectAsync([field], canceled.Token));
        Assert.Equal(3, await fixture.Sampler.WaitForCaptureAsync()); canceled.Cancel();
        fixture.Sampler.Complete(CpuTopologyInformationFieldTests.Snapshot());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => capture);
        Assert.Same(previous, fixture.Reader.GetInformationSnapshot());
    }
}
