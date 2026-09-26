using Microsoft.Data.Sqlite;
using ResourceManager.App.Infrastructure.Persistence.Schema.Migrations;

namespace ResourceManager.App.Infrastructure.Persistence.Schema;

internal static class SqliteSchemaMigrator
{
    private static readonly ISqliteSchemaMigration[] Migrations =
    [
        new InitialSqliteSchemaMigration(),
        new CompactSoftwareFileIndexMigration(),
        new ScheduleFileIndexCompactionMigration(),
        new SchedulePostVacuumCheckpointMigration(),
        new NormalizeOptimizationObservationKeysMigration(),
        new AiGatewayCredentialsMigration(),
        new OptimizeSoftwareFileIndexFtsTriggersMigration(),
        new PortableSoftwareRegistryMigration(),
        new PortableSoftwareConfirmationMigration(),
        new HostManagerReportCoordinatorMigration(),
        new NormalizePortableSoftwareObservationPrecisionMigration()
    ];

    public static async Task MigrateAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await CreateMigrationTableAsync(connection, cancellationToken);
        var applied = await ReadAppliedVersionsAsync(connection, cancellationToken);
        foreach (var migration in Migrations.OrderBy(static item => item.Version))
        {
            if (applied.Contains(migration.Version))
            {
                continue;
            }

            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await migration.ApplyAsync(connection, transaction, cancellationToken);

            await using var recordCommand = connection.CreateCommand();
            recordCommand.Transaction = transaction;
            recordCommand.CommandText = """
                INSERT INTO schema_migrations(version, name, applied_utc_ticks)
                VALUES ($version, $name, $appliedAt);
                """;
            recordCommand.Parameters.AddWithValue("$version", migration.Version);
            recordCommand.Parameters.AddWithValue("$name", migration.Name);
            recordCommand.Parameters.AddWithValue("$appliedAt", DateTimeOffset.UtcNow.UtcTicks);
            await recordCommand.ExecuteNonQueryAsync(cancellationToken);

            await using var versionCommand = connection.CreateCommand();
            versionCommand.Transaction = transaction;
            versionCommand.CommandText = $"PRAGMA user_version={migration.Version};";
            await versionCommand.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
    }

    private static async Task CreateMigrationTableAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS schema_migrations (
                version INTEGER NOT NULL PRIMARY KEY,
                name TEXT NOT NULL,
                applied_utc_ticks INTEGER NOT NULL
            ) STRICT;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<HashSet<int>> ReadAppliedVersionsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var result = new HashSet<int>();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT version FROM schema_migrations;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(reader.GetInt32(0));
        }

        return result;
    }
}
