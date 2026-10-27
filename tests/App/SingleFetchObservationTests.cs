using System.Runtime.CompilerServices;
using System.Threading.Channels;
using ResourceManager.App.Application.CpuTopology;
using ResourceManager.App.Application.Monitoring;
using ResourceManager.App.Domain.CpuTopology;

namespace Resource_Manager_APP.Tests;

public sealed class SingleFetchObservationTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task ResidencyRejectsOldCacheAndReleasesAfterFreshDelivery()
    {
        var source = new ResidencySource();
        var pending = source.FetchOnceAsync("residency-once", CancellationToken.None);
        await source.Started.Task.WaitAsync(Timeout);
        source.Values.Writer.TryWrite(CpuCoreResidencyTestValues.Create(1, DateTimeOffset.UnixEpoch));
        Assert.False(pending.IsCompleted);
        var fresh = CpuCoreResidencyTestValues.Create(2, DateTimeOffset.UtcNow);
        source.Values.Writer.TryWrite(fresh);
        Assert.Same(fresh, await pending.WaitAsync(Timeout));
        Assert.Equal("residency-once", source.Holder);
        Assert.Equal(0, source.Active);
    }

    [Fact]
    public async Task ResidencyExplicitUnavailableCompletesAndReleases()
    {
        var source = new ResidencySource();
        var pending = source.FetchOnceAsync("residency-unavailable", CancellationToken.None);
        await source.Started.Task.WaitAsync(Timeout);
        source.Values.Writer.TryWrite(null);
        Assert.Null(await pending.WaitAsync(Timeout));
        Assert.Equal(0, source.Active);
    }

    [Fact]
    public async Task GpuTelemetryRejectsOldCacheAndReturnsFreshFailureWithoutOldAdapters()
    {
        var source = new TelemetrySource();
        var request = GpuTelemetryWorkerRequest.Create(["gpu.0.compute"]);
        var pending = source.FetchOnceAsync("gpu-once", request, CancellationToken.None);
        await source.Started.Task.WaitAsync(Timeout);
        source.Values.Writer.TryWrite(GpuTelemetryWorkerSnapshot.Unavailable("old") with
            { CapturedAt = DateTimeOffset.UnixEpoch });
        Assert.False(pending.IsCompleted);
        var fresh = GpuTelemetryWorkerSnapshot.Unavailable("new-attempt-failed");
        source.Values.Writer.TryWrite(fresh);
        Assert.Same(fresh, await pending.WaitAsync(Timeout));
        Assert.Equal("gpu-once", source.Holder);
        Assert.Same(request, source.Request);
        Assert.Equal(0, source.Active);
    }

    [Fact]
    public async Task GpuTelemetryEmptySelectionEndsWithoutCachedFallback()
    {
        var source = new TelemetrySource();
        Assert.Null(await source.FetchOnceAsync("gpu-empty", GpuTelemetryWorkerRequest.Create(), CancellationToken.None));
        Assert.Empty(source.Request!.CounterIds);
        Assert.Equal(0, source.Active);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationReleasesEitherObservationSubscription(bool gpu)
    {
        var residency = new ResidencySource();
        var telemetry = new TelemetrySource();
        using var cancellation = new CancellationTokenSource();
        Task pending = gpu
            ? telemetry.FetchOnceAsync("gpu-cancel", GpuTelemetryWorkerRequest.Create(["gpu.0.compute"]), cancellation.Token)
            : residency.FetchOnceAsync("residency-cancel", cancellation.Token);
        await (gpu ? telemetry.Started : residency.Started).Task.WaitAsync(Timeout);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(Timeout));
        Assert.Equal(0, telemetry.Active);
        Assert.Equal(0, residency.Active);
    }

    private abstract class PushSource<T>
    {
        internal Channel<T> Values { get; } = Channel.CreateUnbounded<T>();
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Active;
        internal string? Holder;

        protected async IAsyncEnumerable<T> ReadAsync(string holder,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Holder = holder;
            Interlocked.Increment(ref Active);
            Started.TrySetResult();
            try
            {
                await foreach (var value in Values.Reader.ReadAllAsync(cancellationToken)) yield return value;
            }
            finally { Interlocked.Decrement(ref Active); }
        }
    }

    private sealed class ResidencySource : PushSource<CpuCoreResidencySnapshot?>, ICpuCoreResidencyReader
    {
        public CpuCoreResidencySnapshot? Read() => throw new InvalidOperationException("Use the push subscription.");
        public IDisposable AcquireSubscription(string holder, TimeSpan interval) => throw new NotSupportedException();
        public IAsyncEnumerable<CpuCoreResidencySnapshot?> SubscribeAsync(string holder, TimeSpan interval, CancellationToken token)
            => ReadAsync(holder, token);
    }

    private sealed class TelemetrySource : PushSource<GpuTelemetryWorkerSnapshot>, IGpuTelemetryPushSource
    {
        internal GpuTelemetryWorkerRequest? Request;
        public async IAsyncEnumerable<GpuTelemetryWorkerSnapshot> SubscribeAsync(string holder,
            GpuTelemetryWorkerRequest request, TimeSpan interval, [EnumeratorCancellation] CancellationToken token)
        {
            Request = request;
            if (request.CounterIds.Length == 0) yield break;
            await foreach (var value in ReadAsync(holder, token)) yield return value;
        }
    }
}
