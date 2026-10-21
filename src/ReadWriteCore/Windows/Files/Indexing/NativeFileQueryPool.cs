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
        Interlocked.Exchange(ref release, null)?.Invoke(Session, reusable);
    }
}

public sealed class NativeFileQueryPool : INativeFileQueryLeaseSource, IDisposable
{
    private readonly ConcurrentQueue<NativeFileQuerySession> available = new();
    private readonly SemaphoreSlim signal;
    private long nextEpoch;
    private int disposed;

    public NativeFileQueryPool(
        in NativeFileQueryConfiguration configuration,
        int sessionCount,
        ulong totalResidentByteBudget)
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
                var session = new NativeFileQuerySession(in configuration);
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
        if (Volatile.Read(ref disposed) != 0 || !available.TryDequeue(out var session))
        {
            throw new ObjectDisposedException(nameof(NativeFileQueryPool));
        }
        return new NativeFileQueryLease(session, Release);
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
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }
        while (available.TryDequeue(out var session))
        {
            session.Dispose();
        }
        signal.Dispose();
    }

    private void Release(NativeFileQuerySession session, bool reusable)
    {
        if (reusable && Volatile.Read(ref disposed) == 0)
        {
            available.Enqueue(session);
            signal.Release();
            return;
        }
        session.Dispose();
    }
}
