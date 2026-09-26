using Microsoft.Data.Sqlite;
using ResourceManager.App.Infrastructure.Persistence.Schema.Migrations;

namespace ResourceManager.App.Infrastructure.Persistence;

internal static class SqliteDatabaseMaintenance
{
    private const long MinimumDatabaseBytesForVacuum = 32L * 1024 * 1024;
    private const double MinimumFreePageRatioForVacuum = 0.10;

    public static async Task RunPendingAsync(
        SqliteConnection connection,
        string databasePath,
        CancellationToken cancellationToken)
    {
        if (!await IsPendingAsync(connection, cancellationToken))
        {
            return;
        }

        await ExecuteAsync(connection, "PRAGMA wal_checkpoint(TRUNCATE);", cancellationToken);
        var pageCount = await ReadPragmaInt64Async(connection, "page_count", cancellationToken);
        var freePageCount = await ReadPragmaInt64Async(connection, "freelist_count", cancellationToken);
        var databaseLength = File.Exists(databasePath) ? new FileInfo(databasePath).Length : 0;
        if (databaseLength >= MinimumDatabaseBytesForVacuum
            && pageCount > 0
            && freePageCount / (double)pageCount >= MinimumFreePageRatioForVacuum)
        {
            await ExecuteAsync(connection, "VACUUM;", cancellationToken);
        }

        await ExecuteAsync(connection, "PRAGMA optimize;", cancellationToken);
        await ExecuteAsync(connection, "PRAGMA wal_checkpoint(TRUNCATE);", cancellationToken);
        await using var completeCommand = connection.CreateCommand();
        completeCommand.CommandText = "DELETE FROM storage_metadata WHERE key = $key;";
        completeCommand.Parameters.AddWithValue("$key", ScheduleFileIndexCompactionMigration.MaintenanceKey);
        await completeCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> IsPendingAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM storage_metadata WHERE key = $key LIMIT 1;";
        command.Parameters.AddWithValue("$key", ScheduleFileIndexCompactionMigration.MaintenanceKey);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task<long> ReadPragmaInt64Async(
        SqliteConnection connection,
        string pragma,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA {pragma};";
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
