using ResourceManager.App.Application.Dependencies;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Domain.Components;
using ResourceManager.App.Domain.Metrics;

namespace ResourceManager.App.Application.Components;

public sealed partial class ComponentManager : IComponentManager
{
    private readonly IOptionalDependencyManager dependencyManager;
    private readonly IMetricSampler metricSampler;
    private readonly IProviderRuntimeProbe providerRuntimeProbe;

    public ComponentManager(
        IOptionalDependencyManager dependencyManager,
        IMetricSampler metricSampler,
        IProviderRuntimeProbe providerRuntimeProbe)
    {
        this.dependencyManager = dependencyManager;
        this.metricSampler = metricSampler;
        this.providerRuntimeProbe = providerRuntimeProbe;
    }

    public async Task<IReadOnlyList<ComponentStatus>> GetStatusesAsync(CancellationToken cancellationToken)
    {
        var dependencyStatuses = await dependencyManager.GetStatusesAsync(cancellationToken);
        var snapshot = await metricSampler.GetSnapshotAsync(MetricSampleRequest.CatalogProbe, cancellationToken);
        return ComponentCatalog.Definitions
            .Select(definition => BuildStatus(definition, dependencyStatuses, snapshot))
            .OrderBy(static item => item.Definition.Category, StringComparer.Ordinal)
            .ThenBy(static item => item.Definition.Name, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<ComponentStatus?> GetStatusAsync(string id, CancellationToken cancellationToken)
    {
        var definition = ComponentCatalog.Find(id);
        if (definition is null)
        {
            return null;
        }

        var dependencyStatuses = await dependencyManager.GetStatusesAsync(cancellationToken);
        var snapshot = await metricSampler.GetSnapshotAsync(MetricSampleRequest.CatalogProbe, cancellationToken);
        return BuildStatus(definition, dependencyStatuses, snapshot);
    }
}
