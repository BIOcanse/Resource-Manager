using Microsoft.Data.Sqlite;
using ResourceManager.App.Domain.Indexing;

namespace ResourceManager.App.Infrastructure.Indexing;

public sealed partial class SqliteSoftwareFileIndex
{
    private async Task RefreshRootAsync(
        SoftwareFileIndexRequest request,
        SoftwareFileIndexRoot root,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(root.Path))
        {
            throw new DirectoryNotFoundException($"Software index root is unavailable: {root.Path}");
        }

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await PrepareStagingTableAsync(connection, cancellationToken);
        await using var stagingCommand = CreateStagingBatchCommand(connection);
        await stagingCommand.PrepareAsync(cancellationToken);

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false
        };
        var stagedFiles = new List<StagedFileCandidate>(StagingBatchSize);
        foreach (var filePath in Directory.EnumerateFiles(root.Path, "*", options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = Path.GetRelativePath(root.Path, filePath);
            var relativePathKey = PathKey(relativePath);

            try
            {
                var info = new FileInfo(filePath);
                if (!info.Exists)
                {
                    stagedFiles.Add(StagedFileCandidate.Unavailable(relativePath, relativePathKey));
                }
                else
                {
                    stagedFiles.Add(new StagedFileCandidate(
                        relativePath,
                        relativePathKey,
                        info.Name,
                        info.Extension,
                        info.Length,
                        info.LastWriteTimeUtc.Ticks,
                        (long)info.Attributes,
                        true));
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException
                or IOException
                or PathTooLongException
                or NotSupportedException)
            {
                stagedFiles.Add(StagedFileCandidate.Unavailable(relativePath, relativePathKey));
            }

            if (stagedFiles.Count >= StagingBatchSize)
            {
                await FlushStagingBatchAsync(stagingCommand, stagedFiles, cancellationToken);
                stagedFiles.Clear();
            }
        }

        await FlushStagingBatchAsync(stagingCommand, stagedFiles, cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var rootId = await UpsertRootAsync(connection, transaction, request, root, cancellationToken);
        await MergeStagedEntriesAsync(connection, transaction, rootId, cancellationToken);
        await DeleteMissingEntriesAsync(connection, transaction, rootId, cancellationToken);
        await UpdateRootSummaryAsync(connection, transaction, rootId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task<long> UpsertRootAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SoftwareFileIndexRequest request,
        SoftwareFileIndexRoot root,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO software_index_roots(
                software_id,
                software_name,
                root_path,
                root_path_key,
                root_kind)
            VALUES ($softwareId, $softwareName, $rootPath, $rootPathKey, $rootKind)
            ON CONFLICT(software_id, root_path_key) DO UPDATE SET
                software_name = excluded.software_name,
                root_path = excluded.root_path,
                root_kind = excluded.root_kind
            RETURNING id;
            """;
        command.Parameters.AddWithValue("$softwareId", request.SoftwareId);
        command.Parameters.AddWithValue("$softwareName", request.SoftwareName);
        command.Parameters.AddWithValue("$rootPath", root.Path);
        command.Parameters.AddWithValue("$rootPathKey", PathKey(root.Path));
        command.Parameters.AddWithValue("$rootKind", root.Kind);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task DeleteMissingEntriesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long rootId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM software_index_entries
            WHERE root_id = $rootId
              AND NOT EXISTS (
                  SELECT 1
                  FROM temp.current_index_stage staged
                  WHERE staged.relative_path_key = software_index_entries.relative_path_key
              );
            """;
        command.Parameters.AddWithValue("$rootId", rootId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpdateRootSummaryAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long rootId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE software_index_roots
            SET total_bytes = COALESCE((
                    SELECT SUM(size_bytes)
                    FROM software_index_entries
                    WHERE root_id = $rootId), 0),
                file_count = (
                    SELECT COUNT(*)
                    FROM software_index_entries
                    WHERE root_id = $rootId),
                last_indexed_utc_ticks = $indexedAt
            WHERE id = $rootId;
            """;
        command.Parameters.AddWithValue("$rootId", rootId);
        command.Parameters.AddWithValue("$indexedAt", DateTimeOffset.UtcNow.UtcTicks);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task RemoveStaleRootsAsync(
        string softwareId,
        IReadOnlyList<SoftwareFileIndexRoot> currentRoots,
        CancellationToken cancellationToken)
    {
        var currentKeys = currentRoots.Select(static root => PathKey(root.Path)).ToHashSet(StringComparer.Ordinal);
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var staleIds = new List<long>();
        await using (var readCommand = connection.CreateCommand())
        {
            readCommand.Transaction = transaction;
            readCommand.CommandText = "SELECT id, root_path_key FROM software_index_roots WHERE software_id = $softwareId;";
            readCommand.Parameters.AddWithValue("$softwareId", softwareId);
            await using var reader = await readCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (!currentKeys.Contains(reader.GetString(1)))
                {
                    staleIds.Add(reader.GetInt64(0));
                }
            }
        }

        await using var deleteCommand = connection.CreateCommand();
        deleteCommand.Transaction = transaction;
        deleteCommand.CommandText = "DELETE FROM software_index_roots WHERE id = $id;";
        var idParameter = deleteCommand.Parameters.Add("$id", SqliteType.Integer);
        foreach (var staleId in staleIds)
        {
            idParameter.Value = staleId;
            await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }
}
