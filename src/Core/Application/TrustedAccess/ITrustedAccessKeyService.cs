using ResourceManager.App.Domain.TrustedAccess;

namespace ResourceManager.App.Application.TrustedAccess;

public interface ITrustedAccessKeyService
{
    Task<IReadOnlyList<AccessKeySummary>> ListAsync(CancellationToken cancellationToken);

    Task<AccessKeyCreated> CreateAsync(
        AccessKeyCreateRequest request,
        CancellationToken cancellationToken);

    Task<AccessKeyCreated> RotateAsync(string credentialId, CancellationToken cancellationToken);

    Task<bool> RevokeAsync(string credentialId, CancellationToken cancellationToken);

    Task<AccessKeyCreated?> SetDefaultAsync(bool enabled, CancellationToken cancellationToken);

    Task<AccessKeySummary?> AuthenticateAsync(
        string purpose,
        string? apiKey,
        CancellationToken cancellationToken);

    Task InitializeAsync(bool recover, CancellationToken cancellationToken);

    CancellationToken GetRevocationToken(string credentialId);
}
