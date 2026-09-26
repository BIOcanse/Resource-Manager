using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;
using ResourceManager.App.Application.PublicServices.AiGateway;
using ResourceManager.App.Domain.PublicServices.AiGateway;

namespace ResourceManager.App.Infrastructure.PublicServices.AiGateway;

public sealed class AiGatewayCredentialService(IAiGatewayCredentialStore store)
    : IAiGatewayCredentialService
{
    private const string KeyPrefix = "rm_sk_";
    private const int MaximumDisplayNameLength = 80;
    private readonly SemaphoreSlim mutationGate = new(1, 1);
    private FrozenDictionary<string, StoredAiGatewayCredential>? credentialSnapshot;

    public async Task<IReadOnlyList<AiGatewayCredentialSummary>> ListAsync(
        CancellationToken cancellationToken)
    {
        var snapshot = await GetSnapshotAsync(cancellationToken);
        return snapshot.Values
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
        var displayName = NormalizeDisplayName(request.DisplayName, profile);
        var apiKey = KeyPrefix + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var stored = new StoredAiGatewayCredential(
            Guid.NewGuid().ToString("N"),
            displayName,
            profile,
            Hash(apiKey),
            DateTimeOffset.UtcNow);

        await GetSnapshotAsync(cancellationToken);
        await mutationGate.WaitAsync(cancellationToken);
        try
        {
            await store.AddAsync(stored, cancellationToken);
            var updated = credentialSnapshot!.Values.Append(stored).ToArray();
            Volatile.Write(ref credentialSnapshot, BuildSnapshot(updated));
        }
        finally
        {
            mutationGate.Release();
        }

        return new AiGatewayCredentialCreated(ToSummary(stored), apiKey);
    }

    public async Task<bool> RevokeAsync(
        string credentialId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(credentialId))
        {
            return false;
        }

        await GetSnapshotAsync(cancellationToken);
        await mutationGate.WaitAsync(cancellationToken);
        try
        {
            if (!await store.DeleteAsync(credentialId.Trim(), cancellationToken))
            {
                return false;
            }

            var updated = credentialSnapshot!.Values
                .Where(item => !item.Id.Equals(credentialId, StringComparison.Ordinal))
                .ToArray();
            Volatile.Write(ref credentialSnapshot, BuildSnapshot(updated));
            return true;
        }
        finally
        {
            mutationGate.Release();
        }
    }

    public async Task<bool> ValidateAsync(
        string compatibilityProfile,
        string? apiKey,
        CancellationToken cancellationToken)
    {
        if (!AiGatewayCompatibilityProfiles.IsSupported(compatibilityProfile)
            || string.IsNullOrWhiteSpace(apiKey)
            || !apiKey.StartsWith(KeyPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var profile = AiGatewayCompatibilityProfiles.Normalize(compatibilityProfile);
        var snapshot = await GetSnapshotAsync(cancellationToken);
        return snapshot.ContainsKey(BuildLookupKey(profile, Hash(apiKey)));
    }

    private async Task<FrozenDictionary<string, StoredAiGatewayCredential>> GetSnapshotAsync(
        CancellationToken cancellationToken)
    {
        var current = Volatile.Read(ref credentialSnapshot);
        if (current is not null)
        {
            return current;
        }

        await mutationGate.WaitAsync(cancellationToken);
        try
        {
            current = credentialSnapshot;
            if (current is null)
            {
                current = BuildSnapshot(await store.LoadAsync(cancellationToken));
                Volatile.Write(ref credentialSnapshot, current);
            }

            return current;
        }
        finally
        {
            mutationGate.Release();
        }
    }

    private static FrozenDictionary<string, StoredAiGatewayCredential> BuildSnapshot(
        IEnumerable<StoredAiGatewayCredential> credentials) =>
        credentials.ToFrozenDictionary(
            static credential => BuildLookupKey(
                credential.CompatibilityProfile,
                credential.SecretHash),
            StringComparer.Ordinal);

    private static string BuildLookupKey(string profile, byte[] hash) =>
        $"{profile}:{Convert.ToHexString(hash)}";

    private static byte[] Hash(string apiKey) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(apiKey));

    private static string NormalizeDisplayName(string? displayName, string profile)
    {
        var normalized = string.IsNullOrWhiteSpace(displayName)
            ? $"{profile} client"
            : displayName.Trim();
        if (normalized.Length > MaximumDisplayNameLength)
        {
            throw new ArgumentException(
                $"Credential display name cannot exceed {MaximumDisplayNameLength} characters.",
                nameof(displayName));
        }

        return normalized;
    }

    private static AiGatewayCredentialSummary ToSummary(StoredAiGatewayCredential credential) =>
        new(
            credential.Id,
            credential.DisplayName,
            credential.CompatibilityProfile,
            credential.CreatedAt);
}
