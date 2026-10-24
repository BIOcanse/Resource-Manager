namespace ResourceManager.ReadWrite.Windows.Files.Indexing;

public partial class SqliteIndexedFileOwner
{
    private readonly object refreshGateLock = new();
    private readonly Dictionary<string, RefreshGate> refreshGates = new(StringComparer.OrdinalIgnoreCase);

    internal int RefreshGateCount { get { lock (refreshGateLock) return refreshGates.Count; } }

    // A key belongs only to current refreshes and their waiters, not to query history.
    internal async ValueTask<IDisposable> AcquireRefreshAsync(string softwareId, CancellationToken token)
    {
        RefreshGate gate;
        lock (refreshGateLock)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            if (!refreshGates.TryGetValue(softwareId, out gate!))
                refreshGates.Add(softwareId, gate = new());
            gate.Users++;
        }
        try
        {
            await gate.Semaphore.WaitAsync(token);
            return new RefreshLease(this, softwareId, gate);
        }
        catch
        {
            ReleaseRefresh(softwareId, gate);
            throw;
        }
    }

    private void ReleaseRefresh(string softwareId, RefreshGate gate)
    {
        lock (refreshGateLock)
        {
            if (--gate.Users != 0) return;
            refreshGates.Remove(softwareId);
            gate.Semaphore.Dispose();
        }
    }

    private sealed class RefreshGate
    {
        internal readonly SemaphoreSlim Semaphore = new(1, 1);
        internal int Users;
    }

    private sealed class RefreshLease(SqliteIndexedFileOwner owner, string softwareId, RefreshGate gate) : IDisposable
    {
        private RefreshGate? current = gate;
        public void Dispose()
        {
            var ended = Interlocked.Exchange(ref current, null);
            if (ended is null) return;
            ended.Semaphore.Release();
            owner.ReleaseRefresh(softwareId, ended);
        }
    }
}
