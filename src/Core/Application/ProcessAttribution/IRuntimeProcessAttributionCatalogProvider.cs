namespace ResourceManager.App.Application.ProcessAttribution;

public interface IRuntimeProcessAttributionCatalogProvider
{
    long SoftwareSnapshotGeneration { get; }

    Task<RuntimeProcessAttributionCatalogLease> AcquireCatalogAsync(CancellationToken cancellationToken)
        => AcquireCatalogAsync(null, cancellationToken);

    Task<RuntimeProcessAttributionCatalogLease> AcquireCatalogAsync(string? windowsSid, CancellationToken cancellationToken);

    Task InvalidateSoftwareSnapshotAsync();
}
