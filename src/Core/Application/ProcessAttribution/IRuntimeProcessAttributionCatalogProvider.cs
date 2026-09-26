namespace ResourceManager.App.Application.ProcessAttribution;

public interface IRuntimeProcessAttributionCatalogProvider
{
    long SoftwareSnapshotGeneration { get; }

    Task<RuntimeProcessAttributionCatalog> GetCatalogAsync(CancellationToken cancellationToken);

    Task InvalidateSoftwareSnapshotAsync();
}
