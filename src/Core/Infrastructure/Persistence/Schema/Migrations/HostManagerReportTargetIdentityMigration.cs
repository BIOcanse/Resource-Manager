using Microsoft.Data.Sqlite;

namespace ResourceManager.App.Infrastructure.Persistence.Schema.Migrations;

internal sealed class HostManagerReportTargetIdentityMigration : ISqliteSchemaMigration
{
    public int Version => 13;
    public string Name => "host-manager-report-target-identities";

    public async Task ApplyAsync(SqliteConnection connection, SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE host_manager_report_target_identities (
                target_handle INTEGER NOT NULL PRIMARY KEY,
                software_id TEXT NOT NULL,
                software_name TEXT NOT NULL,
                software_kind TEXT NOT NULL,
                display_kind TEXT NOT NULL
            ) WITHOUT ROWID, STRICT;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
