namespace ResourceManager.App.Infrastructure.Persistence.Legacy;

internal sealed class SqliteLegacyDataImportHostedService(
    IEnumerable<ISqliteLegacyDataImporter> importers,
    ILogger<SqliteLegacyDataImportHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var importer in importers)
        {
            try
            {
                await importer.ImportAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Legacy data importer {ImporterName} failed.", importer.Name);
                throw;
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
