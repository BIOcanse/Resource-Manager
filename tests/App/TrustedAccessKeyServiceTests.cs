using System.Security.Cryptography;
using System.Text;
using ResourceManager.App.Application.TrustedAccess;
using ResourceManager.App.Domain.TrustedAccess;
using ResourceManager.Shared.Runtime.TrustedAccess;

namespace Resource_Manager_APP.Tests;

public sealed class TrustedAccessKeyServiceTests
{
    [Fact]
    public async Task MutationReloadsRecoveredStateEvenBeforeTheChangeSignalArrives()
    {
        var fixture = TrustedAccessTestFactory.Create();
        using var running = fixture.Owner;
        var original = await running.CreateAsync(new AccessKeyCreateRequest("old", AccessKeyPurposes.Control, [AccessKeyScopes.Read]), CancellationToken.None);
        fixture.Store.SuppressChangeNotifications = true;
        using var administrator = new ResourceManager.App.Infrastructure.Security.TrustedAccess.TrustedAccessKeyService(fixture.Store, fixture.KeyFile);
        await administrator.InitializeAsync(true, CancellationToken.None);

        await running.CreateAsync(new AccessKeyCreateRequest("new", AccessKeyPurposes.Control, [AccessKeyScopes.Read]), CancellationToken.None);

        Assert.DoesNotContain(fixture.Store.CurrentState!.Keys, key => key.Id == original.Credential.Id);
        Assert.Null(await running.AuthenticateAsync(AccessKeyPurposes.Control, original.ApiKey, CancellationToken.None));
    }

    [Fact]
    public async Task OwnMutationNotificationsDoNotCancelUnchangedCredentials()
    {
        var fixture = TrustedAccessTestFactory.Create();
        using var owner = fixture.Owner;
        var defaultKey = (await owner.ListAsync(CancellationToken.None)).Single();
        var token = owner.GetRevocationToken(defaultKey.Id);
        await owner.CreateAsync(new AccessKeyCreateRequest("unrelated", AccessKeyPurposes.Control, [AccessKeyScopes.Read]), CancellationToken.None);
        Assert.False(token.IsCancellationRequested);
        Assert.NotNull(await owner.AuthenticateAsync(AccessKeyPurposes.Control, fixture.KeyFile.Value, CancellationToken.None));
        Assert.False(token.IsCancellationRequested);
    }

    [Fact]
    public async Task ChangeNotificationFailureCancelsStreamsAndRequiresSuccessfulReload()
    {
        var fixture = TrustedAccessTestFactory.Create();
        using var owner = fixture.Owner;
        var key = fixture.KeyFile.Value!;
        var summary = await owner.AuthenticateAsync(AccessKeyPurposes.Control, key, CancellationToken.None);
        var token = owner.GetRevocationToken(summary!.Id);
        fixture.Store.LoadFailure = new InvalidDataException("unavailable store");
        fixture.Store.NotifyFailure(new IOException("notification overflow"));

        Assert.True(token.IsCancellationRequested);
        await Assert.ThrowsAsync<InvalidDataException>(() => owner.AuthenticateAsync(AccessKeyPurposes.Control, key, CancellationToken.None));
        fixture.Store.LoadFailure = null;
        Assert.NotNull(await owner.AuthenticateAsync(AccessKeyPurposes.Control, key, CancellationToken.None));
    }

    [Fact]
    public async Task AuthenticationReusesImmutableSummaryWithoutExposingMutableSnapshotScopes()
    {
        var fixture = TrustedAccessTestFactory.Create();
        using var owner = fixture.Owner;
        var summary = await owner.AuthenticateAsync(AccessKeyPurposes.Control, fixture.KeyFile.Value, CancellationToken.None);
        var again = await owner.AuthenticateAsync(AccessKeyPurposes.Control, fixture.KeyFile.Value, CancellationToken.None);
        Assert.Same(summary, again);
        Assert.IsNotType<string[]>(summary!.Scopes);
        var collection = Assert.IsAssignableFrom<IList<string>>(summary.Scopes);
        Assert.Throws<NotSupportedException>(() => collection[0] = AccessKeyScopes.MachineWrite);
        Assert.DoesNotContain(AccessKeyScopes.MachineWrite, again!.Scopes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("rm_sk_ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")]
    public async Task RepeatingEnableDoesNotSilentlyReplaceAMissingOrMismatchedDefaultFile(string? externalValue)
    {
        var fixture = TrustedAccessTestFactory.Create();
        var original = Assert.Single(await fixture.Owner.ListAsync(CancellationToken.None));
        var originalKey = fixture.KeyFile.Value;
        fixture.KeyFile.ReplaceExternallyForTest(externalValue);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.Owner.SetDefaultAsync(true, CancellationToken.None));

        Assert.Equal(original.Id, Assert.Single(fixture.Store.CurrentState!.Keys).Id);
        Assert.Equal(externalValue, fixture.KeyFile.Value);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Owner.AuthenticateAsync(
            AccessKeyPurposes.Control, originalKey, CancellationToken.None));
    }

    [Fact]
    public async Task MissingStore_FailsClosedUntilExplicitInitialization()
    {
        var fixture = TrustedAccessTestFactory.CreateUninitialized();

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Owner.ListAsync(CancellationToken.None));
        Assert.False(fixture.Store.DatabaseExists);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Owner.AuthenticateAsync(
            AccessKeyPurposes.Control,
            "rm_sk_" + new string('a', 64),
            CancellationToken.None));
        Assert.False(fixture.Store.DatabaseExists);

        await fixture.Owner.InitializeAsync(recover: false, cancellationToken: CancellationToken.None);
        var defaultCredential = Assert.Single(await fixture.Owner.ListAsync(CancellationToken.None));
        Assert.True(defaultCredential.IsDefault);
        Assert.Equal(AccessKeyScopes.Default, defaultCredential.Scopes);
        Assert.StartsWith("rm_sk_", fixture.KeyFile.Value, StringComparison.Ordinal);
        Assert.NotNull(await fixture.Owner.AuthenticateAsync(
            AccessKeyPurposes.Control,
            fixture.KeyFile.Value,
            CancellationToken.None));
    }

    [Fact]
    public async Task CreateRotateAndRevoke_UsesOneOwnerAndCancelsStreams()
    {
        var fixture = TrustedAccessTestFactory.Create();
        var defaultBefore = Assert.Single((await fixture.Owner.ListAsync(CancellationToken.None))
            .Where(static item => item.IsDefault));
        var created = await fixture.Owner.CreateAsync(
            new AccessKeyCreateRequest(
                "third-party",
                AccessKeyPurposes.Control,
                [AccessKeyScopes.Read, AccessKeyScopes.ProcessesRead]),
            CancellationToken.None);
        var oldStream = fixture.Owner.GetRevocationToken(created.Credential.Id);
        Assert.False(oldStream.IsCancellationRequested);

        var rotated = await fixture.Owner.RotateAsync(created.Credential.Id, CancellationToken.None);
        Assert.True(oldStream.IsCancellationRequested);
        Assert.Null(await fixture.Owner.AuthenticateAsync(
            AccessKeyPurposes.Control,
            created.ApiKey,
            CancellationToken.None));
        Assert.Equal(rotated.Credential.Id, (await fixture.Owner.AuthenticateAsync(
            AccessKeyPurposes.Control,
            rotated.ApiKey,
            CancellationToken.None))!.Id);
        Assert.Equal(created.Credential.Name, rotated.Credential.Name);
        Assert.Equal(created.Credential.Scopes, rotated.Credential.Scopes);

        var defaultAfter = Assert.Single((await fixture.Owner.ListAsync(CancellationToken.None))
            .Where(static item => item.IsDefault));
        Assert.Equal(defaultBefore.Id, defaultAfter.Id);

        var rotatedStream = fixture.Owner.GetRevocationToken(rotated.Credential.Id);
        Assert.True(await fixture.Owner.RevokeAsync(rotated.Credential.Id, CancellationToken.None));
        Assert.True(rotatedStream.IsCancellationRequested);
        Assert.Null(await fixture.Owner.AuthenticateAsync(
            AccessKeyPurposes.Control,
            rotated.ApiKey,
            CancellationToken.None));
    }

    [Fact]
    public async Task DefaultKey_UsesRequiredScopesAndDisableDoesNotReissueOnRepeatedInitialize()
    {
        var fixture = TrustedAccessTestFactory.Create();
        var original = Assert.Single((await fixture.Owner.ListAsync(CancellationToken.None))
            .Where(static item => item.IsDefault));
        var originalApiKey = fixture.KeyFile.Value!;
        var stream = fixture.Owner.GetRevocationToken(original.Id);

        Assert.Null(await fixture.Owner.SetDefaultAsync(
            enabled: false,
            cancellationToken: CancellationToken.None));
        Assert.Equal(string.Empty, fixture.KeyFile.Value);
        Assert.True(stream.IsCancellationRequested);
        Assert.Null(await fixture.Owner.AuthenticateAsync(
            AccessKeyPurposes.Control,
            originalApiKey,
            CancellationToken.None));
        Assert.Empty((await fixture.Owner.ListAsync(CancellationToken.None)).Where(static item => item.IsDefault));

        await fixture.Owner.InitializeAsync(recover: false, cancellationToken: CancellationToken.None);
        Assert.Empty((await fixture.Owner.ListAsync(CancellationToken.None)).Where(static item => item.IsDefault));
        Assert.Equal(string.Empty, fixture.KeyFile.Value);

        var reenabled = await fixture.Owner.SetDefaultAsync(
            enabled: true,
            cancellationToken: CancellationToken.None);
        Assert.NotNull(reenabled);
        Assert.NotEqual(originalApiKey, reenabled.ApiKey);
        Assert.Equal(reenabled.ApiKey, fixture.KeyFile.Value);
        Assert.True(reenabled.Credential.IsDefault);
        Assert.Equal(AccessKeyScopes.Default, reenabled.Credential.Scopes);
    }

    [Fact]
    public async Task DefaultRotation_ReplacesTheFileAndImmediatelyInvalidatesTheOldKey()
    {
        var fixture = TrustedAccessTestFactory.Create();
        var original = Assert.Single((await fixture.Owner.ListAsync(CancellationToken.None))
            .Where(static item => item.IsDefault));
        var oldApiKey = fixture.KeyFile.Value!;
        var oldStream = fixture.Owner.GetRevocationToken(original.Id);

        var rotated = await fixture.Owner.RotateAsync(original.Id, CancellationToken.None);

        Assert.NotEqual(oldApiKey, rotated.ApiKey);
        Assert.Equal(rotated.ApiKey, fixture.KeyFile.Value);
        Assert.True(oldStream.IsCancellationRequested);
        Assert.Null(await fixture.Owner.AuthenticateAsync(
            AccessKeyPurposes.Control,
            oldApiKey,
            CancellationToken.None));
        var authenticated = await fixture.Owner.AuthenticateAsync(
            AccessKeyPurposes.Control,
            rotated.ApiKey,
            CancellationToken.None);
        Assert.NotNull(authenticated);
        Assert.True(authenticated!.IsDefault);
        Assert.Equal(AccessKeyScopes.Default, authenticated.Scopes);
    }

    [Fact]
    public async Task RevokePublishesInvalidCredentialEvenWhenAStreamingCancellationCallbackThrows()
    {
        var fixture = TrustedAccessTestFactory.Create();
        var created = await fixture.Owner.CreateAsync(
            new AccessKeyCreateRequest("streaming client", AccessKeyPurposes.Control, [AccessKeyScopes.Read]),
            CancellationToken.None);
        using var callback = fixture.Owner.GetRevocationToken(created.Credential.Id)
            .Register(static () => throw new InvalidOperationException("subscriber cleanup failure"));

        Assert.True(await fixture.Owner.RevokeAsync(created.Credential.Id, CancellationToken.None));
        Assert.True(fixture.Owner.GetRevocationToken(created.Credential.Id).IsCancellationRequested);
        Assert.Null(await fixture.Owner.AuthenticateAsync(
            AccessKeyPurposes.Control,
            created.ApiKey,
            CancellationToken.None));
    }

    [Fact]
    public async Task PurposeIsPartOfAuthenticationAndLegacyAiRowsMigrateOnlyOnExplicitInitialization()
    {
        const string legacyApiKey = "rm_sk_0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var fixture = TrustedAccessTestFactory.CreateUninitialized();
        var createdAt = DateTimeOffset.UtcNow.AddDays(-7);
        fixture.Store.LegacyAiCredentials =
        [
            new StoredTrustedAccessKey(
                "legacy-ai-id",
                "legacy profile",
                AccessKeyPurposes.Anthropic,
                [],
                SHA256.HashData(Encoding.UTF8.GetBytes(legacyApiKey)),
                createdAt)
        ];

        await fixture.Owner.InitializeAsync(recover: false, cancellationToken: CancellationToken.None);
        var migrated = (await fixture.Owner.ListAsync(CancellationToken.None))
            .Single(item => item.Id == "legacy-ai-id");
        Assert.Equal("legacy profile", migrated.Name);
        Assert.Equal(AccessKeyPurposes.Anthropic, migrated.Purpose);
        Assert.Equal(createdAt, migrated.CreatedAt);
        Assert.Equal("legacy-ai-id", (await fixture.Owner.AuthenticateAsync(
            AccessKeyPurposes.Anthropic,
            legacyApiKey,
            CancellationToken.None))!.Id);
        Assert.Null(await fixture.Owner.AuthenticateAsync(
            AccessKeyPurposes.OpenAi,
            legacyApiKey,
            CancellationToken.None));
        Assert.Equal(1, fixture.Store.LegacyReadCount);

        await fixture.Owner.InitializeAsync(recover: false, cancellationToken: CancellationToken.None);
        Assert.Equal(1, fixture.Store.LegacyReadCount);
        Assert.Single((await fixture.Owner.ListAsync(CancellationToken.None))
            .Where(item => item.Id == "legacy-ai-id"));
    }

    [Fact]
    public async Task CorruptStateAndMismatchedDefaultFileFailClosedWithoutAutomaticRepair()
    {
        var fixture = TrustedAccessTestFactory.Create();
        var defaultCredential = Assert.Single((await fixture.Owner.ListAsync(CancellationToken.None))
            .Where(static item => item.IsDefault));
        var apiKey = fixture.KeyFile.Value!;

        fixture.KeyFile.ReplaceExternallyForTest("rm_sk_" + new string('b', 64));
        var restarted = new ResourceManager.App.Infrastructure.Security.TrustedAccess.TrustedAccessKeyService(
            fixture.Store,
            fixture.KeyFile);
        await Assert.ThrowsAsync<InvalidDataException>(() => restarted.AuthenticateAsync(
            AccessKeyPurposes.Control,
            apiKey,
            CancellationToken.None));
        Assert.NotEqual(apiKey, fixture.KeyFile.Value);
        Assert.Equal(defaultCredential.Id, fixture.Store.CurrentState!.DefaultCredentialId);

        fixture.Store.LoadFailure = new InvalidDataException("corrupt database");
        var corruptKeyFile = new TrustedAccessTestKeyFileAccess();
        corruptKeyFile.ReplaceExternallyForTest(fixture.KeyFile.Value);
        var corrupt = new ResourceManager.App.Infrastructure.Security.TrustedAccess.TrustedAccessKeyService(
            fixture.Store,
            corruptKeyFile);
        await Assert.ThrowsAsync<InvalidDataException>(() => corrupt.ListAsync(CancellationToken.None));
        Assert.True(fixture.Store.DatabaseExists);
    }

}
