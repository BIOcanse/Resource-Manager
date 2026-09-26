using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using ResourceManager.App.Domain.RuntimeSpecialization;

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

internal sealed class NativeFileQueryWorkspace : INativeFileQueryLeaseSource, IDisposable
{
    private readonly ConcurrentQueue<NativeFileQuerySession> available = new();
    private readonly SemaphoreSlim signal;
    private long nextEpoch;
    private int disposed;

    internal NativeFileQueryWorkspace(CompiledHostManagerFileQueryPlan plan)
        : this(
            CreateConfiguration(plan),
            plan.Recreate.QuerySessionCount,
            checked((ulong)plan.HotPublish.TotalResidentByteBudget))
    {
    }

    internal NativeFileQueryWorkspace(
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
            throw new ObjectDisposedException(nameof(NativeFileQueryWorkspace));
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

    internal static unsafe NativeFileQueryConfiguration CreateConfiguration(
        CompiledHostManagerFileQueryPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.IsPublished)
        {
            throw new InvalidOperationException("The Host Manager file-query plan is not published.");
        }
        var capacity = plan.Recreate.Capacity;
        var hot = plan.HotPublish;
        return new NativeFileQueryConfiguration
        {
            AbiVersion = plan.Build.AbiVersion,
            StructSize = checked((uint)Unsafe.SizeOf<NativeFileQueryConfiguration>()),
            Generation = plan.ConfigurationGeneration,
            MaximumQueryUtf8ByteCount = checked((uint)capacity.MaximumQueryUtf8ByteCount),
            MaximumQueryRuneCount = checked((uint)capacity.MaximumQueryRuneCount),
            MaximumPlanUtf8ByteCount = checked((uint)capacity.MaximumPlanUtf8ByteCount),
            MaximumSourcePlanCount = checked((uint)capacity.MaximumSourcePlanCount),
            MaximumCandidateCountPerSource = checked((uint)capacity.MaximumCandidateCountPerSource),
            MaximumSubmittedCandidateCount = checked((uint)capacity.MaximumSubmittedCandidateCount),
            MaximumUniqueCandidateCount = checked((uint)capacity.MaximumUniqueCandidateCount),
            MaximumCandidateSubmitBatchCount = checked((uint)capacity.MaximumCandidateSubmitBatchCount),
            MaximumCandidateSubmitUtf8ByteCount = checked((uint)capacity.MaximumCandidateSubmitUtf8ByteCount),
            CandidateTextArenaByteCount = checked((uint)capacity.CandidateTextArenaByteCount),
            MaximumFileNameUtf8ByteCount = checked((uint)capacity.MaximumFileNameUtf8ByteCount),
            MaximumResultCount = checked((uint)capacity.MaximumResultCount),
            EntryIndexCapacity = checked((uint)capacity.EntryIndexCapacity),
            OrdinalIndexCapacity = checked((uint)capacity.OrdinalIndexCapacity),
            ShortQueryRuneThreshold = checked((uint)hot.ShortQueryRuneThreshold),
            UnicodeTokenizerVersion = hot.UnicodeTokenizerVersion,
            UnicodeRemoveDiacriticsMode = hot.UnicodeRemoveDiacriticsMode,
            TrigramTokenizerContractVersion = hot.TrigramTokenizerContractVersion,
            CandidateLimitMultiplier = checked((uint)hot.CandidateLimitMultiplier),
            CandidateLimitFloor = checked((uint)hot.CandidateLimitFloor),
            CandidateLimitCeiling = checked((uint)hot.CandidateLimitCeiling),
            FileNamePriority = checked((uint)hot.FileNamePriority),
            RelativePathPriority = checked((uint)hot.RelativePathPriority),
            SoftwareNamePriority = checked((uint)hot.SoftwareNamePriority),
            Flags = 0,
            TextMatchingVersion = hot.TextMatchingVersion,
            ResidentByteBudget = checked((ulong)hot.PerSessionResidentByteBudget)
        };
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
