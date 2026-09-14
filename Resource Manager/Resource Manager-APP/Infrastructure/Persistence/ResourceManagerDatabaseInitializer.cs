namespace ResourceManager.App.Infrastructure.Persistence;

public sealed class ResourceManagerDatabaseInitializer(
    ResourceManagerDatabase database,
    ILogger<ResourceManagerDatabaseInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await database.EnsureInitializedAsync(cancellationToken);
        var state = database.InitializationState
            ?? throw new InvalidOperationException("SQLite initialization completed without a result state.");
        if (state.Disposition == ResourceManagerDatabaseInitializationDisposition.RecoveredAfterCorruption)
        {
            logger.LogWarning(
                "Resource Manager database recovered after corruption. Database={DatabasePath}, Quarantine={QuarantinePath}.",
                state.DatabasePath,
                state.QuarantineDatabasePath);
            return;
        }

        logger.LogInformation(
            "Resource Manager database initialized at {DatabasePath} with disposition {Disposition}.",
            state.DatabasePath,
            state.Disposition);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
