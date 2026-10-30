using Resource_Manager_APP.Tests.Oracles.KernelEtw;
namespace Resource_Manager_APP.Tests;

public sealed partial class OracleNetworkAttributionWindowTests
{
    [Fact]
    public async Task ReadWithoutHostedStartDoesNotAcquireALeaseAndRepeatedCleanupIsIdempotent()
    {
        var broker = new Broker();
        using var reader = ObservationReader(broker, () => 0, pid => new ProcessLease(pid, 100), start: false);
        var snapshot = reader.Read(includeObservedTraffic: true);
        Assert.Null(snapshot.ObservedTraffic);
        Assert.Empty(snapshot.RawProcessValues);
        Assert.Null(reader.ExecuteTask);
        Assert.Equal(0, broker.SubscribeCount);
        await reader.StopAsync(CancellationToken.None);
        await reader.StopAsync(CancellationToken.None);
        reader.Dispose();
        Assert.Equal(0, broker.DisposeCount);
        Assert.Equal(0, broker.SubscribeCount);
    }

    [Fact]
    public async Task CancelledHostedStartDoesNotAcquireALease()
    {
        var broker = new Broker();
        using var reader = ObservationReader(broker, () => 0, pid => new ProcessLease(pid, 100), start: false);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await reader.StartAsync(canceled.Token);
        Assert.NotNull(reader.ExecuteTask);
        try { await reader.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (OperationCanceledException) when (canceled.IsCancellationRequested) { }
        await reader.StopAsync(CancellationToken.None);
        reader.Dispose();
        Assert.Equal(0, broker.SubscribeCount);
        Assert.Equal(0, broker.DisposeCount);
    }
}
