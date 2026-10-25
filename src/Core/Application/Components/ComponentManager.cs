using ResourceManager.App.Application.Dependencies;
using ResourceManager.App.Application.BrowserRuntimes;
using ResourceManager.App.Application.Management;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Domain.Components;
using ResourceManager.App.Domain.Metrics;
using Microsoft.Extensions.Logging;

namespace ResourceManager.App.Application.Components;

public sealed partial class ComponentManager : IComponentManager
{
    private readonly IOptionalDependencyManager dependencyManager;
    private readonly IMetricSampler metricSampler;
    private readonly IProviderRuntimeProbe providerRuntimeProbe;
    private readonly IDependencyPayloadAcquisition payloadAcquisition;
    private readonly IManagementInventoryRevisionOwner? inventoryRevisionOwner;
    private readonly IBrowserRuntimeCatalog? browserRuntimeCatalog;
    private readonly ILogger<ComponentManager>? logger;

    public ComponentManager(
        IOptionalDependencyManager dependencyManager,
        IMetricSampler metricSampler,
        IProviderRuntimeProbe providerRuntimeProbe,
        IDependencyPayloadAcquisition payloadAcquisition,
        IManagementInventoryRevisionOwner? inventoryRevisionOwner = null,
        IBrowserRuntimeCatalog? browserRuntimeCatalog = null,
        ILogger<ComponentManager>? logger = null)
    {
        this.dependencyManager = dependencyManager;
        this.metricSampler = metricSampler;
        this.providerRuntimeProbe = providerRuntimeProbe;
        this.payloadAcquisition = payloadAcquisition;
        this.inventoryRevisionOwner = inventoryRevisionOwner;
        this.browserRuntimeCatalog = browserRuntimeCatalog;
        this.logger = logger;
    }

    public async Task<IReadOnlyList<ComponentStatus>> GetStatusesAsync(CancellationToken cancellationToken)
        => await GetStatusesAsync(windowsSid: null, cancellationToken: cancellationToken);

    public async Task<IReadOnlyList<ComponentStatus>> GetStatusesAsync(
        string? windowsSid,
        CancellationToken cancellationToken)
    {
        var dependencyStatuses = await dependencyManager.GetStatusesAsync(windowsSid, cancellationToken);
        var snapshot = await metricSampler.GetSnapshotAsync(MetricSampleRequest.CatalogProbe, cancellationToken);
        var statuses = ComponentCatalog.Definitions
            .Select(definition => BuildStatus(definition, dependencyStatuses, snapshot))
            .ToArray();
        inventoryRevisionOwner?.ObserveComponents(windowsSid, statuses);
        return statuses;
    }

    public async Task<ComponentStatus?> GetStatusAsync(string id, CancellationToken cancellationToken)
        => await GetStatusAsync(id, windowsSid: null, cancellationToken: cancellationToken);

    public async Task<ComponentStatus?> GetStatusAsync(
        string id,
        string? windowsSid,
        CancellationToken cancellationToken)
    {
        var definition = ComponentCatalog.Find(id);
        if (definition is null)
        {
            return null;
        }

        var dependencyStatuses = await dependencyManager.GetStatusesAsync(windowsSid, cancellationToken);
        var snapshot = await metricSampler.GetSnapshotAsync(MetricSampleRequest.CatalogProbe, cancellationToken);
        var status = BuildStatus(definition, dependencyStatuses, snapshot);
        inventoryRevisionOwner?.ObserveComponent(windowsSid, status);
        return status;
    }
}
