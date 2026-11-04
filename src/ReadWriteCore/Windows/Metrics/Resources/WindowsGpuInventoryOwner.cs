using ResourceManager.App.Domain.Metrics;
using ResourceManager.ReadWrite.Windows.Gpu;
namespace ResourceManager.ReadWrite.Windows.Metrics.Resources;
public sealed class WindowsGpuInventoryOwner
{
    private readonly object currentGate = new();
    private readonly Func<WindowsGpuAdapterInventoryRead> read;
    private WindowsGpuAdapterInventoryRead current = EmptyCurrent();
    private TaskCompletionSource catalogChanged = CreateCatalogSignal();

    public WindowsGpuInventoryOwner()
        : this(new WindowsGpuAdapterOrderReader().ReadInventory)
    {
    }

    public WindowsGpuInventoryOwner(Func<WindowsGpuAdapterInventoryRead> read)
    {
        this.read = read ?? throw new ArgumentNullException(nameof(read));
    }

    public WindowsGpuAdapterInventoryRead ReadInventory(bool requested)
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

        var result = read();
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

    public WindowsGpuAdapterInventoryRead ReadCurrentInventory()
    {
        lock (currentGate)
        {
            return current;
        }
    }

    public Task WaitForInventoryChangeAsync(CancellationToken cancellationToken)
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

