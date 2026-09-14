using Microsoft.Data.Sqlite;
using ResourceManager.App.Application.PublicServices.AiGateway;
using ResourceManager.App.Infrastructure.Persistence;

namespace ResourceManager.App.Infrastructure.PublicServices.AiGateway;

public sealed class SqliteAiGatewayCredentialStore(ResourceManagerDatabase database)
    : IAiGatewayCredentialStore
{
    public async Task<IReadOnlyList<StoredAiGatewayCredential>> LoadAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, display_name, compatibility_profile, secret_hash, created_utc_ticks
            FROM ai_gateway_credentials
            ORDER BY created_utc_ticks DESC;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<StoredAiGatewayCredential>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new StoredAiGatewayCredential(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetFieldValue<byte[]>(3),
                new DateTimeOffset(reader.GetInt64(4), TimeSpan.Zero)));
        }

        return result;
    }

    public async Task AddAsync(
        StoredAiGatewayCredential credential,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ai_gateway_credentials(
                id,
                display_name,
                compatibility_profile,
                secret_hash,
                created_utc_ticks)
            VALUES ($id, $displayName, $profile, $secretHash, $createdAt);
            """;
        command.Parameters.AddWithValue("$id", credential.Id);
        command.Parameters.AddWithValue("$displayName", credential.DisplayName);
        command.Parameters.AddWithValue("$profile", credential.CompatibilityProfile);
        command.Parameters.Add("$secretHash", SqliteType.Blob).Value = credential.SecretHash;
        command.Parameters.AddWithValue("$createdAt", credential.CreatedAt.UtcTicks);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> DeleteAsync(
        string credentialId,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM ai_gateway_credentials WHERE id = $id;";
        command.Parameters.AddWithValue("$id", credentialId);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }
}
