using System.Collections.Immutable;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.ReadWrite.Native;

namespace ResourceManager.App.Infrastructure.Monitoring;

public sealed partial class LastSuccessfulHardwareMetricSnapshot : IDisposable
{
    // The former private native current session had no boundary reader. Native
    // capture/Smart/shared-memory inputs have their own owners and are unchanged.
    private PublicationState current = PublicationState.Empty;
    private bool disposed;
    internal int ComposedViewBuildCount { get; private set; }
    internal int ProjectionBuildCount { get; private set; }

    // Source-compatible SDK overload. No native current cells are needed by this
    // owner, so a supplied factory is never opened or held.
    public LastSuccessfulHardwareMetricSnapshot(string providerName,
        Func<NativeMetricSnapshotSession> createCurrentSession) : this(providerName)
        => ArgumentNullException.ThrowIfNull(createCurrentSession);

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            Volatile.Write(ref current, PublicationState.Empty);
            histories.Clear();
            historyView = historyView.Clear();
            disposed = true;
        }
    }

    /// <summary>Reads publication metadata without composing any dataset values.</summary>
    internal HardwareMetricSnapshot? ReadPublicationContext()
    {
        lock (sync) return ReadState().Context;
    }

    public HardwareMetricSnapshot? Read(MetricSampleRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (sync)
        {
            var state = ReadState();
            if (state.Context is null) return null;
            if (request.IsAll) return ReadSnapshot(state);
            if (state.Projections.TryGetValue(request.PublicationCacheKey, out var cached)) return cached;
            return BuildProjection(state, request);
        }
    }

    private HardwareMetricSnapshot BuildProjection(PublicationState state, MetricSampleRequest request)
    {
        var selected = SelectDatasets(state, request);
        var snapshot = state.Direct ? ReadSnapshot(state)! : Compose(selected, state.Context!) with { History = state.History };
        // Match the host's public field projection, including canonical
        // capacity fields which deliberately have no legacy value alias.
        if (!request.IsCatalogProbe)
        {
            var missing = request.Ids.Where(id => !snapshot.Items.ContainsKey(id)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var addresses = snapshot.FieldBindings.Values.Where(binding => binding.RequestAliases.Any(missing.Contains))
                .Select(static binding => binding.RequestAddress).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var requested = MetricDatasetIds.ResolveSystemDatasets(request, snapshot.Datasets.Keys);
            snapshot = snapshot with
            {
                Items = snapshot.Items.Where(pair => request.Includes(pair.Key) || addresses.Contains(pair.Key))
                    .ToImmutableDictionary(StringComparer.OrdinalIgnoreCase),
                Datasets = requested.ToImmutableDictionary(static id => id,
                    id => snapshot.Datasets.GetValueOrDefault(id) ?? new HardwareMetricDatasetObservation(id,
                        SamplingObservationStatus.Warming, 0, 0, 0, 0, 0, 0), StringComparer.OrdinalIgnoreCase)
            };
        }
        state.Projections.Add(request.PublicationCacheKey, snapshot);
        ProjectionBuildCount++;
        return snapshot;
    }

    /// <summary>Reads a shared immutable projection of this published version.</summary>
    public IReadOnlyDictionary<string, MetricReadingValue> ReadItems(ImmutableArray<string> ids)
    {
        lock (sync)
        {
            var state = ReadState();
            if (ids.IsDefaultOrEmpty || state.Context is null) return EmptyItems;
            if (state.ItemProjections.TryGetValue(ids, out var cached)) return cached;
            var result = ImmutableDictionary.CreateBuilder<string, MetricReadingValue>(StringComparer.OrdinalIgnoreCase);
            foreach (var id in ids)
            {
                if (result.ContainsKey(id)) continue;
                state.Datasets.TryGetValue(id, out var publication);
                var value = state.Direct ? state.Context.Items.GetValueOrDefault(id) : publication?.PayloadSnapshot?.Items.GetValueOrDefault(id);
                var observation = publication?.Observation;
                if (value is not null)
                    result[id] = observation is null ? value : value with
                    {
                        Status = observation.Status,
                        ObservedAt = observation.ObservedAtUtcTicks > 0 ? new DateTimeOffset(observation.ObservedAtUtcTicks, TimeSpan.Zero) : null
                    };
                else if (state.Context.FieldBindings.TryGetValue(id, out var binding))
                    result[id] = new(id, null, binding.Field.Unit)
                    {
                        FieldCode = binding.Field.Code, FieldName = binding.Field.Name,
                        DeviceIdentity = binding.DeviceIdentity, ValueKind = binding.Field.ValueKind,
                        SourceId = binding.SourceId, Status = observation?.Status ?? SamplingObservationStatus.NotRequested
                    };
            }
            var projection = result.ToImmutable();
            state.ItemProjections.Add(ids, projection);
            return projection;
        }
    }

    private PublicationState ReadState()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return Volatile.Read(ref current);
    }

    private void WriteState(PublicationState next)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        Volatile.Write(ref current, next);
    }

    private HardwareMetricSnapshot? ReadSnapshot(PublicationState state)
    {
        if (state.Context is null) return null;
        if (state.Composed is null)
        {
            state.Composed = state.Direct ? state.Context with { History = state.History }
                : Compose(state.Datasets, state.Context) with { History = state.History };
            ComposedViewBuildCount++;
        }
        return state.Composed;
    }

    private static IReadOnlyDictionary<string, DatasetPublication> SelectDatasets(PublicationState state, MetricSampleRequest request)
    {
        var selected = ImmutableDictionary.CreateBuilder<string, DatasetPublication>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in request.Ids) Add(id);
        var missing = request.Ids.Where(id => !(state.Direct ? state.Context!.Items.ContainsKey(id)
                : state.Datasets.TryGetValue(id, out var publication) && publication.PayloadSnapshot?.Items.ContainsKey(id) == true))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (missing.Count > 0)
            foreach (var binding in state.Context!.FieldBindings.Values)
                if (binding.RequestAliases.Any(missing.Contains)) Add(binding.RequestAddress);
        if (request.IncludesCatalog)
            foreach (var id in new[] { MetricDatasetIds.SystemMetricCatalog, MetricDatasetIds.StorageInventory,
                MetricDatasetIds.FanInventory, MetricDatasetIds.BatteryInventory, MetricDatasetIds.NumaInventory }) Add(id);
        if (request.IncludesPrefix("gpu.")) Add(MetricDatasetIds.SystemGpuInventory);
        if (request.IncludesAllGpuCoreMetrics)
            foreach (var id in state.Datasets.Keys) if (request.Includes(id)) Add(id);
        return selected.ToImmutable();

        void Add(string id) { if (state.Datasets.TryGetValue(id, out var value)) selected[id] = value; }
    }

    private static readonly ImmutableDictionary<string, MetricReadingValue> EmptyItems =
        ImmutableDictionary<string, MetricReadingValue>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase);

    private sealed class PublicationState
    {
        internal static PublicationState Empty => new(ImmutableDictionary<string, DatasetPublication>.Empty, null);
        internal readonly ImmutableDictionary<string, DatasetPublication> Datasets;
        internal readonly HardwareMetricSnapshot? Context;
        internal readonly ImmutableDictionary<string, ImmutableArray<HardwareMetricHistorySample>> History;
        internal readonly bool Direct;
        internal HardwareMetricSnapshot? Composed;
        // Only the current version owns these derived references. Replacing the
        // version drops all its projections; an unread version builds none.
        internal readonly Dictionary<string, HardwareMetricSnapshot> Projections = new(StringComparer.OrdinalIgnoreCase);
        internal readonly Dictionary<ImmutableArray<string>, IReadOnlyDictionary<string, MetricReadingValue>> ItemProjections = new(ItemSelectionComparer.Instance);

        internal PublicationState(IReadOnlyDictionary<string, DatasetPublication> datasets, HardwareMetricSnapshot? context,
            ImmutableDictionary<string, ImmutableArray<HardwareMetricHistorySample>>? history = null, bool direct = false)
        {
            Datasets = datasets.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase);
            Context = direct || context is null ? context : context with { Items = EmptyItems,
                Datasets = ImmutableDictionary<string, HardwareMetricDatasetObservation>.Empty };
            History = history ?? context?.History ?? ImmutableDictionary<string, ImmutableArray<HardwareMetricHistorySample>>.Empty;
            Direct = direct;
        }
    }

    private sealed class ItemSelectionComparer : IEqualityComparer<ImmutableArray<string>>
    {
        internal static readonly ItemSelectionComparer Instance = new();
        public bool Equals(ImmutableArray<string> x, ImmutableArray<string> y)
        {
            if (x.Length != y.Length) return false;
            for (var i = 0; i < x.Length; i++) if (!StringComparer.OrdinalIgnoreCase.Equals(x[i], y[i])) return false;
            return true;
        }
        public int GetHashCode(ImmutableArray<string> ids)
        {
            var hash = new HashCode();
            foreach (var id in ids) hash.Add(id, StringComparer.OrdinalIgnoreCase);
            return hash.ToHashCode();
        }
    }
}
