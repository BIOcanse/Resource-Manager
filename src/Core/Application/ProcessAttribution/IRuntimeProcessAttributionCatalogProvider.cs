namespace ResourceManager.App.Application.ProcessAttribution;

public interface IRuntimeProcessAttributionCatalogProvider
{
    long SoftwareSnapshotGeneration { get; }

    Task<RuntimeProcessAttributionCatalogLease> AcquireCatalogAsync(CancellationToken cancellationToken);

    Task InvalidateSoftwareSnapshotAsync();
}
