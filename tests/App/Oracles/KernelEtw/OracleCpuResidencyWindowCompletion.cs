namespace Resource_Manager_APP.Tests.Oracles.KernelEtw;

/// <summary>One capture's real boundary coverage; this is not a publication or a current value.</summary>
internal sealed class OracleCpuResidencyWindowCompletion(IEnumerable<int> processors, long windowQpc)
{
    private readonly object gate = new();
    private readonly HashSet<int> first = [.. processors];
    private readonly HashSet<int> last = [.. processors];
    private readonly TaskCompletionSource<bool> boundaries = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> complete = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long? through;
    private long latestFirstBoundary;
    internal Task<bool> Boundaries => boundaries.Task;
    internal Task<bool> Complete => complete.Task;
    internal void Observe(int processor, long qpc)
    {
        if (complete.Task.IsCompleted) return;
        lock (gate)
        {
            if (through is null)
            {
                if (!first.Remove(processor)) return;
                latestFirstBoundary = Math.Max(latestFirstBoundary, qpc);
                if (first.Count != 0) return;
                through = checked(latestFirstBoundary + windowQpc);
                boundaries.TrySetResult(true);
            }
            if (qpc >= through && last.Remove(processor) && last.Count == 0) complete.TrySetResult(true);
        }
    }
    internal void Invalidate()
    {
        lock (gate) { boundaries.TrySetResult(false); complete.TrySetResult(false); }
    }
}
