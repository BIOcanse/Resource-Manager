using ResourceManager.App.Domain.Metrics;
using ResourceManager.ReadWrite.Native.MetricOwner;
using ResourceManager.ReadWrite.Windows.Metrics.Zig;

namespace ResourceManager.ReadWrite.Windows.Metrics.Views;

/// <summary>
/// One admitted selection shape. Versions are cached only while actual reader code is using them.
/// No publication listener, borrowed memory, Ledger lease or pin is owned by this object.
/// </summary>
public sealed class HardwareMetricObjectView
{
    private readonly object gate = new();
    private readonly int maxReaders;
    private readonly int maxSources;
    private int readers;
    private readonly List<Entry> entries;
    private long buildCount;
    private readonly Action<MetricOwnerFailureTransition>? failureTransition;
    private readonly MetricOwnerFailure[] failureStates = new MetricOwnerFailure[2]; // readers and sources

    public HardwareMetricObjectView(int maxReaders, int maxSources,
        Action<MetricOwnerFailureTransition>? failureTransition = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxReaders);
        ArgumentOutOfRangeException.ThrowIfNegative(maxSources);
        this.maxReaders = maxReaders; this.maxSources = maxSources;
        // Each admitted reader can hold one completed version while claiming
        // one replacement. Reserve this bounded metadata outside the state lock.
        entries = new(checked(maxReaders * 2));
        this.failureTransition = failureTransition;
    }

    public long BuildCount { get { lock (gate) return buildCount; } }
    public int ActiveReaders { get { lock (gate) return readers; } }
    public bool HasCachedSnapshot { get { lock (gate) return entries.Count != 0; } }

    public T RunReader<T>(Func<Reader, T> code)
    {
        ArgumentNullException.ThrowIfNull(code);
        var reader = Enter();
        try { return code(reader); }
        finally { Exit(reader); }
    }

    public async Task<T> RunReaderAsync<T>(Func<Reader, Task<T>> code)
    {
        ArgumentNullException.ThrowIfNull(code);
        var reader = Enter();
        try { return await code(reader).ConfigureAwait(false); }
        finally { Exit(reader); }
    }

    private Reader Enter()
    {
        Reader? reader = null;
        MetricOwnerFailure failure = default;
        MetricOwnerFailureTransition? transition;
        lock (gate)
        {
            if (readers == maxReaders) failure = CapacityFailure((ulong)readers + 1, (ulong)maxReaders);
            else { reader = new Reader(this); readers++; }
            transition = SetFailure(MetricOwnerOperation.ObjectReaders, failure);
        }
        try { Report(transition); }
        catch { if (reader is not null) Exit(reader); throw; }
        if (reader is null) throw new MetricOwnerException(failure);
        return reader;
    }

    private void Exit(Reader reader)
    {
        lock (gate)
        {
            reader.Active = false;
            Release(reader.Held);
            reader.Held = null;
            if (--readers != 0) return;
            entries.Clear();
        }
    }

    public sealed class Reader
    {
        private readonly HardwareMetricObjectView owner;
        private bool reading;
        internal bool Active { get; set; } = true;
        internal Entry? Held { get; set; }
        internal Reader(HardwareMetricObjectView owner) => this.owner = owner;

        /// <summary>
        /// One transport projection per demanded version/shape. The caller supplies
        /// the same cold factory instance for this view. Factories execute outside
        /// the state lock; another reader waits for the same claimed build outside it.
        /// </summary>
        public T ReadProjection<T>(MetricObjectVersion version, MetricSnapshotInput input,
            Func<HardwareMetricSnapshot, T> factory) where T : class
        {
            ArgumentNullException.ThrowIfNull(factory);
            BeginRead();
            try
            {
                ValidateSources(version);
                var entry = ReadEntry(version, input);
                var claim = ClaimProjection<T>(entry, factory, null);
                if (claim.Ready is T cached) return cached;
                if (claim.Completion is null)
                {
                    // Allocate completion metadata without holding the state lock,
                    // then recheck in case another reader claimed this same build.
                    var candidate = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
                    claim = ClaimProjection<T>(entry, factory, candidate);
                    if (claim.Ready is T completed) return completed;
                }
                var completion = claim.Completion!;
                if (claim.Build)
                {
                    T built;
                    try { built = factory(entry.Snapshot!) ?? throw new InvalidOperationException("The object projection returned null."); }
                    catch (Exception error)
                    {
                        lock (owner.gate)
                        {
                            entry.ProjectionCompletion = null;
                            entry.ProjectionFactory = null;
                            entry.ProjectionThread = 0;
                        }
                        completion.SetException(error);
                        _ = completion.Task.Exception;
                        throw;
                    }
                    lock (owner.gate)
                    {
                        entry.Projection = built;
                        entry.ProjectionCompletion = null;
                        entry.ProjectionThread = 0;
                    }
                    completion.SetResult(built);
                }
                var result = (T)completion.Task.GetAwaiter().GetResult();
                lock (owner.gate) RequireActive();
                return result;
            }
            finally { EndRead(); }
        }

        /// <summary>Call inside the consistent selected-column read callback; no input is retained.</summary>
        public HardwareMetricSnapshot Read(MetricObjectVersion version, MetricSnapshotInput input)
        {
            BeginRead();
            try { ValidateSources(version); return ReadEntry(version, input).Snapshot!; }
            finally { EndRead(); }
        }

        private void BeginRead()
        {
            lock (owner.gate)
            {
                RequireActive();
                if (reading) throw new InvalidOperationException("A reader cannot execute concurrent or recursive reads.");
                reading = true;
            }
        }
        private void EndRead() { lock (owner.gate) reading = false; }
        private void RequireActive()
        {
            if (!Active) throw new ObjectDisposedException(nameof(Reader));
        }
        private void ValidateSources(MetricObjectVersion version)
        {
            MetricOwnerFailure failure = default;
            MetricOwnerFailureTransition? transition;
            lock (owner.gate)
            {
                RequireActive();
                if (version.Sources.Length > owner.maxSources)
                    failure = CapacityFailure((ulong)version.Sources.Length, (ulong)owner.maxSources);
                transition = owner.SetFailure(MetricOwnerOperation.ObjectSources, failure);
            }
            owner.Report(transition);
            if (failure.Code != MetricOwnerCode.Ok) throw new MetricOwnerException(failure);
        }

        private Entry? Find(MetricObjectVersion version)
        {
            if (Held is { } held && held.Matches(version)) return held;
            var index = 0;
            while (index < owner.entries.Count)
            {
                var entry = owner.entries[index++];
                if (entry.Matches(version)) return entry;
            }
            return null;
        }
        private Entry Claim(MetricObjectVersion version, out bool build)
        {
            lock (owner.gate)
            {
                RequireActive();
                if (Find(version) is { } existing) { existing.Uses++; build = false; return existing; }
            }
            // Only version identity is copied, never the borrowed metric input.
            var candidate = new Entry(version);
            lock (owner.gate)
            {
                RequireActive();
                var entry = Find(version);
                build = entry is null;
                if (entry is null) { entry = candidate; owner.entries.Add(entry); }
                entry.Uses++;
                return entry;
            }
        }
        private Entry ReadEntry(MetricObjectVersion version, MetricSnapshotInput input)
        {
            var entry = Claim(version, out var build);
            try
            {
                HardwareMetricSnapshot? snapshot;
                if (build)
                {
                    try { snapshot = MetricSnapshotProjection.Create(version, input); }
                    catch (Exception error)
                    {
                        lock (owner.gate) owner.entries.Remove(entry);
                        entry.SnapshotCompletion.SetException(error);
                        _ = entry.SnapshotCompletion.Task.Exception;
                        throw;
                    }
                    lock (owner.gate) { entry.Snapshot = snapshot; owner.buildCount++; }
                    entry.SnapshotCompletion.SetResult(snapshot);
                }
                else
                {
                    lock (owner.gate)
                    {
                        snapshot = entry.Snapshot;
                        if (snapshot is null && entry.SnapshotThread == Environment.CurrentManagedThreadId)
                            throw new InvalidOperationException("A snapshot build cannot wait for itself.");
                    }
                    snapshot ??= entry.SnapshotCompletion.Task.GetAwaiter().GetResult();
                }
                lock (owner.gate)
                {
                    RequireActive();
                    if (!ReferenceEquals(Held, entry))
                    {
                        owner.Release(Held);
                        entry.Readers++;
                        Held = entry;
                    }
                }
                return entry;
            }
            finally
            {
                lock (owner.gate) { entry.Uses--; owner.RemoveUnused(entry); }
            }
        }
        private ProjectionClaim ClaimProjection<T>(Entry entry, Func<HardwareMetricSnapshot, T> factory,
            TaskCompletionSource<object>? candidate) where T : class
        {
            lock (owner.gate)
            {
                RequireActive();
                if (entry.ProjectionFactory is not null && !ReferenceEquals(entry.ProjectionFactory, factory))
                    throw new InvalidOperationException("A projection factory must match its admitted object view shape.");
                if (entry.Projection is { } ready)
                {
                    if (ready is not T) throw new InvalidOperationException("A projection factory must match its admitted object view shape.");
                    return new(ready, null, false);
                }
                if (entry.ProjectionCompletion is { } pending)
                {
                    if (entry.ProjectionThread == Environment.CurrentManagedThreadId)
                        throw new InvalidOperationException("A projection build cannot wait for itself.");
                    return new(null, pending, false);
                }
                if (candidate is null) return default;
                entry.ProjectionFactory = factory;
                entry.ProjectionCompletion = candidate;
                entry.ProjectionThread = Environment.CurrentManagedThreadId;
                return new(null, candidate, true);
            }
        }
    }

    private readonly record struct ProjectionClaim(object? Ready, TaskCompletionSource<object>? Completion, bool Build);

    private void Release(Entry? entry)
    {
        if (entry is null) return;
        entry.Readers--;
        RemoveUnused(entry);
    }
    private void RemoveUnused(Entry entry)
    {
        if (entry.Readers == 0 && entry.Uses == 0) entries.Remove(entry);
    }
    private static MetricOwnerFailure CapacityFailure(ulong required, ulong limit)
        => new() { Code = MetricOwnerCode.CapacityExceeded, Family = MetricOwnerFamily.Binding, Required = required, Limit = limit };

    // State changes are short and ordered under gate. External logging is delivered afterwards.
    private MetricOwnerFailureTransition? SetFailure(MetricOwnerOperation operation, MetricOwnerFailure failure)
    {
        ref var previous = ref failureStates[operation == MetricOwnerOperation.ObjectReaders ? 0 : 1];
        if (previous.Code == failure.Code && previous.Family == failure.Family) return null;
        var recovered = failure.Code == MetricOwnerCode.Ok;
        var report = recovered ? previous : failure;
        previous = failure;
        return new(operation, report, recovered);
    }
    private void Report(MetricOwnerFailureTransition? transition)
    {
        if (transition is { } changed) failureTransition?.Invoke(changed);
    }

    internal sealed class Entry
    {
        private readonly MetricSourceVersion[] sources;
        private readonly ulong region, configuration, catalog, history, committed;
        private readonly uint privilege;
        internal readonly int SnapshotThread = Environment.CurrentManagedThreadId;
        internal readonly TaskCompletionSource<HardwareMetricSnapshot> SnapshotCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal HardwareMetricSnapshot? Snapshot { get; set; }
        internal object? Projection { get; set; }
        internal Delegate? ProjectionFactory { get; set; }
        internal TaskCompletionSource<object>? ProjectionCompletion { get; set; }
        internal int ProjectionThread { get; set; }
        internal int Readers { get; set; }
        internal int Uses { get; set; }
        internal Entry(MetricObjectVersion version)
        {
            sources = version.Sources.ToArray();
            region = version.RegionIncarnation; configuration = version.ConfigurationRevision;
            catalog = version.CatalogRevision; history = version.HistoryRevision;
            committed = version.CommittedGeneration; privilege = version.Privilege;
        }
        internal bool Matches(MetricObjectVersion version)
            => region == version.RegionIncarnation && configuration == version.ConfigurationRevision
                && catalog == version.CatalogRevision && history == version.HistoryRevision
                && committed == version.CommittedGeneration && privilege == version.Privilege
                && version.Sources.SequenceEqual(sources);
    }
}
