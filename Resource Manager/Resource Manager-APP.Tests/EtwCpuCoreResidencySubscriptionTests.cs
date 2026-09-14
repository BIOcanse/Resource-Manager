using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Infrastructure.CpuTopology;
using ResourceManager.App.Infrastructure.Telemetry.Etw;

namespace Resource_Manager_APP.Tests;

public sealed class EtwCpuCoreResidencySubscriptionTests
{
    [Fact]
    public void ReadAndSubscriptionRegistrationDoNotStartEtw()
    {
        using var sampling = new HostManagerSamplingSubscriptionTestFixture();
        var broker = new UnavailableBroker();
        using var reader = CreateReader(sampling, broker);

        var first = reader.Read();
        using var subscription = reader.AcquireSubscription(
            "test.registration", TimeSpan.FromSeconds(1));

        Assert.Same(first, reader.Read());
        Assert.Equal(0, broker.SubscribeCount);
    }

    [Fact]
    public async Task SubscriptionPublishesEmptyResultOnlyAfterBackgroundAttempt()
    {
        using var sampling = new HostManagerSamplingSubscriptionTestFixture();
        var broker = new UnavailableBroker();
        using var reader = CreateReader(sampling, broker);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var subscription = reader.SubscribeAsync(
            "test.callback", TimeSpan.FromSeconds(1), cancellation.Token)
            .GetAsyncEnumerator();
        var next = subscription.MoveNextAsync().AsTask();
        Assert.False(next.IsCompleted);
        Assert.Equal(0, broker.SubscribeCount);

        await reader.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(await next);
            Assert.True(broker.SubscribeCount > 0);
            Assert.Null(subscription.Current);
            Assert.Same(reader.Read(), subscription.Current);
            await cancellation.CancelAsync();
            Assert.False(await subscription.MoveNextAsync());
        }
        finally
        {
            await reader.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task RemovingTheLastDemandReleasesAnUnavailableKernelLease()
    {
        using var sampling = new HostManagerSamplingSubscriptionTestFixture();
        var broker = new PassiveUnavailableBroker();
        using var reader = new EtwCpuCoreResidencyReader(
            broker,
            sampling.Provider,
            sampling.Owner,
            NullLogger<EtwCpuCoreResidencyReader>.Instance);
        var subscription = reader.AcquireSubscription(
            "test.release-unavailable",
            TimeSpan.FromSeconds(1));
        await reader.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(() => broker.SubscribeCount == 1);
            subscription.Dispose();
            await WaitUntilAsync(() => broker.DisposeCount == 1);

            Assert.Null(reader.Read());
        }
        finally
        {
            subscription.Dispose();
            await reader.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task StopBeforeStartRevokesDemandAndRejectsFutureSubscriptions()
    {
        using var sampling = new HostManagerSamplingSubscriptionTestFixture();
        var broker = new PassiveUnavailableBroker();
        using var reader = new EtwCpuCoreResidencyReader(
            broker,
            sampling.Provider,
            sampling.Owner,
            NullLogger<EtwCpuCoreResidencyReader>.Instance);
        var pending = reader.AcquireSubscription(
            "test.stop-before-start",
            TimeSpan.FromSeconds(1));

        await reader.StopAsync(CancellationToken.None);
        await reader.StartAsync(CancellationToken.None);
        await Task.Delay(50);

        Assert.Equal(0, broker.SubscribeCount);
        Assert.Equal(0, broker.DisposeCount);
        Assert.Null(reader.Read());
        Assert.Throws<InvalidOperationException>(() => reader.AcquireSubscription(
            "test.after-stop",
            TimeSpan.FromSeconds(1)));

        pending.Dispose();
        Assert.Equal(0, broker.SubscribeCount);
        Assert.Equal(0, broker.DisposeCount);
    }

    [Fact]
    public async Task FreezeDoesNotPublishASyntheticCurrentValue()
    {
        using var sampling = new HostManagerSamplingSubscriptionTestFixture();
        var broker = new UnavailableBroker();
        using var reader = CreateReader(sampling, broker);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await using var subscription = reader.SubscribeAsync(
                "test.freeze",
                TimeSpan.FromSeconds(1),
                cancellation.Token)
            .GetAsyncEnumerator();
        var next = subscription.MoveNextAsync().AsTask();

        reader.ApplyMode(ResourceManager.App.Domain.Adaptation.ResourceManagerComputeZoneMode.Freeze);
        await Task.Delay(50);

        Assert.False(next.IsCompleted);
        Assert.Null(reader.Read());
        Assert.Equal(0, broker.SubscribeCount);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => next);
    }

    private static EtwCpuCoreResidencyReader CreateReader(
        HostManagerSamplingSubscriptionTestFixture sampling,
        UnavailableBroker broker)
        => new(broker, sampling.Provider, sampling.Owner,
            NullLogger<EtwCpuCoreResidencyReader>.Instance);

    private sealed class UnavailableBroker : IKernelEtwSessionBroker
    {
        private int subscribeCount;
        public int SubscribeCount => Volatile.Read(ref subscribeCount);

        public IKernelEtwSubscription Subscribe(KernelEtwSubscriptionRequest request)
        {
            Interlocked.Increment(ref subscribeCount);
            throw new InvalidOperationException("Fixture: ETW unavailable.");
        }

        public KernelEtwSessionSnapshot GetSnapshot()
            => new("Unavailable", "Fixture: ETW unavailable.", 0,
                DateTimeOffset.UnixEpoch, KernelTraceEventParser.Keywords.None, 0, 0);
    }

    private sealed class PassiveUnavailableBroker : IKernelEtwSessionBroker
    {
        private int subscribeCount;
        private int disposeCount;
        public int SubscribeCount => Volatile.Read(ref subscribeCount);
        public int DisposeCount => Volatile.Read(ref disposeCount);

        public IKernelEtwSubscription Subscribe(KernelEtwSubscriptionRequest request)
        {
            Interlocked.Increment(ref subscribeCount);
            return new Lease(this);
        }

        public KernelEtwSessionSnapshot GetSnapshot()
            => new(
                "Unavailable",
                "Fixture: processing is unavailable after lease creation.",
                0,
                DateTimeOffset.UnixEpoch,
                KernelTraceEventParser.Keywords.None,
                SubscribeCount - DisposeCount,
                0);

        private sealed class Lease(PassiveUnavailableBroker owner) : IKernelEtwSubscription
        {
            private int disposed;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref disposed, 1) == 0)
                {
                    Interlocked.Increment(ref owner.disposeCount);
                }
            }
        }
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!predicate())
        {
            await Task.Delay(10, timeout.Token);
        }
    }
}
