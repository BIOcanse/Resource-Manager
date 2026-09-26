using Microsoft.Data.Sqlite;

namespace ResourceManager.App.Infrastructure.Persistence.Schema.Migrations;

internal sealed class InitialSqliteSchemaMigration : ISqliteSchemaMigration
{
    public int Version => 1;

    public string Name => "initial-dynamic-data-and-file-index";

    public async Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE storage_metadata (
                key TEXT NOT NULL COLLATE NOCASE PRIMARY KEY,
                value TEXT NOT NULL,
                updated_utc_ticks INTEGER NOT NULL
            ) STRICT;

            CREATE TABLE indexed_volumes (
                volume_key TEXT NOT NULL PRIMARY KEY,
                root_path TEXT NOT NULL,
                file_system TEXT,
                volume_serial TEXT,
                journal_id TEXT,
                next_usn INTEGER,
                last_full_index_utc_ticks INTEGER,
                last_incremental_index_utc_ticks INTEGER
            ) STRICT;

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

            CREATE INDEX ix_optimization_observations_last_observed
                ON optimization_observations(last_observed_utc_ticks DESC);
            CREATE INDEX ix_optimization_observations_target
                ON optimization_observations(target_type, target_key);
            CREATE INDEX ix_optimization_observations_type
                ON optimization_observations(type);

            """ + SoftwareFileIndexSchemaSql.Current;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
