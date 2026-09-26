using ResourceManager.App.Application.Monitoring;

using ResourceManager.App.Domain.Metrics;

namespace ResourceManager.App.Infrastructure.Monitoring;

public sealed class WindowsGpuAdapterOrderMonitoringZone : MonitoringSourceZone
{
    private readonly object currentGate = new();
    private readonly WindowsGpuAdapterOrderReader reader = new();
    private WindowsGpuAdapterInventoryRead current = EmptyCurrent();
    private TaskCompletionSource catalogChanged = CreateCatalogSignal();

    public WindowsGpuAdapterOrderMonitoringZone()
        : base(MonitoringSourceZoneIds.WindowsGpuAdapterOrder)
    {
    }

    internal WindowsGpuAdapterInventoryRead ReadInventory(bool requested)
    {
        if (!requested)
        {
            return new WindowsGpuAdapterInventoryRead(
                SamplingObservationStatus.NotRequested,
                0,
                0,
                0,
                0,
                0,
                0,
                []);
        }

        var result = CanRead
            ? reader.ReadInventory()
            : new WindowsGpuAdapterInventoryRead(
                SamplingObservationStatus.Unavailable,
                0,
                DateTimeOffset.UtcNow.UtcTicks,
                0,
                0,
                0,
                0,
                []);
        TaskCompletionSource completed;
        lock (currentGate)
        {
            current = result;
            completed = catalogChanged;
            catalogChanged = CreateCatalogSignal();
        }
        completed.TrySetResult();
        return result;
    }

    internal WindowsGpuAdapterInventoryRead ReadCurrentInventory()
    {
        lock (currentGate)
        {
            return current;
        }
    }

    internal Task WaitForInventoryChangeAsync(CancellationToken cancellationToken)
    {
        lock (currentGate)
        {
            return catalogChanged.Task.WaitAsync(cancellationToken);
        }
    }

    private static TaskCompletionSource CreateCatalogSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static WindowsGpuAdapterInventoryRead EmptyCurrent() =>
        new(
            SamplingObservationStatus.Unavailable,
            0,
            0,
            0,
            0,
            0,
            0,
            []);
}
