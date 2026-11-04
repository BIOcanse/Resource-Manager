using Microsoft.Data.Sqlite;

namespace ResourceManager.App.Infrastructure.Persistence.Schema.Migrations;

internal sealed class PortableSoftwareRegistryMigration : ISqliteSchemaMigration
{
    public int Version => 8;

    public string Name => "portable-software-registry";

    public async Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE portable_software_executables (
                software_id TEXT NOT NULL,
                catalog_entry_id TEXT NOT NULL,
                display_name TEXT NOT NULL,
                kind TEXT NOT NULL,
                executable_path TEXT NOT NULL,
                root_path TEXT NOT NULL,
                first_observed_utc_ticks INTEGER NOT NULL,
                PRIMARY KEY (software_id, executable_path)
            ) STRICT;

            CREATE INDEX ix_portable_software_executables_software
                ON portable_software_executables(software_id);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
