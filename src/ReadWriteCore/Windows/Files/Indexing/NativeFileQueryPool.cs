using ResourceManager.ReadWrite.Native;
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

/// <summary>How a file-query pool holds sessions: at most <see cref="SessionLimit"/> live sessions (never more
/// than the pool's capacity), <see cref="PrebuiltSessions"/> created with the pool (at most the limit), and
/// whether a returned session that no waiter takes is closed (<see cref="CloseIdleSessions"/>) or kept.</summary>
public readonly record struct NativeFileQueryPoolShape(int SessionLimit, int PrebuiltSessions, bool CloseIdleSessions);

public sealed class NativeFileQueryPool : INativeFileQueryLeaseSource, IDisposable
{
    private readonly Queue<NativeFileQuerySession> available = new();
    private readonly Queue<TaskCompletionSource> waiters = new();
    private readonly object lifecycleGate = new();
    private readonly NativeFileQueryConfiguration configuration;
    private readonly Func<ulong, uint, INativeFileQueryScratch> createScratch;
    private readonly Func<NativeFileQueryPoolShape> shape;
    private readonly int capacity;
    private readonly ulong totalResidentByteBudget;
    private ulong resident;
    private int live;
    private long nextEpoch;
    private int disposed;

    /// <summary>Creates all <paramref name="sessionCount"/> sessions now and keeps returned sessions.</summary>
    public NativeFileQueryPool(
        in NativeFileQueryConfiguration configuration,
        int sessionCount,
        ulong totalResidentByteBudget)
        : this(in configuration, sessionCount, totalResidentByteBudget, NativeFileQueryScratch.CreateOrdinary) { }

    /// <summary>Holds sessions as <paramref name="shape"/> says. The shape is read at each acquisition and
    /// return, so a change applies to later uses; <paramref name="sessionCount"/> stays the hard capacity.</summary>
    public NativeFileQueryPool(
        in NativeFileQueryConfiguration configuration,
        int sessionCount,
        ulong totalResidentByteBudget,
        Func<NativeFileQueryPoolShape> shape)
        : this(in configuration, sessionCount, totalResidentByteBudget, NativeFileQueryScratch.CreateOrdinary,
            shape ?? throw new ArgumentNullException(nameof(shape))) { }

    internal NativeFileQueryPool(in NativeFileQueryConfiguration configuration, int sessionCount,
        ulong totalResidentByteBudget, Func<ulong, uint, INativeFileQueryScratch> createScratch,
        Func<NativeFileQueryPoolShape>? shape = null)
    {
        if (sessionCount <= 0 || totalResidentByteBudget == 0)
        {
            throw new InvalidOperationException(
                "The native file-query workspace shape is not published.");
        }

        this.configuration = configuration;
        this.createScratch = createScratch ?? throw new ArgumentNullException(nameof(createScratch));
        capacity = sessionCount;
        this.totalResidentByteBudget = totalResidentByteBudget;
        this.shape = shape ?? (() => new NativeFileQueryPoolShape(sessionCount, sessionCount, false));
        var initial = this.shape();
        var prebuilt = Math.Min(Math.Max(initial.PrebuiltSessions, 0), LimitOf(initial));
        try
        {
            for (var index = 0; index < prebuilt; index++)
            {
                var session = CreateSession();
                available.Enqueue(session);
                live++;
            }
        }
        catch
        {
            while (available.TryDequeue(out var session))
            {
                session.Dispose();
            }
            throw;
        }
    }

    public async ValueTask<NativeFileQueryLease> AcquireAsync(
        CancellationToken cancellationToken)
    {
        // Cancellation is checked once before waiting; a waiter woken for a session takes it.
        cancellationToken.ThrowIfCancellationRequested();
        while (true)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            NativeFileQuerySession? session = null;
            TaskCompletionSource? waiter = null;
            lock (lifecycleGate)
            {
                if (Volatile.Read(ref disposed) != 0)
                    throw new ObjectDisposedException(nameof(NativeFileQueryPool));
                if (available.TryDequeue(out var idle)) session = idle;
                else if (live < LimitOf(shape())) live++;
                else
                {
                    waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    waiters.Enqueue(waiter);
                }
            }
            if (waiter is not null)
            {
                await using (cancellationToken.Register(static state => ((TaskCompletionSource)state!).TrySetCanceled(), waiter))
                    await waiter.Task.ConfigureAwait(false);
                continue;
            }
            if (session is null)
            {
                // The live slot is reserved above; the session itself is created on demand outside the gate.
                try { session = CreateSession(); }
                catch
                {
                    lock (lifecycleGate) { live--; WakeOneLocked(); }
                    throw;
                }
            }
            lock (lifecycleGate)
            {
                if (Volatile.Read(ref disposed) != 0)
                {
                    // Pool disposal retries closing whatever the queue still owns.
                    available.Enqueue(session);
                    throw new ObjectDisposedException(nameof(NativeFileQueryPool));
                }
                try
                {
                    // A failed prior acquisition may still own a use whose return
                    // failed. Retry that explicit return before borrowing again.
                    session.EndQuery(false);
                    session.BeginQuery();
                }
                catch { available.Enqueue(session); WakeOneLocked(); throw; }
                return new NativeFileQueryLease(session, Release);
            }
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
                CloseLocked(session);
                available.Dequeue();
            }
            while (waiters.TryDequeue(out var waiter))
            {
                waiter.TrySetException(new ObjectDisposedException(nameof(NativeFileQueryPool)));
            }
            Volatile.Write(ref disposed, 2);
        }
    }

    private int LimitOf(NativeFileQueryPoolShape current) => Math.Clamp(current.SessionLimit, 1, capacity);

    private NativeFileQuerySession CreateSession()
    {
        var session = new NativeFileQuerySession(in configuration, createScratch);
        lock (lifecycleGate)
        {
            var next = checked(resident + session.Capacity.ResidentByteCount);
            if (next <= totalResidentByteBudget)
            {
                resident = next;
                return session;
            }
        }
        session.Dispose();
        throw new InvalidOperationException(
            "The native file-query session pool exceeds its explicit total resident budget.");
    }

    private void Release(NativeFileQuerySession session, bool reusable)
    {
        lock (lifecycleGate)
        {
            try
            {
                session.EndQuery(reusable);
                var current = shape();
                // A waiting query always takes a returned session; otherwise the shape decides whether
                // the session stays for the next query or closes (also when the limit was lowered).
                if (reusable && Volatile.Read(ref disposed) == 0
                    && (HasWaiterLocked() || (!current.CloseIdleSessions && live <= LimitOf(current))))
                {
                    available.Enqueue(session);
                    WakeOneLocked();
                    return;
                }
                CloseLocked(session);
                WakeOneLocked();
            }
            catch
            {
                // Acquisition retries EndQuery; pool disposal retries closing.
                // Both paths must retain this session after a failed return.
                available.Enqueue(session);
                if (Volatile.Read(ref disposed) == 0) WakeOneLocked();
                else Volatile.Write(ref disposed, 1);
                throw;
            }
        }
    }

    private void CloseLocked(NativeFileQuerySession session)
    {
        var bytes = session.Capacity.ResidentByteCount;
        session.Dispose();
        resident -= Math.Min(resident, bytes);
        live--;
    }

    private bool HasWaiterLocked()
    {
        while (waiters.TryPeek(out var waiter) && waiter.Task.IsCompleted) waiters.Dequeue();
        return waiters.Count > 0;
    }

    private void WakeOneLocked()
    {
        while (waiters.TryDequeue(out var waiter))
            if (waiter.TrySetResult()) return;
    }
}
