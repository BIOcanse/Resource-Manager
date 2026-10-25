using System.Security.Cryptography;
using System.Text;
using ResourceManager.App.Application.PublicServices.AiGateway;
using ResourceManager.App.Domain.PublicServices.AiGateway;
using ResourceManager.App.Domain.TrustedAccess;
using ResourceManager.App.Infrastructure.PublicServices.AiGateway;
using ResourceManager.Shared.Runtime.TrustedAccess;

namespace Resource_Manager_APP.Tests;

public sealed class AiGatewayCredentialServiceTests
{
    [Fact]
    public async Task CreateValidateReloadAndRevoke_UsesSharedOwnerAndKeepsOnlyDigest()
    {
        var fixture = TrustedAccessTestFactory.Create();
        var service = new AiGatewayCredentialService(fixture.Owner);

        var created = await service.CreateAsync(
            new AiGatewayCredentialCreateRequest("test client", AiGatewayCompatibilityProfiles.OpenAi),
            CancellationToken.None);

        Assert.StartsWith("rm_sk_", created.ApiKey, StringComparison.Ordinal);
        Assert.True(await service.ValidateAsync(
            AiGatewayCompatibilityProfiles.OpenAi,
            created.ApiKey,
            CancellationToken.None));
        Assert.False(await service.ValidateAsync(
            AiGatewayCompatibilityProfiles.Anthropic,
            created.ApiKey,
            CancellationToken.None));
        Assert.Equal(created.Credential, Assert.Single(await service.ListAsync(CancellationToken.None)));

        var state = fixture.Store.CurrentState!;
        var stored = state.Keys.Single(item => item.Id == created.Credential.Id);
        Assert.Equal(AccessKeyPurposes.OpenAi, stored.Purpose);
        Assert.Empty(stored.Scopes);
        Assert.Equal(SHA256.HashData(Encoding.UTF8.GetBytes(created.ApiKey)), stored.SecretHash);
        Assert.Equal(32, stored.SecretHash.Length);

        var reloadedOwner = new ResourceManager.App.Infrastructure.Security.TrustedAccess.TrustedAccessKeyService(
            fixture.Store,
            fixture.KeyFile);
        var reloaded = new AiGatewayCredentialService(reloadedOwner);
        Assert.True(await reloaded.ValidateAsync(
            AiGatewayCompatibilityProfiles.OpenAi,
            created.ApiKey,
            CancellationToken.None));
        Assert.True(await reloaded.RevokeAsync(created.Credential.Id, CancellationToken.None));
        Assert.False(await reloaded.ValidateAsync(
            AiGatewayCompatibilityProfiles.OpenAi,
            created.ApiKey,
            CancellationToken.None));
    }

    [Fact]
    public async Task Revoke_DoesNotRemoveAControlCredentialWithTheSameEndpointSurface()
    {
        var fixture = TrustedAccessTestFactory.Create();
        var control = await fixture.Owner.CreateAsync(
            new AccessKeyCreateRequest("control", AccessKeyPurposes.Control, [AccessKeyScopes.Read]),
            CancellationToken.None);
        var service = new AiGatewayCredentialService(fixture.Owner);

        Assert.False(await service.RevokeAsync(control.Credential.Id, CancellationToken.None));
        Assert.NotNull(await fixture.Owner.AuthenticateAsync(
            AccessKeyPurposes.Control,
            control.ApiKey,
            CancellationToken.None));
    }
}
