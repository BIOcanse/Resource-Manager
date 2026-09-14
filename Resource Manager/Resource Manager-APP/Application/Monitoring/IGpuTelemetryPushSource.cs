namespace ResourceManager.App.Application.Monitoring;

public interface IGpuTelemetryPushSource
{
    IAsyncEnumerable<GpuTelemetryWorkerSnapshot> SubscribeAsync(
        string subscriptionId,
        GpuTelemetryWorkerRequest request,
        TimeSpan interval,
        CancellationToken cancellationToken);
}
