using ResourceManager.App.Domain.GpuPlacement;

namespace ResourceManager.App.Application.GpuPlacement;

/// <summary>A short lease on the existing Present session for an already known process instance.</summary>
public interface IPresentGraphicsApiObservationSource
{
    event Action? ObservationChanged { add { } remove { } }
    IPresentGraphicsApiSubscription AcquireGraphicsApiSubscription(GpuPlacementProcessInstance process);
}

public interface IPresentGraphicsApiSubscription : IDisposable
{
    GpuGraphicsApi? Read();
    bool IsComplete => false;
    // The ETW implementation captures the final answer and removes the holder
    // under the same gate, so an event at the deadline cannot become a negative.
    GpuGraphicsApi? Complete() { var result = Read(); Dispose(); return result; }
}
