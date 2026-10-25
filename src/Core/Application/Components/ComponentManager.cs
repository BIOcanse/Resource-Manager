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
        var observedRevision = inventoryRevisionOwner?.Current.ComponentRevision;
        var dependencyStatuses = await dependencyManager.GetStatusesAsync(windowsSid, cancellationToken);
        var snapshot = await metricSampler.GetSnapshotAsync(MetricSampleRequest.CatalogProbe, cancellationToken);
        var statuses = ComponentCatalog.Definitions
            .Select(definition => BuildStatus(definition, dependencyStatuses, snapshot))
            .ToArray();
        inventoryRevisionOwner?.ObserveComponents(windowsSid, statuses, observedRevision);
        return statuses;
    }

    public async Task<ComponentStatus?> GetStatusAsync(string id, CancellationToken cancellationToken)
        => await GetStatusAsync(id, windowsSid: null, cancellationToken: cancellationToken);

    public async Task<ComponentStatus?> GetStatusAsync(
        string id,
        string? windowsSid,
        CancellationToken cancellationToken)
        => await ReadStatusAsync(id, windowsSid, cancellationToken);

    private async Task<ComponentStatus?> ReadStatusAsync(
        string id,
        string? windowsSid,
        CancellationToken cancellationToken,
        bool contentChanged = false)
    {
        var definition = ComponentCatalog.Find(id);
        if (definition is null)
        {
            return null;
        }

        var observedRevision = inventoryRevisionOwner?.Current.ComponentRevision;
        ComponentStatus status;
        try
        {
            // Once bytes are committed, cancellation cannot abandon their revision publication.
            var readToken = contentChanged ? CancellationToken.None : cancellationToken;
            var dependencyStatuses = await dependencyManager.GetStatusesAsync(windowsSid, readToken);
            var snapshot = await metricSampler.GetSnapshotAsync(MetricSampleRequest.CatalogProbe, readToken);
            status = BuildStatus(definition, dependencyStatuses, snapshot);
        }
        catch when (contentChanged)
        {
            inventoryRevisionOwner?.ObserveComponentContentChanged(id);
            throw;
        }
        if (contentChanged)
            inventoryRevisionOwner?.ObserveComponentContentChanged(windowsSid, status);
        else
            inventoryRevisionOwner?.ObserveComponent(windowsSid, status, observedRevision);
        return status;
    }
}
