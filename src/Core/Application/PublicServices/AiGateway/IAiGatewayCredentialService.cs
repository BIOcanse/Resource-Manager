using ResourceManager.App.Domain.PublicServices.AiGateway;

namespace ResourceManager.App.Application.PublicServices.AiGateway;

public interface IAiGatewayCredentialService
{
    Task<IReadOnlyList<AiGatewayCredentialSummary>> ListAsync(CancellationToken cancellationToken);

    Task<AiGatewayCredentialCreated> CreateAsync(
        AiGatewayCredentialCreateRequest request,
        CancellationToken cancellationToken);

    Task<bool> RevokeAsync(string credentialId, CancellationToken cancellationToken);

    Task<bool> ValidateAsync(
        string compatibilityProfile,
        string? apiKey,
        CancellationToken cancellationToken);
}
