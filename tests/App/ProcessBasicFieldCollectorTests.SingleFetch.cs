using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.App.Domain.ResourceBreakdown;
using ResourceManager.App.Infrastructure.ResourceBreakdown;
using ResourceManager.Subscription;
using ResourceManager.Subscription.Native;
using NetworkTests = Resource_Manager_APP.Tests.ProcessNetworkFieldCollectorTests;

namespace Resource_Manager_APP.Tests;

public sealed partial class ProcessBasicFieldCollectorTests
{
    [Fact]
    public async Task SingleFetchReleaseDoesNotDrainAnAlreadyAdmittedProcessCapture()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var secondCapture = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishCapture = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var captures = 0;
        var blockNextCapture = 0;
        var opensBeforeBlockedCapture = 0;
        (int, int) admittedSources = default;
        var api = new Api();
        NetworkTests.Fixture? running = null;
        using var fixture = Fixture(api, inventory: () =>
        {
            Interlocked.Increment(ref captures);
            if (Interlocked.CompareExchange(ref blockNextCapture, 2, 1) == 1)
            {
                // The original worker has admitted this round while the single-fetch holder is active.
                admittedSources = ProcessSubscriptions(running!.Sampler).CaptureManagedSourceState();
                opensBeforeBlockedCapture = Volatile.Read(ref api.Opens);
                secondCapture.TrySetResult();
                finishCapture.Task.WaitAsync(deadline.Token).GetAwaiter().GetResult();
            }
            return [Process.GetCurrentProcess()];
        });
        running = fixture;
        await fixture.Sampler.StartAsync(deadline.Token);
        try
        {
            // Model a consumer continuation delayed by load, without sleeping or changing the owner.
            var source = new DelayedProcessDelivery(fixture.Sampler, secondCapture.Task,
                () => Volatile.Write(ref blockNextCapture, 1));
            var fresh = await source.FetchOnceAsync("single-fetch:delayed-process-consumer",
                Request(Names[5]), deadline.Token);
            Assert.Equal(MetricInteger.Parse("9007199254740993"), Value(Assert.IsType<ResourceBreakdownSnapshot>(fresh), Names[5]).IntegerValue);
            Assert.Equal((0, 0), ProcessSubscriptions(fixture.Sampler).CaptureManagedSourceState());
            Assert.Equal((1, 1), admittedSources);
            Assert.True(opensBeforeBlockedCapture > 0);
            Assert.Equal(opensBeforeBlockedCapture, Volatile.Read(ref api.Opens));
            var fetchedOpens = Volatile.Read(ref api.Opens);
            finishCapture.TrySetResult();
            await WaitForProcessDemandDrainedAsync(fixture, deadline.Token);
            // Release prevents new rounds; it does not revoke this already-admitted capture.
            Assert.Equal(fetchedOpens + 1, Volatile.Read(ref api.Opens));
            Assert.Equal(Volatile.Read(ref api.Opens), Volatile.Read(ref api.Closes));
            var drainedOpens = Volatile.Read(ref api.Opens);
            var drainedCaptures = Volatile.Read(ref captures);
            // Wake the same owner again with no demand: it must not start any new capture.
            await WaitForProcessDemandDrainedAsync(fixture, deadline.Token);
            Assert.Equal(drainedOpens, Volatile.Read(ref api.Opens));
            Assert.Equal(drainedCaptures, Volatile.Read(ref captures));
        }
        catch (OperationCanceledException error)
        {
            throw new InvalidOperationException($"Controlled capture did not finish: captures={captures}, opens={api.Opens}, delivery={blockNextCapture}, worker={fixture.Sampler.CaptureSamplingWorkerTask()?.Status}", error);
        }
        finally
        {
            finishCapture.TrySetResult();
            await fixture.Sampler.StopAsync(CancellationToken.None);
        }
    }

    private static ResourceManager.App.Infrastructure.Monitoring.NativeItemSamplingSubscriptionTracker<ResourceBreakdownSampleRequest>
        ProcessSubscriptions(WindowsResourceBreakdownSampler sampler)
        => (ResourceManager.App.Infrastructure.Monitoring.NativeItemSamplingSubscriptionTracker<ResourceBreakdownSampleRequest>)
            typeof(WindowsResourceBreakdownSampler).GetField("subscriptions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(sampler)!;

    private static async Task WaitForProcessDemandDrainedAsync(NetworkTests.Fixture fixture, CancellationToken token)
    {
        var tracker = ProcessSubscriptions(fixture.Sampler);
        while (tracker.CaptureManagedSourceState() != (0, 0)) await Task.Delay(1, token);
        using var lease = fixture.Subscriptions.Owner.Acquire(roleId: 3);
        var session = lease.Session;
        var sources = new NativeSamplingSubscriptionSourceView[session.Capacity.SnapshotSourceCapacity];
        var items = new NativeSamplingSubscriptionItemState[session.Capacity.SnapshotItemCapacity];
        NativeSamplingSubscriptionSnapshotHeader Read()
        {
            var header = new NativeSamplingSubscriptionSnapshotHeader
            {
                AbiVersion = NativeSamplingSubscriptionAbi.Version,
                StructSize = (uint)Marshal.SizeOf<NativeSamplingSubscriptionSnapshotHeader>()
            };
            Assert.Equal(NativeSamplingSubscriptionStatus.Ok, session.Snapshot(ref header, sources, items));
            return header;
        }
        var before = Read();
        var signal = (SemaphoreSlim)typeof(WindowsResourceBreakdownSampler)
            .GetField("demandSignal", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Sampler)!;
        WindowsResourceBreakdownSampler.ReleaseDemandSignal(signal);
        // Only the original worker creates plans. A newer zero-source plan acknowledges both
        // lease removal and settlement of any older capture, unlike a client cancellation or delay.
        var after = Read();
        while (after.LastPlanEpoch <= before.LastPlanEpoch)
        {
            await Task.Delay(1, token);
            after = Read();
        }
        Assert.Equal(0U, after.ActiveSourceCount);
        Assert.Equal(0U, after.MembershipCount);
    }

    private sealed class DelayedProcessDelivery(IResourceBreakdownPushSource source, Task nextCapture, Action delivered)
        : IResourceBreakdownPushSource
    {
        public async IAsyncEnumerable<ResourceBreakdownSnapshot> SubscribeAsync(string holder,
            ResourceBreakdownSampleRequest request, TimeSpan interval,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var snapshot in source.SubscribeAsync(holder, request, interval, cancellationToken))
            {
                delivered();
                await nextCapture.WaitAsync(cancellationToken);
                yield return snapshot;
            }
        }
    }
}
