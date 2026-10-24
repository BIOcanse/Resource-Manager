using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Infrastructure.Telemetry.Etw;

namespace ResourceManager.App.Infrastructure.GpuPlacement.External;

/// <summary>Caches only complete negative renderer qualifications for one process incarnation.</summary>
internal sealed class RendererAbsenceCache : IDisposable
{
    internal const int MaximumCachedEntries = 4096;
    private const int RevisionSlotCount = 4096;
    private const KernelTraceEventParser.Keywords RequiredKeywords = KernelTraceEventParser.Keywords.Process
        | KernelTraceEventParser.Keywords.ImageLoad;

    private readonly object gate = new();
    private readonly IKernelEtwSessionBroker broker;
    private readonly Dictionary<CacheKey, CacheEntry> entries = new(CacheKeyComparer.Instance);
    // Hash collisions only cause conservative misses. The fixed table bounds invalidation stamps.
    private readonly long[] pidRevisions = new long[RevisionSlotCount];
    private readonly string subscriptionId = $"renderer-absence-cache:{Guid.NewGuid():N}";
    private IKernelEtwSubscription? lease;
    private Binding? currentBinding;
    private object cacheEpoch = new();
    private long nextBindingId;
    private long? observedGeneration;
    private int? observedEventsLost;
    private bool leaseAttempted;
    private bool disposed;

    public RendererAbsenceCache(IKernelEtwSessionBroker broker)
        => this.broker = broker ?? throw new ArgumentNullException(nameof(broker));

    internal readonly record struct Observation(bool IsAbsent, object? Stamp)
    {
        internal bool CanPublishAbsent => !IsAbsent && Stamp is not null;
    }

    internal Observation Read(GpuPlacementProcessInstance identity)
    {
        if (!TryCreateKey(identity, out var key)) return default;
        var subscription = EnsureLease();
        if (subscription is null || !TryGetUsableBinding(subscription, out var binding, out var snapshot))
            return default;

        lock (gate)
        {
            if (disposed || !ReferenceEquals(currentBinding, binding) || !binding.Source.IsActive
                || observedGeneration != snapshot.Generation || observedEventsLost != snapshot.EventsLost)
                return default;

            var revisionSlot = GetRevisionSlot(identity.ProcessId);
            var revision = pidRevisions[revisionSlot];
            if (entries.TryGetValue(key, out var entry))
            {
                if (entry.RevisionSlot == revisionSlot && entry.Revision == revision)
                    return new(true, null);
                entries.Remove(key);
            }

            return new(false, new PublicationStamp(key, binding, snapshot.Generation,
                snapshot.EventsLost, cacheEpoch, revisionSlot, revision));
        }
    }

    internal void PublishAbsent(GpuPlacementProcessInstance identity, Observation observation)
    {
        if (!TryCreateKey(identity, out var key)
            || observation.Stamp is not PublicationStamp stamp
            || !CacheKeyComparer.Instance.Equals(key, stamp.Key))
            return;

        var subscription = EnsureLease();
        if (subscription is null || !TryGetUsableBinding(subscription, out var binding, out var snapshot))
            return;

        lock (gate)
        {
            if (disposed || !ReferenceEquals(currentBinding, binding) || !ReferenceEquals(binding, stamp.Binding)
                || !binding.Source.IsActive || !ReferenceEquals(cacheEpoch, stamp.CacheEpoch)
                || snapshot.Generation != stamp.Generation || snapshot.EventsLost != stamp.EventsLost
                || observedGeneration != stamp.Generation || observedEventsLost != stamp.EventsLost
                || stamp.RevisionSlot != GetRevisionSlot(identity.ProcessId)
                || pidRevisions[stamp.RevisionSlot] != stamp.Revision)
                return;

            if (entries.TryGetValue(key, out var existing)
                && existing.RevisionSlot == stamp.RevisionSlot && existing.Revision == stamp.Revision)
                return;

            if (entries.Count >= MaximumCachedEntries)
            {
                // Overflow flushes the bounded cache and invalidates outstanding publication stamps.
                ClearCacheLocked();
                return;
            }

            entries[key] = new CacheEntry(stamp.RevisionSlot, stamp.Revision);
        }
    }

    // The production ETW handlers route decoded PIDs through this helper. It is also
    // the deterministic test seam because TraceEvent payload classes are sealed.
    internal void OnProcessChanged(KernelEtwEventSource source, int processId)
    {
        Binding? binding;
        lock (gate)
        {
            binding = currentBinding;
            if (binding is null || !ReferenceEquals(binding.Source, source)) return;
        }

        OnProcessChanged(binding, processId);
    }

    public void Dispose()
    {
        IKernelEtwSubscription? ownedLease;
        Binding? binding;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            ownedLease = lease;
            lease = null;
            binding = currentBinding;
            currentBinding = null;
            ClearTrackingLocked();
        }

        if (binding is not null) Detach(binding);
        ownedLease?.Dispose();
    }

    private IKernelEtwSubscription? EnsureLease()
    {
        lock (gate)
        {
            if (disposed) return null;
            if (lease is not null) return lease;
            if (leaseAttempted) return null;
            leaseAttempted = true;
        }

        IKernelEtwSubscription acquired;
        try
        {
            acquired = broker.Subscribe(new KernelEtwSubscriptionRequest(
                subscriptionId, RequiredKeywords, BindSource));
        }
        catch (ObjectDisposedException)
        {
            return null;
        }

        var disposeAcquired = false;
        lock (gate)
        {
            if (disposed) disposeAcquired = true;
            else lease = acquired;
        }

        if (disposeAcquired)
        {
            acquired.Dispose();
            return null;
        }

        return acquired;
    }

    private void BindSource(KernelEtwEventSource source, KernelEtwSessionContext context)
    {
        Binding candidate;
        lock (gate)
        {
            if (disposed) return;
            candidate = new Binding(this, ++nextBindingId, source, context);
        }

        Attach(candidate);

        Binding? previous = null;
        var accepted = false;
        lock (gate)
        {
            if (!disposed && (currentBinding is null || candidate.Id > currentBinding.Id))
            {
                previous = currentBinding;
                currentBinding = candidate;
                ClearTrackingLocked();
                accepted = true;
            }
        }

        if (previous is not null) Detach(previous);
        if (!accepted) Detach(candidate);
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

    private void OnProcessChanged(Binding binding, int processId)
    {
        if (processId <= 0) return;
        lock (gate)
        {
            if (disposed || !ReferenceEquals(currentBinding, binding) || !binding.Source.IsActive) return;

            var revisionSlot = GetRevisionSlot(processId);
            if (pidRevisions[revisionSlot] == long.MaxValue)
            {
                ClearCacheLocked();
            }
            else
            {
                pidRevisions[revisionSlot]++;
                // Entries are invalid immediately by revision and removed on their next read.
                // Keep image callbacks constant-time; the cache remains bounded independently.
            }
        }
    }

    private void Attach(Binding binding)
    {
        binding.Source.ProcessStartGroup += binding.ProcessStarted;
        binding.Source.ProcessEndGroup += binding.ProcessEnded;
        binding.Source.ImageDCStart += binding.ImageChanged;
        binding.Source.ImageLoad += binding.ImageChanged;
        binding.Source.ImageUnload += binding.ImageChanged;
    }

    private static void Detach(Binding binding)
    {
        binding.Source.ProcessStartGroup -= binding.ProcessStarted;
        binding.Source.ProcessEndGroup -= binding.ProcessEnded;
        binding.Source.ImageDCStart -= binding.ImageChanged;
        binding.Source.ImageLoad -= binding.ImageChanged;
        binding.Source.ImageUnload -= binding.ImageChanged;
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
        Array.Clear(pidRevisions, 0, pidRevisions.Length);
        cacheEpoch = new object();
    }

    private static int GetRevisionSlot(int processId)
        => (int)(unchecked((uint)processId * 2654435761u) % RevisionSlotCount);

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
        internal Binding(RendererAbsenceCache owner, long id, KernelEtwEventSource source,
            KernelEtwSessionContext context)
        {
            Id = id;
            Source = source;
            Context = context;
            ProcessStarted = data => owner.OnProcessChanged(this, data.ProcessID);
            ProcessEnded = data => owner.OnProcessChanged(this, data.ProcessID);
            ImageChanged = data => owner.OnProcessChanged(this, data.ProcessID);
        }

        internal long Id { get; }
        internal KernelEtwEventSource Source { get; }
        internal KernelEtwSessionContext Context { get; }
        internal Action<ProcessTraceData> ProcessStarted { get; }
        internal Action<ProcessTraceData> ProcessEnded { get; }
        internal Action<ImageLoadTraceData> ImageChanged { get; }
    }

    private readonly record struct CacheKey(int ProcessId, ulong ProcessStartKey, string ExecutablePath);
    private readonly record struct CacheEntry(int RevisionSlot, long Revision);
    private sealed record PublicationStamp(CacheKey Key, Binding Binding, long Generation, int EventsLost,
        object CacheEpoch, int RevisionSlot, long Revision);

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
