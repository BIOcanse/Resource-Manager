using ResourceManager.App.Domain.Metrics;

namespace ResourceManager.App.Endpoints.Transport;

/// <summary>Field and part metadata from one committed hardware catalog.</summary>
public sealed record MetricCatalogWireSnapshot(
    string CatalogRevision,
    IReadOnlyList<MetricDeviceFieldCatalog> Fields,
    IReadOnlyList<MetricDefinition> Metrics);
