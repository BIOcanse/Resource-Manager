namespace ResourceManager.App.Infrastructure.Control;

/// <summary>Serializes library, preset, desired, applied-reference and pending-release mutations.</summary>
public sealed class GpuVfCurveMutationGate : IDisposable
{
    public SemaphoreSlim Semaphore { get; } = new(1, 1);
    public void Dispose() => Semaphore.Dispose();
}
