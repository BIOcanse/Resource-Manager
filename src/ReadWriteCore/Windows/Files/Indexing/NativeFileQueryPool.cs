using ResourceManager.ReadWrite.Native;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace ResourceManager.App.Infrastructure.NativeCore;

public interface INativeFileQueryLeaseSource
{
    ValueTask<NativeFileQueryLease> AcquireAsync(CancellationToken cancellationToken);

    ulong NextEpoch();
}

public sealed class NativeFileQueryLease : IDisposable
{
    private Action<NativeFileQuerySession, bool>? release;
    private bool reusable;
    private readonly object releaseGate = new();

    internal NativeFileQueryLease(
        NativeFileQuerySession session,
        Action<NativeFileQuerySession, bool> release)
    {
        Session = session;
        this.release = release;
    }

    internal NativeFileQuerySession Session { get; }

    internal void MarkReusable() => reusable = true;

    public void Dispose()
    {
        lock (releaseGate)
        {
            var callback = release;
            release = null;
            // The callback transfers ownership back to the pool even if cleanup
            // fails. A later Dispose must not return a session borrowed again.
            callback?.Invoke(Session, reusable);
        }
    }
}

public sealed class NativeFileQueryPool : INativeFileQueryLeaseSource, IDisposable
{
    private readonly ConcurrentQueue<NativeFileQuerySession> available = new();
    private readonly SemaphoreSlim signal;
    private readonly object lifecycleGate = new();
    private long nextEpoch;
    private int disposed;

    public NativeFileQueryPool(
        in NativeFileQueryConfiguration configuration,
        int sessionCount,
        ulong totalResidentByteBudget)
        : this(in configuration, sessionCount, totalResidentByteBudget, NativeFileQueryScratch.CreateOrdinary) { }

    internal NativeFileQueryPool(in NativeFileQueryConfiguration configuration, int sessionCount,
        ulong totalResidentByteBudget, Func<ulong, uint, INativeFileQueryScratch> createScratch)
    {
        if (sessionCount <= 0 || totalResidentByteBudget == 0)
        {
            throw new InvalidOperationException(
                "The native file-query workspace shape is not published.");
        }

        signal = new SemaphoreSlim(0, sessionCount);
        var sessions = new List<NativeFileQuerySession>(sessionCount);
        try
        {
            ulong resident = 0;
            for (var index = 0; index < sessionCount; index++)
            {
                var session = new NativeFileQuerySession(in configuration, createScratch);
                sessions.Add(session);
                resident = checked(resident + session.Capacity.ResidentByteCount);
            }
            if (resident > totalResidentByteBudget)
            {
                throw new InvalidOperationException(
                    "The native file-query session pool exceeds its explicit total resident budget.");
            }
            foreach (var session in sessions)
            {
                available.Enqueue(session);
                signal.Release();
            }
        }
        catch
        {
            foreach (var session in sessions)
            {
                session.Dispose();
            }
            signal.Dispose();
            throw;
        }
    }

    public async ValueTask<NativeFileQueryLease> AcquireAsync(
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        await signal.WaitAsync(cancellationToken).ConfigureAwait(false);
        lock (lifecycleGate)
        {
            if (Volatile.Read(ref disposed) != 0 || !available.TryDequeue(out var session))
                throw new ObjectDisposedException(nameof(NativeFileQueryPool));
            try
            {
                // A failed prior acquisition may still own a use whose return
                // failed. Retry that explicit return before borrowing again.
                session.EndQuery(false);
                session.BeginQuery();
            }
            catch { available.Enqueue(session); signal.Release(); throw; }
            return new NativeFileQueryLease(session, Release);
        }
    }

    public ulong NextEpoch()
    {
        var value = Interlocked.Increment(ref nextEpoch);
        return value > 0
            ? checked((ulong)value)
            : throw new InvalidOperationException(
                "The native file-query operation epoch is exhausted.");
    }

    public void Dispose()
    {
        lock (lifecycleGate)
        {
            if (disposed == 2) return;
            Volatile.Write(ref disposed, 1);
            // Leave a failed close owned by the queue for the next Dispose.
            while (available.TryPeek(out var session))
            {
                session.Dispose();
                available.TryDequeue(out _);
            }
            signal.Dispose();
            Volatile.Write(ref disposed, 2);
        }
    }

    private void Release(NativeFileQuerySession session, bool reusable)
    {
        lock (lifecycleGate)
        {
            try
            {
                session.EndQuery(reusable);
                if (reusable && Volatile.Read(ref disposed) == 0)
                {
                    available.Enqueue(session);
                    signal.Release();
                    return;
                }
                session.Dispose();
            }
            catch
            {
                // Acquisition retries EndQuery; pool disposal retries closing.
                // Both paths must retain this session after a failed return.
                available.Enqueue(session);
                if (Volatile.Read(ref disposed) == 0) signal.Release();
                else Volatile.Write(ref disposed, 1);
                throw;
            }
        }
    }
}
