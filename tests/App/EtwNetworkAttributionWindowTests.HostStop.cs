using System.Net;

namespace Resource_Manager_APP.Tests;

public sealed partial class EtwNetworkAttributionWindowTests
{
    [Fact]
    public async Task StopWithoutHostedStartReleasesReadCreatedLeaseAndRepeatedCleanupIsIdempotent()
    {
        var broker = new Broker();
        using var reader = ObservationReader(broker, () => 0, pid => new ProcessLease(pid, 100));
        reader.Read(includeObservedTraffic: true);
        Transfer(reader, broker, 41, 100, true, false, IPAddress.Loopback, 1000, 100);
        Assert.Null(reader.ExecuteTask); Assert.Equal(1, broker.SubscribeCount);
        await reader.StopAsync(CancellationToken.None);
        await reader.StopAsync(CancellationToken.None);
        reader.Dispose();
        Assert.Equal(1, broker.DisposeCount); Assert.False(broker.Source.IsActive);
    }

    [Fact]
    public async Task StopAfterCancelledBackgroundStartReleasesLeaseWithoutExecutingWorkerBody()
    {
        var broker = new Broker();
        using var reader = ObservationReader(broker, () => 0, pid => new ProcessLease(pid, 100));
        reader.Read(includeObservedTraffic: true);
        Transfer(reader, broker, 41, 100, true, false, IPAddress.Loopback, 1000, 100);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await reader.StartAsync(canceled.Token);
        Assert.NotNull(reader.ExecuteTask);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ExecuteTask!);
        await reader.StopAsync(CancellationToken.None);
        Assert.Equal(1, broker.DisposeCount); Assert.False(broker.Source.IsActive);
        reader.Dispose(); Assert.Equal(1, broker.DisposeCount);
    }
}
