using System.Text;
using Microsoft.Data.Sqlite;

namespace ResourceManager.App.Infrastructure.Indexing;

public sealed partial class SqliteSoftwareFileIndex
{
    private const int StagingBatchSize = 96;

    private static async Task PrepareStagingTableAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TEMP TABLE IF NOT EXISTS current_index_stage (
                relative_path_key TEXT NOT NULL PRIMARY KEY,
                relative_path TEXT,
                file_name TEXT,
                extension TEXT,
                size_bytes INTEGER,
                last_write_utc_ticks INTEGER,
                attributes INTEGER,
                metadata_available INTEGER NOT NULL
            ) WITHOUT ROWID;
            DELETE FROM temp.current_index_stage;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static SqliteCommand CreateStagingBatchCommand(SqliteConnection connection)
    {
        var command = connection.CreateCommand();
        var sql = new StringBuilder("""
            INSERT INTO temp.current_index_stage(
                relative_path_key,
                relative_path,
                file_name,
                extension,
                size_bytes,
                last_write_utc_ticks,
                attributes,
                metadata_available)
            VALUES
            """);
        for (var index = 0; index < StagingBatchSize; index++)
        {
            if (index > 0)
            {
                sql.Append(',');
            }

            sql.Append($"($key{index},$path{index},$name{index},$extension{index},$size{index},$write{index},$attributes{index},$available{index})");
            command.Parameters.Add($"$key{index}", SqliteType.Text);
            command.Parameters.Add($"$path{index}", SqliteType.Text);
            command.Parameters.Add($"$name{index}", SqliteType.Text);
            command.Parameters.Add($"$extension{index}", SqliteType.Text);
            command.Parameters.Add($"$size{index}", SqliteType.Integer);
            command.Parameters.Add($"$write{index}", SqliteType.Integer);
            command.Parameters.Add($"$attributes{index}", SqliteType.Integer);
            command.Parameters.Add($"$available{index}", SqliteType.Integer);
        }

        sql.Append("""
            ON CONFLICT(relative_path_key) DO UPDATE SET
                relative_path = excluded.relative_path,
                file_name = excluded.file_name,
                extension = excluded.extension,
                size_bytes = excluded.size_bytes,
                last_write_utc_ticks = excluded.last_write_utc_ticks,
                attributes = excluded.attributes,
                metadata_available = excluded.metadata_available;
            """);
        command.CommandText = sql.ToString();
        return command;
    }

    private static async Task FlushStagingBatchAsync(
        SqliteCommand command,
        IReadOnlyList<StagedFileCandidate> files,
        CancellationToken cancellationToken)
    {
        if (files.Count == 0)
        {
            return;
        }

        for (var index = 0; index < StagingBatchSize; index++)
        {
            if (index < files.Count)
            {
                SetStagingParameters(command, index, files[index]);
                continue;
            }

            command.Parameters[$"$key{index}"].Value = $"\u0001resource-manager-unused-{index}";
            command.Parameters[$"$path{index}"].Value = string.Empty;
            command.Parameters[$"$name{index}"].Value = DBNull.Value;
            command.Parameters[$"$extension{index}"].Value = DBNull.Value;
            command.Parameters[$"$size{index}"].Value = DBNull.Value;
            command.Parameters[$"$write{index}"].Value = DBNull.Value;
            command.Parameters[$"$attributes{index}"].Value = DBNull.Value;
            command.Parameters[$"$available{index}"].Value = 0;
        }

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void SetStagingParameters(
        SqliteCommand command,
        int index,
        StagedFileCandidate file)
    {
        command.Parameters[$"$key{index}"].Value = file.RelativePathKey;
        command.Parameters[$"$path{index}"].Value = file.RelativePath;
        command.Parameters[$"$name{index}"].Value = DbValue(file.FileName);
        command.Parameters[$"$extension{index}"].Value = DbValue(file.Extension);
        command.Parameters[$"$size{index}"].Value = DbValue(file.SizeBytes);
        command.Parameters[$"$write{index}"].Value = DbValue(file.LastWriteUtcTicks);
        command.Parameters[$"$attributes{index}"].Value = DbValue(file.Attributes);
        command.Parameters[$"$available{index}"].Value = file.MetadataAvailable ? 1 : 0;
    }

    private static async Task MergeStagedEntriesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long rootId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO software_index_entries(
                root_id,
                relative_path,
                relative_path_key,
                file_name,
                extension,
                size_bytes,
                last_write_utc_ticks,
                attributes)
            SELECT
                $rootId,
                relative_path,
                relative_path_key,
                file_name,
                extension,
                size_bytes,
                last_write_utc_ticks,
                attributes
            FROM temp.current_index_stage
            WHERE metadata_available = 1
            ON CONFLICT(root_id, relative_path_key) DO UPDATE SET
                relative_path = excluded.relative_path,
                file_name = excluded.file_name,
                extension = excluded.extension,
                size_bytes = excluded.size_bytes,
                last_write_utc_ticks = excluded.last_write_utc_ticks,
                attributes = excluded.attributes
            WHERE software_index_entries.relative_path <> excluded.relative_path
               OR software_index_entries.file_name <> excluded.file_name
               OR software_index_entries.extension <> excluded.extension
               OR software_index_entries.size_bytes <> excluded.size_bytes
               OR software_index_entries.last_write_utc_ticks <> excluded.last_write_utc_ticks
               OR software_index_entries.attributes <> excluded.attributes;
            """;
        command.Parameters.AddWithValue("$rootId", rootId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static object DbValue(object? value)
    {
        return value ?? DBNull.Value;
    }

    private sealed record StagedFileCandidate(
        string RelativePath,
        string RelativePathKey,
        string? FileName,
        string? Extension,
        long? SizeBytes,
        long? LastWriteUtcTicks,
        long? Attributes,
        bool MetadataAvailable)
    {
        public static StagedFileCandidate Unavailable(string relativePath, string relativePathKey)
        {
            return new StagedFileCandidate(
                relativePath,
                relativePathKey,
                null,
                null,
                null,
                null,
                null,
                false);
        }
    }
}
