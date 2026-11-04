using ResourceManager.App.Domain.PublicServices.Sqlite;

namespace ResourceManager.App.Application.PublicServices.Sqlite;

public interface IPublicSqliteDatabaseService
{
    Task<IReadOnlyList<PublicSqliteDatabaseInfo>> ListAsync(CancellationToken cancellationToken);

    Task<PublicSqliteDatabaseInfo?> GetAsync(string databaseId, CancellationToken cancellationToken);

    Task<PublicSqliteDatabaseInfo> CreateAsync(string databaseId, CancellationToken cancellationToken);

    Task<PublicSqliteCommandResult> QueryAsync(
        string databaseId,
        PublicSqliteCommandRequest request,
        CancellationToken cancellationToken);

    Task<PublicSqliteCommandResult> ExecuteAsync(
        string databaseId,
        PublicSqliteCommandRequest request,
        CancellationToken cancellationToken);

    Task<PublicSqliteBatchResult> BatchAsync(
        string databaseId,
        PublicSqliteBatchRequest request,
        CancellationToken cancellationToken);

    Task<PublicSqliteCheckpointResult> CheckpointAsync(
        string databaseId,
        CancellationToken cancellationToken);
}
