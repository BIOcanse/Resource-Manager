using Microsoft.Data.Sqlite;

namespace ResourceManager.App.Infrastructure.Persistence.Schema.Migrations;

internal sealed class CompactSoftwareFileIndexMigration : ISqliteSchemaMigration
{
    public int Version => 2;

    public string Name => "compact-file-index-search";

    public async Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        if (!await HasLegacyEntryColumnsAsync(connection, transaction, cancellationToken))
        {
            return;
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DROP TRIGGER IF EXISTS software_index_entries_ai;
            DROP TRIGGER IF EXISTS software_index_entries_ad;
            DROP TRIGGER IF EXISTS software_index_entries_au;
            DROP TRIGGER IF EXISTS software_index_roots_ai;
            DROP TRIGGER IF EXISTS software_index_roots_ad;
            DROP TRIGGER IF EXISTS software_index_roots_au;
            DROP TABLE IF EXISTS software_file_search_fts;
            DROP TABLE IF EXISTS software_file_name_fts;
            DROP TABLE IF EXISTS software_file_path_fts;
            DROP TABLE IF EXISTS software_root_search_fts;
            DROP INDEX IF EXISTS ix_software_index_entries_root;
            DROP INDEX IF EXISTS ix_software_index_entries_software;
            DROP INDEX IF EXISTS ix_software_index_entries_extension;

            ALTER TABLE software_index_entries RENAME TO software_index_entries_v1;
            """ + SoftwareFileIndexSchemaSql.Current + """

            INSERT INTO software_index_entries(
                id,
                root_id,
                relative_path,
                relative_path_key,
                file_name,
                extension,
                size_bytes,
                last_write_utc_ticks,
                attributes)
            SELECT
                id,
                root_id,
                relative_path,
                relative_path_key,
                file_name,
                extension,
                size_bytes,
                last_write_utc_ticks,
                attributes
            FROM software_index_entries_v1;

            DROP TABLE software_index_entries_v1;
            INSERT INTO software_file_name_fts(software_file_name_fts) VALUES ('rebuild');
            INSERT INTO software_file_path_fts(software_file_path_fts) VALUES ('rebuild');
            INSERT INTO software_root_search_fts(software_root_search_fts) VALUES ('rebuild');
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> HasLegacyEntryColumnsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA table_info(software_index_entries);";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (reader.GetString(1).Equals("software_id", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
