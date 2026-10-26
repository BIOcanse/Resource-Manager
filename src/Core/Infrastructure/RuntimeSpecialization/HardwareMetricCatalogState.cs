using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Domain.Monitoring;
using ResourceManager.ReadWrite.Fields;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization;

public sealed class HardwareMetricCatalogState
{
    private readonly object gate = new();
    private HardwareMetricCatalog? current;
    private CatalogIdentity? identity;
    private string revision = Guid.NewGuid().ToString("N");

    public event Action? Changed;

    public HardwareMetricSnapshot? Current => Volatile.Read(ref current)?.CreateSnapshot();

    public HardwareMetricCatalog? Catalog => Volatile.Read(ref current);

    public string Revision => Volatile.Read(ref revision);

    public (HardwareMetricSnapshot? Snapshot, string Revision) ReadCommitted()
    {
        lock (gate)
        {
            return (current?.CreateSnapshot(), revision);
        }
    }

    public MetricSampleRequest SelectSubscribable(MetricSampleRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.IsCatalogProbe || request.IsEmpty)
        {
            return request;
        }

        var snapshot = Current;
        if (snapshot is null)
        {
            return request;
        }

        var available = MetricCatalog.FromSnapshot(snapshot)
            .Where(static definition => definition.Selectable)
            .Select(static definition => definition.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        // A bound field is subscribable even while its source has no current value.
        available.UnionWith(snapshot.FieldBindings.Keys);
        available.UnionWith(snapshot.FieldBindings.Values.SelectMany(static binding => binding.RequestAliases));
        available.Add(MetricDatasetIds.SystemGpuInventory);
        var selected = request.IsAll
            ? available
            : request.Ids
                .Where(available.Contains)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (request.IncludesAllGpuCoreMetrics)
        {
            selected.UnionWith(available.Where(id => snapshot.FieldBindings.TryGetValue(id, out var binding)
                ? binding.Field.Code is MetricFieldCode.GpuUsage or MetricFieldCode.GpuMemoryUsed or MetricFieldCode.GpuMemoryPercent or MetricFieldCode.GpuEngineBusy
                : IsGpuCoreMetric(id)));
        }
        var selection = MetricSampleRequest.ForIds(selected);
        return request.IncludesCatalog ? selection.WithCatalog() : selection;
    }

    public void PublishCatalogProbe(HardwareMetricSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var nextIdentity = CatalogIdentity.Create(snapshot);
        Action? changed = null;
        lock (gate)
        {
            Volatile.Write(ref current, HardwareMetricCatalog.FromSnapshot(snapshot));
            if (nextIdentity.Equals(identity))
            {
                return;
            }

            identity = nextIdentity;
            Volatile.Write(ref revision, Guid.NewGuid().ToString("N"));
            changed = Changed;
        }

        changed?.Invoke();
    }

    public bool PublishReadingMetadata(HardwareMetricSnapshot snapshot)
    {
        Action? changed;
        lock (gate)
        {
            if (current is null) return false;
            var next = MetricReadingCatalog.Update(current, snapshot);
            if (ReferenceEquals(next, current)) return false;
            Volatile.Write(ref current, next);
            identity = CatalogIdentity.Create(next.CreateSnapshot());
            Volatile.Write(ref revision, Guid.NewGuid().ToString("N"));
            changed = Changed;
        }
        changed?.Invoke();
        return true;
    }

    private static bool IsGpuCoreMetric(string metricId)
    {
        if (!metricId.StartsWith("gpu.", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        var separator = metricId.IndexOf('.', "gpu.".Length);
        if (separator <= "gpu.".Length)
        {
            return false;
        }
        var name = metricId[(separator + 1)..];
        return name.Equals("usage", StringComparison.OrdinalIgnoreCase)
            || name.Equals("vram", StringComparison.OrdinalIgnoreCase)
            || name.Equals("vramPercent", StringComparison.OrdinalIgnoreCase)
            || name.Equals("engineBusy", StringComparison.OrdinalIgnoreCase);
    }

    // Keep the compared fields typed: a hash rejects differences quickly, and a
    // matching hash still requires full equality. No per-field formatted copies.
    private sealed class CatalogIdentity : IEquatable<CatalogIdentity>
    {
        private readonly ulong workspaceIdentity;
        private readonly ulong configurationGeneration;
        private readonly ulong catalogGeneration;
        private readonly (int Index, string Identity, bool? HasDedicatedMemoryMetrics)[] adapters;
        private readonly (string Id, string ScopeKind, string ScopeKey)[] metrics;
        private readonly (string Device, string Address, string Source, MetricReadingSource? Reading)[] bindings;
        private readonly (string Address, string Alias, bool Selected)[] aliases;
        private readonly int hash;

        private CatalogIdentity(HardwareMetricSnapshot snapshot)
        {
            workspaceIdentity = snapshot.WorkspaceIdentity;
            configurationGeneration = snapshot.ConfigurationGeneration;
            catalogGeneration = snapshot.CatalogGeneration;
            var boundAddresses = snapshot.FieldBindings.Values.SelectMany(static binding => binding.RequestAliases)
                .Concat(snapshot.FieldBindings.Keys).ToHashSet(StringComparer.OrdinalIgnoreCase);
            adapters = snapshot.Gpus.OrderBy(static gpu => gpu.Index)
                .Select(static gpu => (gpu.Index, gpu.IdentityKey ?? string.Empty, gpu.HasDedicatedMemoryMetrics)).ToArray();
            metrics = MetricCatalog.FromSnapshot(snapshot)
                .Where(definition => definition.Selectable || boundAddresses.Contains(definition.Id))
                .OrderBy(static definition => definition.Id, StringComparer.OrdinalIgnoreCase)
                .Select(static definition => (definition.Id, definition.ScopeKind ?? string.Empty,
                    definition.ScopeKey ?? string.Empty)).ToArray();
            bindings = snapshot.FieldBindings.Values
                .OrderBy(static binding => binding.RequestAddress, StringComparer.Ordinal)
                .Select(static binding => (binding.DeviceIdentity, binding.RequestAddress,
                    binding.SourceId, binding.Field.Reading)).ToArray();
            aliases = snapshot.FieldBindings.Values.SelectMany(static binding =>
                    binding.RequestAliases.Select(alias => (binding.RequestAddress, Alias: alias, Selected: false))
                        .Concat(binding.SelectedRequestAliases.Select(alias => (binding.RequestAddress, Alias: alias, Selected: true))))
                .OrderBy(static alias => alias.RequestAddress, StringComparer.Ordinal)
                .ThenBy(static alias => alias.Alias, StringComparer.Ordinal)
                .ThenBy(static alias => alias.Selected).ToArray();
            var builder = new HashCode();
            builder.Add(workspaceIdentity);
            builder.Add(configurationGeneration);
            builder.Add(catalogGeneration);
            builder.Add(adapters.Length);
            foreach (var adapter in adapters) builder.Add(adapter);
            builder.Add(metrics.Length);
            foreach (var metric in metrics) builder.Add(metric);
            builder.Add(bindings.Length);
            foreach (var binding in bindings) builder.Add(binding);
            builder.Add(aliases.Length);
            foreach (var alias in aliases) builder.Add(alias);
            hash = builder.ToHashCode();
        }

        internal static CatalogIdentity Create(HardwareMetricSnapshot snapshot) => new(snapshot);

        public bool Equals(CatalogIdentity? other)
            => other is not null && hash == other.hash
                && workspaceIdentity == other.workspaceIdentity
                && configurationGeneration == other.configurationGeneration
                && catalogGeneration == other.catalogGeneration
                && adapters.AsSpan().SequenceEqual(other.adapters)
                && metrics.AsSpan().SequenceEqual(other.metrics)
                && bindings.AsSpan().SequenceEqual(other.bindings)
                && aliases.AsSpan().SequenceEqual(other.aliases);

        public override bool Equals(object? obj) => obj is CatalogIdentity other && Equals(other);
        public override int GetHashCode() => hash;
    }
}
