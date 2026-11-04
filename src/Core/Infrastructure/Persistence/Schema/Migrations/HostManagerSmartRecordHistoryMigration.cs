using Microsoft.Data.Sqlite;

namespace ResourceManager.App.Infrastructure.Persistence.Schema.Migrations;

internal sealed class HostManagerSmartRecordHistoryMigration : ISqliteSchemaMigration
{
    public int Version => 15;
    public string Name => "host_manager_smart_record_history";

    public async Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE host_manager_smart_record_history (
                sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                state TEXT NOT NULL CHECK(state IN ('active', 'history')),
                payload_json TEXT NOT NULL,
                recorded_utc_ticks INTEGER NOT NULL,
                restored_utc_ticks INTEGER,
                restore_result TEXT,
                ledger_committed INTEGER NOT NULL CHECK(ledger_committed IN (0, 1))
            ) STRICT;
            CREATE INDEX ix_host_manager_smart_record_history_state_sequence
                ON host_manager_smart_record_history(state, sequence DESC);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
