using Microsoft.Data.Sqlite;

namespace ResourceManager.App.Infrastructure.Persistence.Schema.Migrations;

internal sealed class AiGatewayCredentialsMigration : ISqliteSchemaMigration
{
    public int Version => 6;

    public string Name => "ai-gateway-credentials";

    public async Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE ai_gateway_credentials (
                id TEXT NOT NULL PRIMARY KEY,
                display_name TEXT NOT NULL,
                compatibility_profile TEXT NOT NULL
                    CHECK (compatibility_profile IN ('openai', 'anthropic')),
                secret_hash BLOB NOT NULL UNIQUE,
                created_utc_ticks INTEGER NOT NULL
            ) STRICT;

            CREATE INDEX ix_ai_gateway_credentials_profile
                ON ai_gateway_credentials(compatibility_profile, created_utc_ticks DESC);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
