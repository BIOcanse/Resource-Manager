using ResourceManager.App.Domain.GpuPlacement;

namespace ResourceManager.App.Application.GpuPlacement;

// The original action accepts ownership before Start; Dispose closes local handles, never a live target thread.
// That owner serializes Start, WaitAsync completion, Observe and Dispose, including cancellation and shutdown.
public abstract class GpuRemoteCallExecution : IDisposable
{
    public abstract GpuRemoteCallRequest Request { get; }
    public abstract GpuRemoteCallSnapshot Snapshot { get; }
    public abstract void Start();
    public abstract Task WaitAsync(CancellationToken cancellationToken);
    public abstract GpuRemoteCallSnapshot Observe();
    public abstract void Dispose();
}
