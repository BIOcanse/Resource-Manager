using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;

internal sealed class BusinessBlockDemandLease(BusinessBlockScheduler owner, string key, BusinessBlockReason reason) : IBusinessBlockDependency
{
    private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int releasing;
    public Task Ready { get; internal set; } = Task.CompletedTask;
    public Task Released => released.Task;
    public void Dispose()
    {
        if (Interlocked.Exchange(ref releasing, 1) == 0) _ = ReleaseAsync();
    }
    private async Task ReleaseAsync()
    {
        try
        {
            await owner.SetDemandAsync(key, null, reason).ConfigureAwait(false);
            released.TrySetResult();
        }
        catch (Exception error) { released.TrySetException(error); }
    }
    public ValueTask DisposeAsync() { Dispose(); return new(Released); }
}
