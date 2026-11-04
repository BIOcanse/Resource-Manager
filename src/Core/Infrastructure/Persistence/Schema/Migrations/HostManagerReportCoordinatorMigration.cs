using Microsoft.Data.Sqlite;

namespace ResourceManager.App.Infrastructure.Persistence.Schema.Migrations;

internal sealed class HostManagerReportCoordinatorMigration : ISqliteSchemaMigration
{
    public int Version => 10;

    public string Name => "host-manager-report-coordinator";

    public async Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE host_manager_report_coordinator_rows (
                operation_kind INTEGER NOT NULL
                    CHECK(operation_kind BETWEEN 1 AND 6),
                identity_handle INTEGER NOT NULL
                    CHECK(identity_handle <> 0),
                slot_generation INTEGER NOT NULL
                    CHECK(slot_generation <> 0),
                mutation_version INTEGER NOT NULL
                    CHECK(mutation_version <> 0),
                row_payload BLOB NOT NULL
                    CHECK(length(row_payload) = 352),
                PRIMARY KEY(operation_kind, identity_handle)
            ) WITHOUT ROWID, STRICT;

            CREATE INDEX ix_host_manager_report_coordinator_mutation
                ON host_manager_report_coordinator_rows(mutation_version);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
