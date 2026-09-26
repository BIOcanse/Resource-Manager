namespace ResourceManager.App.Application.PublicServices.AiGateway;

public sealed record StoredAiGatewayCredential(
    string Id,
    string DisplayName,
    string CompatibilityProfile,
    byte[] SecretHash,
    DateTimeOffset CreatedAt);

public interface IAiGatewayCredentialStore
{
    Task<IReadOnlyList<StoredAiGatewayCredential>> LoadAsync(CancellationToken cancellationToken);

    Task AddAsync(StoredAiGatewayCredential credential, CancellationToken cancellationToken);

    Task<bool> DeleteAsync(string credentialId, CancellationToken cancellationToken);
}
