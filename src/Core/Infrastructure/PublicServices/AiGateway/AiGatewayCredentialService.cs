using ResourceManager.App.Application.PublicServices.AiGateway;
using ResourceManager.App.Application.TrustedAccess;
using ResourceManager.App.Domain.PublicServices.AiGateway;
using ResourceManager.App.Domain.TrustedAccess;
using ResourceManager.Shared.Runtime.TrustedAccess;

namespace ResourceManager.App.Infrastructure.PublicServices.AiGateway;

public sealed class AiGatewayCredentialService(ITrustedAccessKeyService trustedAccess)
    : IAiGatewayCredentialService
{
    public async Task<IReadOnlyList<AiGatewayCredentialSummary>> ListAsync(
        CancellationToken cancellationToken)
    {
        var credentials = await trustedAccess.ListAsync(cancellationToken);
        return credentials
            .Where(static item => item.Purpose is AccessKeyPurposes.OpenAi or AccessKeyPurposes.Anthropic)
            .OrderByDescending(static item => item.CreatedAt)
            .Select(ToSummary)
            .ToArray();
    }

    public async Task<AiGatewayCredentialCreated> CreateAsync(
        AiGatewayCredentialCreateRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!AiGatewayCompatibilityProfiles.IsSupported(request.CompatibilityProfile))
        {
            throw new ArgumentException("Unsupported AI compatibility profile.", nameof(request));
        }

        var profile = AiGatewayCompatibilityProfiles.Normalize(request.CompatibilityProfile);
        var created = await trustedAccess.CreateAsync(
            new AccessKeyCreateRequest(request.DisplayName, profile, []),
            cancellationToken);
        return new AiGatewayCredentialCreated(ToSummary(created.Credential), created.ApiKey);
    }

    public async Task<bool> RevokeAsync(
        string credentialId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(credentialId))
        {
            return false;
        }

        var credential = (await trustedAccess.ListAsync(cancellationToken))
            .FirstOrDefault(item => item.Id.Equals(credentialId.Trim(), StringComparison.Ordinal));
        if (credential is null
            || credential.Purpose is not (AccessKeyPurposes.OpenAi or AccessKeyPurposes.Anthropic))
        {
            return false;
        }

        return await trustedAccess.RevokeAsync(credentialId, cancellationToken);
    }

    public async Task<bool> ValidateAsync(
        string compatibilityProfile,
        string? apiKey,
        CancellationToken cancellationToken)
    {
        if (!AiGatewayCompatibilityProfiles.IsSupported(compatibilityProfile))
        {
            return false;
        }

        return await trustedAccess.AuthenticateAsync(
            AiGatewayCompatibilityProfiles.Normalize(compatibilityProfile),
            apiKey,
            cancellationToken) is not null;
    }

    private static AiGatewayCredentialSummary ToSummary(AccessKeySummary credential) =>
        new(
            credential.Id,
            credential.Name,
            credential.Purpose,
            credential.CreatedAt);
}
