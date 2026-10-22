using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.ReadWrite.Fields;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization;

public sealed class DashboardMonitoringCatalogState
{
    private readonly object gate = new();
    private HardwareMetricSnapshot? current;
    private CatalogIdentity identity;
    private string revision = Guid.NewGuid().ToString("N");

    public event Action? Changed;

    public HardwareMetricSnapshot? Current => Volatile.Read(ref current);

    public string Revision => Volatile.Read(ref revision);

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
            Volatile.Write(ref current, snapshot);
            if (nextIdentity == identity)
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
            identity = CatalogIdentity.Create(next);
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

    private readonly record struct CatalogIdentity(
        ulong WorkspaceIdentity,
        ulong ConfigurationGeneration,
        ulong CatalogGeneration,
        string SelectableMetricSignature)
    {
        internal static CatalogIdentity Create(HardwareMetricSnapshot snapshot)
        {
            var boundAddresses = snapshot.FieldBindings.Values.SelectMany(static binding => binding.RequestAliases)
                .Concat(snapshot.FieldBindings.Keys).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var adapterIdentity = snapshot.Gpus
                .OrderBy(static gpu => gpu.Index)
                .Select(static gpu => $"{gpu.Index}:{gpu.IdentityKey ?? string.Empty}");
            var metricIdentity = MetricCatalog.FromSnapshot(snapshot)
                .Where(definition => definition.Selectable || boundAddresses.Contains(definition.Id))
                .OrderBy(static definition => definition.Id, StringComparer.OrdinalIgnoreCase)
                .Select(static definition =>
                    $"{definition.Id}:{definition.ScopeKind ?? string.Empty}:{definition.ScopeKey ?? string.Empty}");
            return new CatalogIdentity(
                snapshot.WorkspaceIdentity,
                snapshot.ConfigurationGeneration,
                snapshot.CatalogGeneration,
                string.Join('|', adapterIdentity.Concat(metricIdentity).Concat(snapshot.FieldBindings.Values
                    .OrderBy(static binding => binding.RequestAddress, StringComparer.Ordinal)
                    .Select(static binding => $"{binding.DeviceIdentity}:{binding.RequestAddress}:{binding.SourceId}:{binding.Field.Reading}"))));
        }
    }
}
