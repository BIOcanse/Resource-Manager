namespace ResourceManager.App.Infrastructure.Persistence.Legacy;

internal interface ISqliteLegacyDataImporter
{
    string Name { get; }

    Task ImportAsync(CancellationToken cancellationToken);
}
