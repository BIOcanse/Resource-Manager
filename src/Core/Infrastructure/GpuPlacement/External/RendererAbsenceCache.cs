using Microsoft.Diagnostics.Tracing.Parsers;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Infrastructure.Telemetry.Etw;

namespace ResourceManager.App.Infrastructure.GpuPlacement.External;

/// <summary>Caches only complete negative renderer qualifications for one process incarnation.</summary>
internal sealed class RendererAbsenceCache : IDisposable
{
    internal const int MaximumCachedEntries = 4096;
    private const KernelTraceEventParser.Keywords RequiredKeywords = KernelTraceEventParser.Keywords.Process
        | KernelTraceEventParser.Keywords.ImageLoad;

    private readonly object gate = new();
    private readonly object lifetimeGate = new();
    private readonly IKernelEtwSessionBroker broker;
    private readonly Func<KernelEtwEventSource, int, KernelEtwNativeRendererHistory.Revision?> readRevision;
    private readonly Dictionary<CacheKey, CacheEntry> entries = new(CacheKeyComparer.Instance);
    private readonly string subscriptionId = $"renderer-absence-cache:{Guid.NewGuid():N}";
    private IKernelEtwSubscription? lease;
    private Binding? currentBinding;
    private object cacheEpoch = new();
    private long? observedGeneration;
    private int? observedEventsLost;
    private int holders;
    private bool disposed;

    public RendererAbsenceCache(IKernelEtwSessionBroker broker)
        : this(broker, KernelEtwNativeRendererHistory.Read) { }

    internal RendererAbsenceCache(IKernelEtwSessionBroker broker,
        Func<KernelEtwEventSource, int, KernelEtwNativeRendererHistory.Revision?> readRevision)
    {
        this.broker = broker ?? throw new ArgumentNullException(nameof(broker));
        this.readRevision = readRevision;
    }

    internal readonly record struct Observation(bool IsAbsent, object? Stamp)
    {
        internal bool CanPublishAbsent => !IsAbsent && Stamp is not null;
    }

    internal Observation Read(GpuPlacementProcessInstance identity)
    {
        if (!TryCreateKey(identity, out var key)) return default;
        var subscription = Volatile.Read(ref lease);
        if (subscription is null || !TryGetUsableBinding(subscription, out var binding, out var snapshot))
            return default;

        lock (gate)
        {
            if (disposed || !ReferenceEquals(currentBinding, binding) || !binding.Source.IsActive
                || observedGeneration != snapshot.Generation || observedEventsLost != snapshot.EventsLost)
                return default;

            var revision = ReadRevision(binding, identity.ProcessId);
            if (revision is null) return default;
            if (entries.TryGetValue(key, out var entry))
            {
                if (entry.Revision == revision.Value)
                    return new(true, null);
                entries.Remove(key);
            }

            return new(false, new PublicationStamp(key, binding, snapshot.Generation,
                snapshot.EventsLost, cacheEpoch, revision.Value));
        }
    }

    internal void PublishAbsent(GpuPlacementProcessInstance identity, Observation observation)
    {
        if (!TryCreateKey(identity, out var key)
            || observation.Stamp is not PublicationStamp stamp
            || !CacheKeyComparer.Instance.Equals(key, stamp.Key))
            return;

        var subscription = Volatile.Read(ref lease);
        if (subscription is null || !TryGetUsableBinding(subscription, out var binding, out var snapshot))
            return;

        lock (gate)
        {
            if (disposed || !ReferenceEquals(currentBinding, binding) || !ReferenceEquals(binding, stamp.Binding)
                || !binding.Source.IsActive || !ReferenceEquals(cacheEpoch, stamp.CacheEpoch)
                || snapshot.Generation != stamp.Generation || snapshot.EventsLost != stamp.EventsLost
                || observedGeneration != stamp.Generation || observedEventsLost != stamp.EventsLost
                || ReadRevision(binding, identity.ProcessId) != stamp.Revision)
                return;

            if (entries.TryGetValue(key, out var existing)
                && existing.Revision == stamp.Revision)
                return;

            if (entries.Count >= MaximumCachedEntries)
            {
                // Overflow flushes the bounded cache and invalidates outstanding publication stamps.
                ClearCacheLocked();
                return;
            }

            entries[key] = new CacheEntry(stamp.Revision);
        }
    }

    /// <summary>Held by the owning Smart block for its entire run, never by a read or sampling round.</summary>
    internal IDisposable Acquire()
    {
        lock (lifetimeGate)
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (holders++ != 0) return new Hold(this);
            }
            try
            {
                var acquired = broker.Acquire(new(subscriptionId, RequiredKeywords, BindSource)
                { NativeOutputs = KernelEtwNativeOutputs.Renderer });
                lock (gate) Volatile.Write(ref lease, acquired);
                return new Hold(this);
            }
            catch
            {
                lock (gate) holders = 0;
                ReleaseCapture();
                throw;
            }
        }
    }

    private void Release()
    {
        lock (lifetimeGate)
        {
            lock (gate)
            {
                if (holders == 0 || --holders != 0) return;
            }
            ReleaseCapture();
        }
    }

    public void Dispose()
    {
        lock (lifetimeGate)
        {
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                holders = 0;
            }
            ReleaseCapture();
        }
    }

    private void ReleaseCapture()
    {
        IKernelEtwSubscription? ownedLease;
        lock (gate)
        {
            ownedLease = lease;
            Volatile.Write(ref lease, null);
            currentBinding = null;
            ClearTrackingLocked();
        }
        ownedLease?.Dispose();
    }

    private sealed class Hold(RendererAbsenceCache cache) : IDisposable
    {
        private RendererAbsenceCache? owner = cache;
        public void Dispose() => Interlocked.Exchange(ref owner, null)?.Release();
    }

    private void BindSource(KernelEtwEventSource source, KernelEtwSessionContext context)
    {
        source.ConsumerCounter("renderer-invalidation");
        source.ConsumerRejectionCounter("renderer-invalidation", "invalid-process");
        source.ConsumerRejectionCounter("renderer-invalidation", "stale-binding");
        lock (gate)
        {
            if (disposed || holders == 0) return;
            currentBinding = new Binding(source, context);
            ClearTrackingLocked();
        }
    }

    private KernelEtwNativeRendererHistory.Revision? ReadRevision(Binding binding, int processId)
    {
        try { return readRevision(binding.Source, processId); }
        catch (ObjectDisposedException) when (!binding.Source.IsActive) { return null; }
    }

    private bool TryGetUsableBinding(IKernelEtwSubscription subscription, out Binding binding,
        out KernelEtwSessionSnapshot snapshot)
    {
        try
        {
            snapshot = subscription.GetSnapshot();
        }
        catch (ObjectDisposedException)
        {
            MarkUnavailable();
            binding = null!;
            snapshot = null!;
            return false;
        }

        if (!snapshot.IsRunning || (snapshot.EnabledKeywords & RequiredKeywords) != RequiredKeywords)
        {
            MarkUnavailable();
            binding = null!;
            return false;
        }

        lock (gate)
        {
            var current = currentBinding;
            if (disposed || current is null || !current.Source.IsActive
                || current.Context.Generation != snapshot.Generation)
            {
                ClearTrackingLocked();
                binding = null!;
                return false;
            }

            if (observedGeneration != snapshot.Generation || observedEventsLost != snapshot.EventsLost)
            {
                ClearCacheLocked();
                observedGeneration = snapshot.Generation;
                observedEventsLost = snapshot.EventsLost;
            }

            binding = current;
            return true;
        }
    }

    private void MarkUnavailable()
    {
        lock (gate) ClearTrackingLocked();
    }

    private void ClearTrackingLocked()
    {
        ClearCacheLocked();
        observedGeneration = null;
        observedEventsLost = null;
    }

    private void ClearCacheLocked()
    {
        entries.Clear();
        cacheEpoch = new object();
    }

    private static bool TryCreateKey(GpuPlacementProcessInstance identity, out CacheKey key)
    {
        if (identity is not null && identity.ProcessId > 0 && identity.ProcessStartKey != 0
            && !string.IsNullOrWhiteSpace(identity.ExecutablePath))
        {
            key = new CacheKey(identity.ProcessId, identity.ProcessStartKey, identity.ExecutablePath);
            return true;
        }

        key = default;
        return false;
    }

    private sealed class Binding
    {
        internal Binding(KernelEtwEventSource source,
            KernelEtwSessionContext context)
        {
            Source = source;
            Context = context;
        }

        internal KernelEtwEventSource Source { get; }
        internal KernelEtwSessionContext Context { get; }
    }

    private readonly record struct CacheKey(int ProcessId, ulong ProcessStartKey, string ExecutablePath);
    private readonly record struct CacheEntry(KernelEtwNativeRendererHistory.Revision Revision);
    private sealed record PublicationStamp(CacheKey Key, Binding Binding, long Generation, int EventsLost,
        object CacheEpoch, KernelEtwNativeRendererHistory.Revision Revision);

    private sealed class CacheKeyComparer : IEqualityComparer<CacheKey>
    {
        internal static CacheKeyComparer Instance { get; } = new();

        public bool Equals(CacheKey x, CacheKey y)
            => x.ProcessId == y.ProcessId && x.ProcessStartKey == y.ProcessStartKey
                && StringComparer.OrdinalIgnoreCase.Equals(x.ExecutablePath, y.ExecutablePath);

        public int GetHashCode(CacheKey value)
            => HashCode.Combine(value.ProcessId, value.ProcessStartKey,
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.ExecutablePath));
    }
}
