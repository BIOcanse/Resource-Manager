using Microsoft.Data.Sqlite;

namespace ResourceManager.App.Infrastructure.Persistence.Schema.Migrations;

internal sealed class PortableSoftwareConfirmationMigration : ISqliteSchemaMigration
{
    public int Version => 9;

    public string Name => "portable-software-confirmation";

    public async Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            ALTER TABLE portable_software_executables
                ADD COLUMN identity_confirmed INTEGER NOT NULL DEFAULT 0;

            ALTER TABLE portable_software_executables
                ADD COLUMN root_confirmed INTEGER NOT NULL DEFAULT 0;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
