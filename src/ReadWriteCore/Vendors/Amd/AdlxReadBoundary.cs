using System.Runtime.InteropServices;

namespace ResourceManager.ReadWrite.Vendors.Amd;

// The production DLL has one SDK instance. At most one read may outlive its caller;
// it owns managed inputs/results only, never the caller's native workspace or publication permit.
internal sealed class AdlxReadBoundary
{
    private readonly object gate = new();
    private Task? active;
    private bool retired;

    public async Task<T> RunAsync<T>(Func<T> read, T unavailable, TimeSpan maximumDuration,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task<T> operation;
        lock (gate)
        {
            if (retired || active is not null) return unavailable;
            operation = Task.Run(() =>
            {
                lock (gate) if (retired) return unavailable;
                cancellationToken.ThrowIfCancellationRequested();
                return read();
            }, CancellationToken.None);
            active = operation;
            _ = operation.ContinueWith(completed =>
            {
                // Observe a late failure even when its caller has timed out/canceled.
                _ = completed.Exception;
                lock (gate) if (ReferenceEquals(active, completed)) active = null;
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        // The operation's deadline survives cancellation of a particular subscriber.
        var bounded = ObserveAsync(operation, unavailable, maximumDuration);
        _ = bounded.ContinueWith(completed => { _ = completed.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return await bounded.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> ObserveAsync<T>(Task<T> operation, T unavailable, TimeSpan maximumDuration)
    {
        try { return await operation.WaitAsync(maximumDuration).ConfigureAwait(false); }
        catch (TimeoutException) { lock (gate) retired = true; return unavailable; }
        catch (SEHException) { lock (gate) retired = true; return unavailable; }
    }
}
