using System.Threading.Channels;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Application.Monitoring;
using ResourceManager.App.Infrastructure.Monitoring.GpuTelemetry;
using ResourceManager.App.Infrastructure.Monitoring;

namespace Resource_Manager_APP.Tests;

public sealed class GpuTelemetryWorkerLifecycleTests
{
    [Fact]
    public async Task ActualWorkerPublishesZeroFailureRecoveryAndSettlesCancelledCapture()
    {
        using var fixture = new HostManagerSamplingSubscriptionTestFixture();
        var captures = Channel.CreateUnbounded<TaskCompletionSource<GpuTelemetryWorkerSnapshot>>();
        using var source = new GpuTelemetryWorkerSupervisor(fixture.Owner,
            NullLogger<GpuTelemetryWorkerSupervisor>.Instance, async (request, token) =>
            {
                Assert.Equal(["gpu.0.compute"], request.CounterIds);
                var value = new TaskCompletionSource<GpuTelemetryWorkerSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
                await captures.Writer.WriteAsync(value, token);
                return await value.Task.WaitAsync(token);
            });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var subscription = source.SubscribeAsync("lifecycle",
            GpuTelemetryWorkerRequest.Create(["gpu.0.compute"]), TimeSpan.FromMilliseconds(100), cancellation.Token)
            .GetAsyncEnumerator();
        var next = subscription.MoveNextAsync().AsTask();
        Assert.False(next.IsCompleted);
        Assert.False(captures.Reader.TryRead(out _));
        await source.StartAsync(CancellationToken.None);
        try
        {
            var first = await captures.Reader.ReadAsync(cancellation.Token);
            first.SetResult(Snapshot(0));
            Assert.True(await next);
            Assert.Equal(0, Value(subscription.Current));

            next = subscription.MoveNextAsync().AsTask();
            var failed = await captures.Reader.ReadAsync(cancellation.Token);
            failed.SetException(new InvalidOperationException("recorded read failed"));
            Assert.True(await next);
            Assert.Equal(GpuTelemetryWorkerStatus.Failed, subscription.Current.Status);
            Assert.Null(Value(subscription.Current));

            next = subscription.MoveNextAsync().AsTask();
            var recovered = await captures.Reader.ReadAsync(cancellation.Token);
            recovered.SetResult(Snapshot(25));
            Assert.True(await next);
            Assert.Equal(25, Value(subscription.Current));

            var blocked = await captures.Reader.ReadAsync(cancellation.Token);
            var saved = source.GetLatestSnapshot();
            await source.StopAsync(CancellationToken.None);
            Assert.False(blocked.Task.IsCompleted);
            Assert.Equal(saved.Sequence, source.GetLatestSnapshot().Sequence);
            Assert.Equal(25, Value(source.GetLatestSnapshot()));
            var tracker = (NativeItemSamplingSubscriptionTracker<GpuTelemetryWorkerRequest>)
                typeof(GpuTelemetryWorkerSupervisor).GetField("subscriptions", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(source)!;
            var followingPlan = tracker.CreatePlan(DateTimeOffset.UtcNow);
            if (followingPlan.Request is not null)
                tracker.MarkSkipped(followingPlan.Request, DateTimeOffset.UtcNow);
        }
        finally { await source.StopAsync(CancellationToken.None); }
    }

    private static GpuTelemetryWorkerSnapshot Snapshot(double value) => new(
        GpuTelemetryWorkerSnapshot.CurrentProtocolVersion, 0, DateTimeOffset.UtcNow, 0,
        GpuTelemetryWorkerStatus.Online,
        [new(0, 1, "recorded GPU", "10DE", "recorded",
            [new("gpu.0.compute", "compute", GpuTelemetryCounterClass.HardwareCounter,
                value, "%", "recorded", false, null)])], [], null);

    private static double? Value(GpuTelemetryWorkerSnapshot snapshot)
        => Assert.Single(Assert.Single(snapshot.Adapters).Counters).Value;
}
