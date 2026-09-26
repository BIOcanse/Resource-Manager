using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Domain.Metrics;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization;

public sealed class DashboardMonitoringCatalogState
{
    private readonly object gate = new();
    private HardwareMetricSnapshot? current;
    private CatalogIdentity identity;

    public event Action? Changed;

    public HardwareMetricSnapshot? Current => Volatile.Read(ref current);

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
        var selected = request.IsAll
            ? available
            : request.Ids
                .Where(available.Contains)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (request.IncludesAllGpuCoreMetrics)
        {
            selected.UnionWith(available.Where(IsGpuCoreMetric));
        }
        return MetricSampleRequest.ForIds(selected);
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
            changed = Changed;
        }

        changed?.Invoke();
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
            || name.Equals("vramPercent", StringComparison.OrdinalIgnoreCase);
    }

    private readonly record struct CatalogIdentity(
        ulong WorkspaceIdentity,
        ulong ConfigurationGeneration,
        ulong CatalogGeneration,
        string SelectableMetricSignature)
    {
        internal static CatalogIdentity Create(HardwareMetricSnapshot snapshot)
        {
            var adapterIdentity = snapshot.Gpus
                .OrderBy(static gpu => gpu.Index)
                .Select(static gpu => $"{gpu.Index}:{gpu.IdentityKey ?? string.Empty}");
            var metricIdentity = MetricCatalog.FromSnapshot(snapshot)
                .Where(static definition => definition.Selectable)
                .OrderBy(static definition => definition.Id, StringComparer.OrdinalIgnoreCase)
                .Select(static definition =>
                    $"{definition.Id}:{definition.ScopeKind ?? string.Empty}:{definition.ScopeKey ?? string.Empty}");
            return new CatalogIdentity(
                snapshot.WorkspaceIdentity,
                snapshot.ConfigurationGeneration,
                snapshot.CatalogGeneration,
                string.Join('|', adapterIdentity.Concat(metricIdentity)));
        }
    }
}
