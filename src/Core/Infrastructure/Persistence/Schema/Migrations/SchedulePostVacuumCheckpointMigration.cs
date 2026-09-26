using Microsoft.Data.Sqlite;

namespace ResourceManager.App.Infrastructure.Persistence.Schema.Migrations;

internal sealed class SchedulePostVacuumCheckpointMigration : ISqliteSchemaMigration
{
    public int Version => 4;

    public string Name => "schedule-post-vacuum-checkpoint";

    public async Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO storage_metadata(key, value, updated_utc_ticks)
            VALUES ($key, 'pending', $updatedAt)
            ON CONFLICT(key) DO UPDATE SET
                value = excluded.value,
                updated_utc_ticks = excluded.updated_utc_ticks;
            """;
        command.Parameters.AddWithValue("$key", ScheduleFileIndexCompactionMigration.MaintenanceKey);
        command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.UtcTicks);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
