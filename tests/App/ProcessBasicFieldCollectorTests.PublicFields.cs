using ResourceManager.App.Infrastructure.ReadWrite;
using ResourceManager.ReadWrite;
using ResourceManager.Subscription;

namespace Resource_Manager_APP.Tests;

public sealed partial class ProcessBasicFieldCollectorTests
{
    [Fact]
    public async Task PublicProcessFieldDirectCaptureSharesCurrentAndOriginalPublication()
    {
        var api = new Api(); using var fixture = Fixture(api);
        var backend = new HostProcessFieldBackend(fixture.Sampler);
        await backend.DiscoverAsync(default);
        var field = Assert.Single(backend.Catalog.Fields, item => item.Field.FieldName == Names[1]).Field;
        Assert.Equal($"windows-process|{Environment.ProcessId}:{Birth}", field.DeviceIdentity);
        Assert.Equal("9007199254740993", backend.ReadCached(field).Value?.Integer);
        Assert.Equal(1, api.Opens); // explicit discovery query, not a cache read
        var before = api.Opens; _ = backend.Catalog; _ = backend.ReadCached(field); Assert.Equal(before, api.Opens);

        await fixture.Sampler.StartAsync(CancellationToken.None);
        try
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var subscriptionCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            api.MemoryValue = api.MemoryValue! with { WorkingSet = 17 };
            var direct = Assert.Single(await backend.CollectAsync([field], cancellation.Token));
            Assert.Equal("17", direct.Value?.Integer);
            Assert.Equal(direct, backend.ReadCached(field));
            Assert.Equal(2, api.Opens); // discovery and explicit direct capture

            await using var stream = backend.SubscribeAsync("public", [field], TimeSpan.FromSeconds(1),
                new(FieldSubscriptionSamplingMode.FreshOnTrigger), subscriptionCancellation.Token)
                .GetAsyncEnumerator(subscriptionCancellation.Token);
            Assert.True(await stream.MoveNextAsync());
            var fresh = Assert.Single(stream.Current);
            Assert.Equal("17", fresh.Value?.Integer);
            Assert.Equal(fresh, backend.ReadCached(field));
            Assert.Equal(3, api.Opens); // one actual source query for the fresh worker trigger
            Assert.Equal("17", Value(fixture.Sampler.ReadLatest(Request(Names[1])), Names[1]).IntegerValue);

            before = api.Opens; _ = backend.Catalog; _ = backend.ReadCached(field); Assert.Equal(before, api.Opens);
            var opensAtCancellation = api.Opens;
            var pending = stream.MoveNextAsync().AsTask();
            subscriptionCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
            await Task.Delay(TimeSpan.FromMilliseconds(1_100));
            Assert.Equal(opensAtCancellation, api.Opens); // the removed final lease no longer queries each period

            api.Fault = "identity-before";
            var failed = await backend.CollectAsync([field], cancellation.Token);
            Assert.Null(Assert.Single(failed).Value); Assert.Null(backend.ReadCached(field).Value);
            Assert.Equal(opensAtCancellation + 1, api.Opens); // only the selected explicit failure query ran
            var wrong = field with { DeviceIdentity = $"windows-process|{Environment.ProcessId}:{Birth + 1}" };
            Assert.Throws<ArgumentException>(() => backend.ReadCached(wrong));
            using var canceled = new CancellationTokenSource(); canceled.Cancel(); before = api.Opens;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => backend.CollectAsync([field], canceled.Token).AsTask());
            Assert.Equal(before, api.Opens);
        }
        finally
        {
            await fixture.Sampler.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public void ProcessSnapshotIndexFollowsWithSelectionAndClear()
    {
        var first = new ProcessMetricFieldSnapshot(DateTimeOffset.UtcNow, null, [], [new(17, "123", [new("field", "0", null)])]);
        Assert.Equal("0", Assert.Single(first.FindProcess(17, "123")!.Values).IntegerValue);
        var replaced = first with { Processes = [new(17, "124", [new("field", "1", null)])] };
        Assert.Null(replaced.FindProcess(17, "123")); Assert.NotNull(replaced.FindProcess(17, "124"));
        Assert.Null(Assert.Single(first.Clear().FindProcess(17, "123")!.Values).IntegerValue);
        Assert.Equal("0", Assert.Single(first.FindProcess(17, "123")!.Values).IntegerValue);
    }
}
