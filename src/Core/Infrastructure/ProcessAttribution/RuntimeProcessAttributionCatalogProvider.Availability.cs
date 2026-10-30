namespace ResourceManager.App.Infrastructure.ProcessAttribution;

public sealed partial class RuntimeProcessAttributionCatalogProvider
{
    private RuntimeProcessAttributionCatalogAvailability? availability;

    internal bool IsAvailable => Volatile.Read(ref availability)?.IsAvailable ?? true;

    internal event Action? AvailabilityChanged
    {
        add { lock (lifecycleGate) GetAvailability().AvailabilityChanged += value; }
        remove { lock (lifecycleGate) { if (availability is { } current) current.AvailabilityChanged -= value; } }
    }

    public bool ReportFailure(Exception error)
    {
        RuntimeProcessAttributionCatalogAvailability current;
        lock (lifecycleGate)
        {
            if (disposed) return false;
            current = GetAvailability();
        }
        return current.ReportFailure(error);
    }

    // The same resource check is exposed internally so tests need no wall-clock delay.
    internal Task CheckAvailabilityAsync(CancellationToken cancellationToken = default)
    {
        lock (lifecycleGate) return GetAvailability().CheckAvailabilityAsync(cancellationToken);
    }

    // lifecycleGate protects creation, including creation by event subscribers.
    private RuntimeProcessAttributionCatalogAvailability GetAvailability()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return availability ??= new(CheckCatalogResourceAsync,
            availabilityOptions?.Value ?? new(), clock ?? TimeProvider.System);
    }

    private async Task CheckCatalogResourceAsync(CancellationToken cancellationToken)
    {
        // A cached directory alone cannot prove that its backing inventory can be read again.
        await InvalidateSoftwareSnapshotAsync(cancellationToken).ConfigureAwait(false);
        using var lease = await AcquireCatalogAsync(cancellationToken).ConfigureAwait(false);
    }
}
