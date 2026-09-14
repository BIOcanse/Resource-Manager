using Microsoft.Data.Sqlite;

namespace ResourceManager.App.Infrastructure.Persistence.Schema.Migrations;

internal sealed class NormalizeOptimizationObservationKeysMigration : ISqliteSchemaMigration
{
    public int Version => 5;

    public string Name => "normalize-optimization-observation-keys";

    public async Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        if (await HasNoCaseKeyAsync(connection, transaction, cancellationToken))
        {
            return;
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DROP INDEX IF EXISTS ix_optimization_observations_last_observed;
            DROP INDEX IF EXISTS ix_optimization_observations_target;
            DROP INDEX IF EXISTS ix_optimization_observations_type;
            ALTER TABLE optimization_observations RENAME TO optimization_observations_v4;

            CREATE TABLE optimization_observations (
                key TEXT NOT NULL COLLATE NOCASE PRIMARY KEY,
                report_id TEXT NOT NULL,
                type TEXT NOT NULL,
                target_type TEXT NOT NULL,
                target_key TEXT NOT NULL,
                software_id TEXT,
                last_observed_utc_ticks INTEGER NOT NULL,
                payload_json TEXT NOT NULL
            ) STRICT;

            INSERT INTO optimization_observations(
                key,
                report_id,
                type,
                target_type,
                target_key,
                software_id,
                last_observed_utc_ticks,
                payload_json)
            SELECT
                key,
                report_id,
                type,
                target_type,
                target_key,
                software_id,
                last_observed_utc_ticks,
                payload_json
            FROM (
                SELECT *, ROW_NUMBER() OVER (
                    PARTITION BY key COLLATE NOCASE
                    ORDER BY last_observed_utc_ticks DESC) AS key_rank
                FROM optimization_observations_v4
            )
            WHERE key_rank = 1;

            DROP TABLE optimization_observations_v4;
            CREATE INDEX ix_optimization_observations_last_observed
                ON optimization_observations(last_observed_utc_ticks DESC);
            CREATE INDEX ix_optimization_observations_target
                ON optimization_observations(target_type, target_key);
            CREATE INDEX ix_optimization_observations_type
                ON optimization_observations(type);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> HasNoCaseKeyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'optimization_observations';";
        var sql = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken));
        return sql?.Contains("key TEXT NOT NULL COLLATE NOCASE", StringComparison.OrdinalIgnoreCase) == true;
    }
}
