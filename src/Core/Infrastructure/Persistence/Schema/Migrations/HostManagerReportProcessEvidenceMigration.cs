using Microsoft.Data.Sqlite;

namespace ResourceManager.App.Infrastructure.Persistence.Schema.Migrations;

internal sealed class HostManagerReportProcessEvidenceMigration : ISqliteSchemaMigration
{
    public int Version => 14;
    public string Name => "host_manager_report_process_evidence";

    public async Task ApplyAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "ALTER TABLE host_manager_report_target_identities ADD COLUMN process_evidence TEXT;";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
