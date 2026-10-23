using ResourceManager.App.Domain.GpuPlacement;

namespace ResourceManager.App.Application.GpuPlacement;

/// <summary>A short lease on the existing Present session for an already known process instance.</summary>
public interface IPresentGraphicsApiObservationSource
{
    IPresentGraphicsApiSubscription AcquireGraphicsApiSubscription(GpuPlacementProcessInstance process);
}

public interface IPresentGraphicsApiSubscription : IDisposable
{
    GpuGraphicsApi? Read();
}
