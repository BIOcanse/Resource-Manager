using Microsoft.Data.Sqlite;

namespace ResourceManager.App.Infrastructure.Persistence.Schema.Migrations;

internal sealed class OptimizeSoftwareFileIndexFtsTriggersMigration : ISqliteSchemaMigration
{
    public int Version => 7;

    public string Name => "optimize-software-file-index-fts-triggers";

    public async Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DROP TRIGGER IF EXISTS software_index_entries_au;
            CREATE TRIGGER software_index_entries_au
            AFTER UPDATE OF file_name, relative_path ON software_index_entries
            WHEN old.file_name <> new.file_name OR old.relative_path <> new.relative_path
            BEGIN
                INSERT INTO software_file_name_fts(software_file_name_fts, rowid, file_name)
                VALUES ('delete', old.id, old.file_name);
                INSERT INTO software_file_path_fts(software_file_path_fts, rowid, relative_path)
                VALUES ('delete', old.id, old.relative_path);
                INSERT INTO software_file_name_fts(rowid, file_name) VALUES (new.id, new.file_name);
                INSERT INTO software_file_path_fts(rowid, relative_path) VALUES (new.id, new.relative_path);
            END;

            DROP TRIGGER IF EXISTS software_index_roots_au;
            CREATE TRIGGER software_index_roots_au
            AFTER UPDATE OF software_name ON software_index_roots
            WHEN old.software_name <> new.software_name
            BEGIN
                INSERT INTO software_root_search_fts(software_root_search_fts, rowid, software_name)
                VALUES ('delete', old.id, old.software_name);
                INSERT INTO software_root_search_fts(rowid, software_name) VALUES (new.id, new.software_name);
            END;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
