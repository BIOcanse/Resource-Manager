using ResourceManager.App.Domain.DeviceTopology;

namespace Resource_Manager_APP.Tests;

public sealed class CpuTopologyInformationSubscriptionTests
{
    [Fact]
    public async Task LegacyAndInformationShareOneNativeItemHighestFrequencyAndAtomicCapture()
    {
        await using var fixture = new CpuTopologyProviderTestFixture();
        await using var slow = fixture.Subscribe("same-id", TimeSpan.FromSeconds(5));
        var legacy = slow.Next();
        using var cancellation = new CancellationTokenSource();
        await using var fast = fixture.Reader.SubscribeInformationAsync("same-id", TimeSpan.FromMilliseconds(100),
            cancellation.Token).GetAsyncEnumerator();
        var first = fast.MoveNextAsync().AsTask();
        Assert.Equal((2u, 2u, 1u, 100ul), fixture.ReadDemand());
        Assert.Same(DeviceInformationSnapshot.Empty, fixture.Reader.GetInformationSnapshot());
        Assert.Equal(0, fixture.Sampler.CaptureCount);
        await fixture.Reader.StartAsync(CancellationToken.None);
        Assert.Equal(1, await fixture.Sampler.WaitForCaptureAsync());
        var snapshot = CpuTopologyInformationFieldTests.Snapshot();
        fixture.Sampler.Complete(snapshot);
        Assert.True(await first.WaitAsync(CpuTopologyProviderTestFixture.Deadline));
        Assert.Same(snapshot, await legacy.WaitAsync(CpuTopologyProviderTestFixture.Deadline));
        var information = fast.Current;
        Assert.Same(information, fixture.Reader.GetInformationSnapshot());
        Assert.Equal(snapshot.CapturedAt, information.ObservedAt);
        Assert.Equal(7, information.Devices.Count);

        var next = fast.MoveNextAsync().AsTask();
        var initialResets = fixture.Sampler.ResetCount;
        Assert.Equal(2, await fixture.Sampler.WaitForCaptureAsync());
        Assert.Same(information, fixture.Reader.GetInformationSnapshot());
        Assert.Same(snapshot, fixture.Reader.GetSnapshot());
        Assert.False(next.IsCompleted);
        fixture.Sampler.Complete(snapshot with { CapturedAt = snapshot.CapturedAt.AddSeconds(1) });
        Assert.True(await next.WaitAsync(CpuTopologyProviderTestFixture.Deadline));
        await fast.DisposeAsync();
        Assert.Equal((1u, 1u, 1u, 5_000ul), fixture.ReadDemand());
        Assert.Equal(3, await fixture.Sampler.WaitForCaptureAsync());
        Assert.Equal(initialResets, fixture.Sampler.ResetCount);
        fixture.Sampler.Complete(snapshot);
    }

    [Fact]
    public async Task SamplingFailureClearsKnownValuesAndTheSubscriptionRecovers()
    {
        await using var fixture = new CpuTopologyProviderTestFixture();
        using var cancellation = new CancellationTokenSource();
        await using var subscription = fixture.Reader.SubscribeInformationAsync("fields", TimeSpan.FromMilliseconds(100),
            cancellation.Token).GetAsyncEnumerator();
        var first = subscription.MoveNextAsync().AsTask();
        await fixture.Reader.StartAsync(CancellationToken.None);
        _ = await fixture.Sampler.WaitForCaptureAsync();
        var snapshot = CpuTopologyInformationFieldTests.Snapshot(); fixture.Sampler.Complete(snapshot);
        Assert.True(await first.WaitAsync(CpuTopologyProviderTestFixture.Deadline));
        var declarations = subscription.Current.Devices.Select(static device => device.DeviceIdentity).ToArray();

        var failed = subscription.MoveNextAsync().AsTask();
        var resetsBeforeFailure = fixture.Sampler.ResetCount;
        _ = await fixture.Sampler.WaitForCaptureAsync(); fixture.Sampler.Complete(new InvalidOperationException("native failure"));
        Assert.True(await failed.WaitAsync(CpuTopologyProviderTestFixture.Deadline));
        Assert.Null(fixture.Reader.GetSnapshot()); Assert.Null(subscription.Current.ObservedAt);
        Assert.Equal(declarations, subscription.Current.Devices.Select(static device => device.DeviceIdentity));
        Assert.All(subscription.Current.Devices.SelectMany(static device => device.Fields), field => Assert.Null(field.Value));
        Assert.True(fixture.Sampler.ResetCount > resetsBeforeFailure);

        var recovered = subscription.MoveNextAsync().AsTask();
        _ = await fixture.Sampler.WaitForCaptureAsync(); fixture.Sampler.Complete(snapshot);
        Assert.True(await recovered.WaitAsync(CpuTopologyProviderTestFixture.Deadline));
        Assert.All(subscription.Current.Devices.SelectMany(static device => device.Fields), field => Assert.NotNull(field.Value));
        Assert.Equal(1u, fixture.ReadDemand().SourceCount);
    }

    [Fact]
    public async Task CanceledPausedConsumerCannotStartFutureReadsButStartedReadMayPublish()
    {
        await using var fixture = new CpuTopologyProviderTestFixture();
        using var cancellation = new CancellationTokenSource();
        await using var subscription = fixture.Reader.SubscribeInformationAsync("fields", TimeSpan.FromMilliseconds(100),
            cancellation.Token).GetAsyncEnumerator();
        var first = subscription.MoveNextAsync().AsTask();
        await fixture.Reader.StartAsync(CancellationToken.None);
        _ = await fixture.Sampler.WaitForCaptureAsync();
        var snapshot = CpuTopologyInformationFieldTests.Snapshot(); fixture.Sampler.Complete(snapshot);
        Assert.True(await first.WaitAsync(CpuTopologyProviderTestFixture.Deadline));
        // The enumerator is paused after yielding, so cancellation has not removed the source payload yet.
        Assert.Equal(2, await fixture.Sampler.WaitForCaptureAsync());
        var resetsBeforeCancel = fixture.Sampler.ResetCount;
        await cancellation.CancelAsync();
        var completed = snapshot with { CapturedAt = snapshot.CapturedAt.AddSeconds(1) };
        fixture.Sampler.Complete(completed);
        using var deadline = new CancellationTokenSource(CpuTopologyProviderTestFixture.Deadline);
        while (fixture.Reader.GetInformationSnapshot().ObservedAt != completed.CapturedAt)
            await Task.Delay(10, deadline.Token);
        await Task.Delay(300, deadline.Token);
        Assert.Equal(2, fixture.Sampler.CaptureCount);
        Assert.Equal(1u, fixture.ReadDemand().SourceCount);
        Assert.Same(completed, fixture.Reader.GetSnapshot());
        Assert.True(fixture.Sampler.ResetCount > resetsBeforeCancel);

        // Restart while the canceled source is still paused: its old lease must not keep the time interval alive.
        using var resumedCancellation = new CancellationTokenSource();
        await using var resumed = fixture.Reader.SubscribeInformationAsync("resumed", TimeSpan.FromMilliseconds(100),
            resumedCancellation.Token).GetAsyncEnumerator();
        var restarted = resumed.MoveNextAsync().AsTask();
        Assert.Equal(3, await fixture.Sampler.WaitForCaptureAsync());
        var baseline = snapshot with { OsTopologyInformation = snapshot.OsTopologyInformation! with
            { LogicalProcessorBusyPercent = null } };
        fixture.Sampler.Complete(baseline);
        Assert.True(await restarted.WaitAsync(CpuTopologyProviderTestFixture.Deadline));
        Assert.All(resumed.Current.Devices.SelectMany(static device => device.Fields)
            .Where(field => field.Definition.SourceId == "windows.processor-time"), field => Assert.Null(field.Value));
        Assert.Equal(2u, fixture.ReadDemand().SourceCount);
        await resumedCancellation.CancelAsync();
        await resumed.DisposeAsync();
        await subscription.DisposeAsync();
        Assert.Equal(0u, fixture.ReadDemand().SourceCount);
    }

    [Fact]
    public async Task AlreadyCanceledSubscriptionAddsNoNativeDemand()
    {
        await using var fixture = new CpuTopologyProviderTestFixture();
        await using var subscription = fixture.Reader.SubscribeInformationAsync("fields", TimeSpan.FromMilliseconds(100),
            new CancellationToken(true)).GetAsyncEnumerator();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await subscription.MoveNextAsync());
        Assert.Equal(0u, fixture.ReadDemand().SourceCount); Assert.Equal(0, fixture.Sampler.CaptureCount);
    }
}
