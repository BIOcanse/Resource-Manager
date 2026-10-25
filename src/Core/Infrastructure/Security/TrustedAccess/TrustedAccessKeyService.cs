using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using ResourceManager.App.Application.TrustedAccess;
using ResourceManager.App.Domain.TrustedAccess;
using ResourceManager.Shared.Runtime.TrustedAccess;

namespace ResourceManager.App.Infrastructure.Security.TrustedAccess;

public sealed class TrustedAccessKeyService(
    ITrustedAccessKeyStore store,
    ITrustedAccessKeyFileAccess keyFile,
    ILogger<TrustedAccessKeyService>? logger = null) : ITrustedAccessKeyService, IDisposable
{
    private const string KeyPrefix = "rm_sk_";
    private const int MaximumNameLength = 80;
    private const int MaximumCredentialCount = 10000;
    private readonly SemaphoreSlim mutationGate = new(1, 1);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> revocationTokens = new(StringComparer.Ordinal);
    private CredentialSnapshot? credentialSnapshot;
    private IDisposable? changeSubscription;
    private readonly object changeSync = new();
    private bool refreshRunning;
    private bool refreshRequested;
    private Exception? notificationFailure;
    private bool disposed;

    public async Task<IReadOnlyList<AccessKeySummary>> ListAsync(CancellationToken cancellationToken)
    {
        var snapshot = await EnsureLoadedAsync(cancellationToken);
        return snapshot.Summaries;
    }

    public async Task<AccessKeyCreated> CreateAsync(
        AccessKeyCreateRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var purpose = NormalizePurpose(request.Purpose, nameof(request));
        var name = NormalizeName(request.Name, purpose);
        var scopes = NormalizeScopes(request.Scopes);
        await EnsureLoadedAsync(cancellationToken);
        await mutationGate.WaitAsync(cancellationToken);
        try
        {
            using var lease = await store.AcquireMutationLockAsync(cancellationToken);
            var current = (await ReloadSnapshotAsync(cancellationToken)).State;
            if (current.Keys.Count >= MaximumCredentialCount)
            {
                throw new InvalidOperationException("The trusted access key limit has been reached.");
            }

            var apiKey = CreateApiKey();
            var credential = new StoredTrustedAccessKey(
                Guid.NewGuid().ToString("N"),
                name,
                purpose,
                scopes,
                Hash(apiKey),
                DateTimeOffset.UtcNow);
            var updated = CopyState(current, current.Keys.Append(credential).ToArray());
            await store.SaveAsync(updated, current.Revision, cancellationToken);
            PublishSnapshot(updated);
            logger?.LogInformation(
                "Trusted access key {CredentialId} was created for purpose {Purpose}.",
                credential.Id,
                purpose);
            return new AccessKeyCreated(ToSummary(credential, updated), apiKey);
        }
        finally
        {
            mutationGate.Release();
        }
    }

    public async Task<AccessKeyCreated> RotateAsync(
        string credentialId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(credentialId))
        {
            throw new ArgumentException("A credential ID is required.", nameof(credentialId));
        }

        await EnsureLoadedAsync(cancellationToken);
        await mutationGate.WaitAsync(cancellationToken);
        try
        {
            using var lease = await store.AcquireMutationLockAsync(cancellationToken);
            var current = (await ReloadSnapshotAsync(cancellationToken)).State;
            var oldCredential = current.Keys.FirstOrDefault(item =>
                item.Id.Equals(credentialId.Trim(), StringComparison.Ordinal));
            if (oldCredential is null)
            {
                throw new KeyNotFoundException("Trusted access key was not found.");
            }

            var apiKey = CreateApiKey();
            var replacement = oldCredential with
            {
                Id = Guid.NewGuid().ToString("N"),
                SecretHash = Hash(apiKey),
                CreatedAt = DateTimeOffset.UtcNow,
                Scopes = [.. oldCredential.Scopes]
            };
            var isDefault = current.DefaultEnabled
                && current.DefaultCredentialId!.Equals(oldCredential.Id, StringComparison.Ordinal);
            var updated = new TrustedAccessKeyStoreState(
                current.Keys
                    .Where(item => !item.Id.Equals(oldCredential.Id, StringComparison.Ordinal))
                    .Append(replacement)
                    .ToArray(),
                current.DefaultEnabled,
                isDefault ? replacement.Id : current.DefaultCredentialId,
                current.LegacyAiMigrationCompleted);

            if (isDefault)
            {
                PublishSnapshot(DisableDefaultSnapshot(current));
                CancelRevocation(oldCredential.Id);
                keyFile.WriteAtomic(apiKey);
            }

            try
            {
                await store.SaveAsync(updated, current.Revision, cancellationToken);
            }
            catch
            {
                if (isDefault)
                {
                    var disabled = DisableDefaultSnapshot(updated);
                    PublishSnapshot(disabled);
                    try
                    {
                        keyFile.WriteAtomic(string.Empty);
                    }
                    catch (Exception rollbackException)
                    {
                        logger?.LogError(
                            rollbackException,
                            "Trusted access default key file could not be cleared after a failed rotation.");
                    }
                }

                throw;
            }

            PublishSnapshot(updated);
            CancelRevocation(oldCredential.Id);
            logger?.LogInformation(
                "Trusted access key {CredentialId} was rotated to {ReplacementCredentialId}.",
                oldCredential.Id,
                replacement.Id);
            return new AccessKeyCreated(ToSummary(replacement, updated), apiKey);
        }
        finally
        {
            mutationGate.Release();
        }
    }

    public async Task<bool> RevokeAsync(string credentialId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(credentialId))
        {
            return false;
        }

        await EnsureLoadedAsync(cancellationToken);
        await mutationGate.WaitAsync(cancellationToken);
        try
        {
            using var lease = await store.AcquireMutationLockAsync(cancellationToken);
            var current = (await ReloadSnapshotAsync(cancellationToken)).State;
            var id = credentialId.Trim();
            var credential = current.Keys.FirstOrDefault(item => item.Id.Equals(id, StringComparison.Ordinal));
            if (credential is null)
            {
                return false;
            }

            if (current.DefaultEnabled
                && current.DefaultCredentialId!.Equals(credential.Id, StringComparison.Ordinal))
            {
                await DisableDefaultAsync(current, cancellationToken);
                return true;
            }

            var updated = CopyState(
                current,
                current.Keys.Where(item => !item.Id.Equals(id, StringComparison.Ordinal)).ToArray());
            await store.SaveAsync(updated, current.Revision, cancellationToken);
            PublishSnapshot(updated);
            logger?.LogInformation("Trusted access key {CredentialId} was revoked.", id);
            return true;
        }
        finally
        {
            mutationGate.Release();
        }
    }

    public async Task<AccessKeyCreated?> SetDefaultAsync(
        bool enabled,
        CancellationToken cancellationToken)
    {
        await EnsureLoadedAsync(cancellationToken);
        await mutationGate.WaitAsync(cancellationToken);
        try
        {
            using var lease = await store.AcquireMutationLockAsync(cancellationToken);
            var current = (await ReloadSnapshotAsync(cancellationToken)).State;
            if (!enabled)
            {
                await DisableDefaultAsync(current, cancellationToken);
                return null;
            }

            var existing = current.DefaultEnabled
                ? current.Keys.FirstOrDefault(item => item.Id.Equals(current.DefaultCredentialId, StringComparison.Ordinal))
                : null;
            var existingKey = existing is null ? null : keyFile.Read();
            if (existing is not null
                && !string.IsNullOrEmpty(existingKey)
                && CryptographicOperations.FixedTimeEquals(Hash(existingKey), existing.SecretHash))
            {
                return new AccessKeyCreated(ToSummary(existing, current), existingKey);
            }

            if (current.DefaultEnabled)
                throw new InvalidDataException("The enabled default key file does not match its credential; explicit rotation or recovery is required.");

            var apiKey = CreateApiKey();
            var credential = new StoredTrustedAccessKey(
                Guid.NewGuid().ToString("N"),
                "Default control access",
                AccessKeyPurposes.Control,
                AccessKeyScopes.Default,
                Hash(apiKey),
                DateTimeOffset.UtcNow);
            var updated = new TrustedAccessKeyStoreState(
                current.Keys
                    .Where(item => !item.Id.Equals(existing?.Id, StringComparison.Ordinal))
                    .Append(credential)
                    .ToArray(),
                true,
                credential.Id,
                current.LegacyAiMigrationCompleted);

            keyFile.WriteAtomic(apiKey);
            if (existing is not null)
            {
                CancelRevocation(existing.Id);
                PublishSnapshot(DisableDefaultSnapshot(updated));
            }

            try
            {
                await store.SaveAsync(updated, current.Revision, cancellationToken);
            }
            catch
            {
                if (existing is not null)
                {
                    PublishSnapshot(DisableDefaultSnapshot(updated));
                }

                try
                {
                    keyFile.WriteAtomic(string.Empty);
                }
                catch (Exception rollbackException)
                {
                    logger?.LogError(
                        rollbackException,
                        "Trusted access default key file could not be cleared after a failed enable operation.");
                }

                throw;
            }

            PublishSnapshot(updated);
            logger?.LogInformation("Trusted access default key {CredentialId} was enabled.", credential.Id);
            return new AccessKeyCreated(ToSummary(credential, updated), apiKey);
        }
        finally
        {
            mutationGate.Release();
        }
    }

    public Task<AccessKeySummary?> AuthenticateAsync(
        string purpose,
        string? apiKey,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!AccessKeyPurposes.IsSupported(purpose)
            || string.IsNullOrWhiteSpace(apiKey)
            || apiKey.Length != 70
            || !apiKey.StartsWith(KeyPrefix, StringComparison.Ordinal)
            || apiKey.AsSpan(KeyPrefix.Length).IndexOfAnyExcept("0123456789abcdefABCDEF") >= 0)
        {
            return Task.FromResult<AccessKeySummary?>(null);
        }

        var snapshot = Volatile.Read(ref credentialSnapshot);
        return snapshot is null
            ? AuthenticateAfterLoadingAsync(purpose, apiKey, cancellationToken)
            : Authenticate(snapshot, purpose, apiKey);
    }

    private async Task<AccessKeySummary?> AuthenticateAfterLoadingAsync(
        string purpose, string apiKey, CancellationToken cancellationToken) =>
        await Authenticate(await EnsureLoadedAsync(cancellationToken), purpose, apiKey);

    private static Task<AccessKeySummary?> Authenticate(CredentialSnapshot snapshot, string purpose, string apiKey)
    {
        Span<byte> bytes = stackalloc byte[70];
        Encoding.ASCII.GetBytes(apiKey, bytes);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(bytes, hash);
        return snapshot.Credentials.TryGetValue(
            CredentialLookup.Create(purpose, hash), out var credential)
            ? credential
            : Task.FromResult<AccessKeySummary?>(null);
    }

    public async Task InitializeAsync(bool recover, CancellationToken cancellationToken)
    {
        await mutationGate.WaitAsync(cancellationToken);
        try
        {
            using var lease = await store.AcquireMutationLockAsync(cancellationToken, ensureInitialized: true);
            if (recover)
            {
                var empty = EmptyState(legacyAiMigrationCompleted: true);
                PublishSnapshot(empty);
                var recovered = CreateDefaultState([], legacyAiMigrationCompleted: true);
                keyFile.WriteAtomic(recovered.ApiKey);
                try
                {
                    await store.RecoverAsync(recovered.State, cancellationToken);
                }
                catch
                {
                    keyFile.WriteAtomic(string.Empty);
                    throw;
                }

                PublishSnapshot(recovered.State);
                EnsureWatching();
                logger?.LogInformation("Trusted access storage was explicitly recovered.");
                return;
            }

            TrustedAccessKeyStoreState initialized;
            if (store.DatabaseExists)
            {
                var existing = await store.LoadAsync(cancellationToken);
                if (existing.LegacyAiMigrationCompleted)
                {
                    if (!existing.DefaultEnabled && keyFile.Read() != string.Empty)
                    {
                        keyFile.WriteAtomic(string.Empty);
                    }

                    if (existing.DefaultEnabled)
                    {
                        EnsureDefaultFileMatches(existing);
                    }

                    initialized = existing;
                }
                else
                {
                    var legacy = await store.ReadLegacyAiCredentialsAsync(cancellationToken);
                    initialized = MergeLegacyAiCredentials(existing, legacy);
                    if (initialized.DefaultEnabled)
                    {
                        EnsureDefaultFileMatches(initialized);
                    }
                    else if (keyFile.Read() != string.Empty)
                    {
                        keyFile.WriteAtomic(string.Empty);
                    }

                    await store.SaveAsync(initialized, existing.Revision, cancellationToken);
                }
            }
            else
            {
                var legacy = await store.ReadLegacyAiCredentialsAsync(cancellationToken);
                var created = CreateDefaultState(legacy, legacyAiMigrationCompleted: true);
                initialized = created.State;
                keyFile.WriteAtomic(created.ApiKey);
                try
                {
                    await store.InitializeAsync(initialized, cancellationToken);
                }
                catch
                {
                    keyFile.WriteAtomic(string.Empty);
                    throw;
                }
            }

            PublishSnapshot(initialized);
            EnsureWatching();
            logger?.LogInformation("Trusted access storage initialization completed.");
        }
        finally
        {
            mutationGate.Release();
        }
    }

    public CancellationToken GetRevocationToken(string credentialId)
    {
        if (string.IsNullOrWhiteSpace(credentialId))
        {
            return new CancellationToken(canceled: true);
        }

        if (!revocationTokens.TryGetValue(credentialId, out var source))
            return new CancellationToken(canceled: true);

        try
        {
            return source.Token;
        }
        catch (ObjectDisposedException)
        {
            return new CancellationToken(canceled: true);
        }
    }

    private async Task<CredentialSnapshot> EnsureLoadedAsync(CancellationToken cancellationToken)
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
            if (current is not null)
            {
                return current;
            }

            if (!store.DatabaseExists)
            {
                throw new InvalidOperationException("Trusted access storage has not been initialized by an administrator.");
            }

            using var lease = await store.AcquireMutationLockAsync(cancellationToken);
            EnsureWatching();
            return await ReloadSnapshotAsync(cancellationToken);
        }
        finally
        {
            mutationGate.Release();
        }
    }

    private async Task DisableDefaultAsync(
        TrustedAccessKeyStoreState current,
        CancellationToken cancellationToken)
    {
        var oldDefaultId = current.DefaultCredentialId;
        var updated = new TrustedAccessKeyStoreState(
            oldDefaultId is null
                ? CloneKeys(current.Keys)
                : current.Keys.Where(item => !item.Id.Equals(oldDefaultId, StringComparison.Ordinal)).ToArray(),
            false,
            null,
            current.LegacyAiMigrationCompleted);
        PublishSnapshot(updated);
        if (oldDefaultId is not null)
        {
            CancelRevocation(oldDefaultId);
        }

        keyFile.WriteAtomic(string.Empty);
        await store.SaveAsync(updated, current.Revision, cancellationToken);
        logger?.LogInformation("Trusted access default key {CredentialId} was disabled.", oldDefaultId);
    }

    private void EnsureWatching()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        changeSubscription ??= store.SubscribeToChanges(OnStoreChanged);
    }

    // Filesystem events include the owner's own commit. Coalesce events and reload
    // under both mutation locks before allowing further authentication.
    private void OnStoreChanged(Exception? notificationError)
    {
        var startRefresh = false;
        lock (changeSync)
        {
            if (disposed) return;
            // New requests must wait for authoritative state as soon as a change
            // is signalled, even while another mutation owns the gate.
            Volatile.Write(ref credentialSnapshot, null);
            refreshRequested = true;
            notificationFailure ??= notificationError;
            if (!refreshRunning)
            {
                refreshRunning = true;
                startRefresh = true;
            }
        }

        if (notificationError is not null) InvalidateSnapshot();
        if (startRefresh) _ = RefreshAfterStoreChangeAsync();
    }

    private async Task RefreshAfterStoreChangeAsync()
    {
        while (true)
        {
            await mutationGate.WaitAsync();
            try
            {
                Exception? failure;
                lock (changeSync)
                {
                    if (disposed)
                    {
                        refreshRunning = false;
                        return;
                    }
                    refreshRequested = false;
                    failure = notificationFailure;
                    notificationFailure = null;
                }
                if (failure is not null) throw failure;
                using var lease = await store.AcquireMutationLockAsync(CancellationToken.None);
                await ReloadSnapshotAsync(CancellationToken.None);
            }
            catch (Exception error)
            {
                InvalidateSnapshot();
                changeSubscription?.Dispose();
                changeSubscription = null;
                logger?.LogError(error, "Trusted access storage monitoring failed; cached credentials were invalidated.");
            }
            finally
            {
                mutationGate.Release();
            }

            lock (changeSync)
            {
                if (!refreshRequested)
                {
                    refreshRunning = false;
                    return;
                }
            }
        }
    }

    private async Task<CredentialSnapshot> ReloadSnapshotAsync(CancellationToken cancellationToken)
    {
        try
        {
            EnsureWatching();
            var state = await store.LoadAsync(cancellationToken);
            if (!state.LegacyAiMigrationCompleted)
                throw new InvalidOperationException("Trusted access storage requires explicit administrator initialization.");
            EnsureDefaultFileMatches(state);
            var current = Volatile.Read(ref credentialSnapshot);
            return current?.State.Revision == state.Revision ? current : PublishSnapshot(state);
        }
        catch
        {
            InvalidateSnapshot();
            throw;
        }
    }

    private void InvalidateSnapshot()
    {
        Volatile.Write(ref credentialSnapshot, null);
        foreach (var id in revocationTokens.Keys)
            CancelRevocation(id);
    }

    public void Dispose()
    {
        mutationGate.Wait();
        try
        {
            lock (changeSync) disposed = true;
            changeSubscription?.Dispose();
            changeSubscription = null;
            InvalidateSnapshot();
        }
        finally
        {
            mutationGate.Release();
        }
    }

    private void EnsureDefaultFileMatches(TrustedAccessKeyStoreState state)
    {
        if (!state.DefaultEnabled)
        {
            return;
        }

        var credential = state.Keys.FirstOrDefault(item =>
            item.Id.Equals(state.DefaultCredentialId, StringComparison.Ordinal));
        var key = keyFile.Read();
        if (credential is null || string.IsNullOrEmpty(key)
            || !CryptographicOperations.FixedTimeEquals(Hash(key), credential.SecretHash))
        {
            throw new InvalidDataException("The default key file does not match the persisted default credential.");
        }
    }

    private static TrustedAccessKeyStoreState MergeLegacyAiCredentials(
        TrustedAccessKeyStoreState existing,
        IReadOnlyList<StoredTrustedAccessKey> legacy)
    {
        var rows = existing.Keys.ToDictionary(static key => key.Id, StringComparer.Ordinal);
        foreach (var key in legacy)
        {
            if (rows.TryGetValue(key.Id, out var existingKey))
            {
                if (!existingKey.Purpose.Equals(key.Purpose, StringComparison.Ordinal)
                    || !CryptographicOperations.FixedTimeEquals(existingKey.SecretHash, key.SecretHash))
                {
                    throw new InvalidDataException("An existing trusted access key conflicts with an original AI credential.");
                }

                continue;
            }

            rows.Add(key.Id, key);
        }

        return new TrustedAccessKeyStoreState(
            rows.Values.OrderByDescending(static key => key.CreatedAt).ToArray(),
            existing.DefaultEnabled,
            existing.DefaultCredentialId,
            true);
    }

    private CredentialSnapshot PublishSnapshot(TrustedAccessKeyStoreState state)
    {
        var clonedState = state with { Keys = CloneKeys(state.Keys) };
        var summaries = clonedState.Keys
            .OrderByDescending(static item => item.CreatedAt)
            .ThenBy(static item => item.Id, StringComparer.Ordinal)
            .Select(item => ToSummary(item, clonedState)).ToArray();
        var byId = summaries.ToDictionary(static item => item.Id, StringComparer.Ordinal);
        var snapshot = clonedState.Keys.ToFrozenDictionary(
            static credential => CredentialLookup.Create(credential.Purpose, credential.SecretHash),
            credential => Task.FromResult<AccessKeySummary?>(byId[credential.Id]));

        foreach (var credential in clonedState.Keys)
        {
            revocationTokens.GetOrAdd(credential.Id, static _ => new CancellationTokenSource());
        }

        var published = new CredentialSnapshot(clonedState, snapshot, Array.AsReadOnly(summaries));
        Volatile.Write(ref credentialSnapshot, published);

        var nextIds = clonedState.Keys.Select(static key => key.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var removedId in revocationTokens.Keys)
        {
            if (!nextIds.Contains(removedId)) CancelRevocation(removedId);
        }

        return published;
    }

    private void CancelRevocation(string credentialId)
    {
        if (revocationTokens.TryRemove(credentialId, out var source))
        {
            try
            {
                source.Cancel();
            }
            catch (Exception error)
            {
                logger?.LogWarning(
                    error,
                    "Trusted access revocation callbacks failed for credential {CredentialId}.",
                    credentialId);
            }
            finally
            {
                source.Dispose();
            }
        }
    }

    private static TrustedAccessKeyStoreState DisableDefaultSnapshot(TrustedAccessKeyStoreState state)
    {
        var defaultId = state.DefaultCredentialId;
        return new TrustedAccessKeyStoreState(
            defaultId is null
                ? CloneKeys(state.Keys)
                : state.Keys.Where(item => !item.Id.Equals(defaultId, StringComparison.Ordinal)).ToArray(),
            false,
            null,
            state.LegacyAiMigrationCompleted);
    }

    private static TrustedAccessKeyStoreState CopyState(
        TrustedAccessKeyStoreState state,
        IReadOnlyList<StoredTrustedAccessKey> keys) =>
        new(CloneKeys(keys), state.DefaultEnabled, state.DefaultCredentialId, state.LegacyAiMigrationCompleted);

    private static IReadOnlyList<StoredTrustedAccessKey> CloneKeys(IEnumerable<StoredTrustedAccessKey> keys) =>
        keys.Select(static key => key with
        {
            Scopes = [.. key.Scopes],
            SecretHash = key.SecretHash.ToArray()
        }).ToArray();

    private static AccessKeySummary ToSummary(
        StoredTrustedAccessKey credential,
        TrustedAccessKeyStoreState state) =>
        new(
            credential.Id,
            credential.Name,
            credential.Purpose,
            Array.AsReadOnly(credential.Scopes.ToArray()),
            credential.CreatedAt,
            state.DefaultEnabled
                && state.DefaultCredentialId!.Equals(credential.Id, StringComparison.Ordinal));

    private static string NormalizePurpose(string purpose, string parameterName)
    {
        if (!AccessKeyPurposes.IsSupported(purpose))
        {
            throw new ArgumentException("Unsupported trusted access purpose.", parameterName);
        }

        return AccessKeyPurposes.Normalize(purpose);
    }

    private static string NormalizeName(string? name, string purpose)
    {
        var normalized = string.IsNullOrWhiteSpace(name)
            ? purpose == AccessKeyPurposes.Control ? "Resource Manager client" : $"{purpose} client"
            : name.Trim();
        if (normalized.Length > MaximumNameLength)
        {
            throw new ArgumentException($"Key name cannot exceed {MaximumNameLength} characters.", nameof(name));
        }

        return normalized;
    }

    private static string[] NormalizeScopes(string[] scopes)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        var normalized = scopes
            .Select(static scope => scope?.Trim() ?? string.Empty)
            .Where(static scope => scope.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static scope => scope, StringComparer.Ordinal)
            .ToArray();
        if (normalized.Any(static scope => !AccessKeyScopes.IsSupported(scope)))
        {
            throw new ArgumentException("The request contains an unsupported access scope.", nameof(scopes));
        }

        return normalized;
    }

    private static string CreateApiKey() =>
        KeyPrefix + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    private static byte[] Hash(string apiKey) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(apiKey));

    private static TrustedAccessKeyStoreState EmptyState(bool legacyAiMigrationCompleted) =>
        new([], false, null, legacyAiMigrationCompleted);

    private static (TrustedAccessKeyStoreState State, string ApiKey) CreateDefaultState(
        IEnumerable<StoredTrustedAccessKey> existingKeys,
        bool legacyAiMigrationCompleted)
    {
        var apiKey = CreateApiKey();
        var credential = new StoredTrustedAccessKey(
            Guid.NewGuid().ToString("N"),
            "Default control access",
            AccessKeyPurposes.Control,
            AccessKeyScopes.Default,
            Hash(apiKey),
            DateTimeOffset.UtcNow);
        return (
            new TrustedAccessKeyStoreState(
                CloneKeys(existingKeys).Append(credential).ToArray(),
                true,
                credential.Id,
                legacyAiMigrationCompleted),
            apiKey);
    }

    private sealed record CredentialSnapshot(
        TrustedAccessKeyStoreState State,
        FrozenDictionary<CredentialLookup, Task<AccessKeySummary?>> Credentials,
        IReadOnlyList<AccessKeySummary> Summaries);

    private readonly record struct CredentialLookup(string Purpose, ulong A, ulong B, ulong C, ulong D)
    {
        public static CredentialLookup Create(string purpose, ReadOnlySpan<byte> hash) => new(
            purpose.Equals(AccessKeyPurposes.Control, StringComparison.OrdinalIgnoreCase)
                ? AccessKeyPurposes.Control
                : purpose.Equals(AccessKeyPurposes.OpenAi, StringComparison.OrdinalIgnoreCase)
                    ? AccessKeyPurposes.OpenAi
                    : AccessKeyPurposes.Anthropic,
            BinaryPrimitives.ReadUInt64LittleEndian(hash),
            BinaryPrimitives.ReadUInt64LittleEndian(hash[8..]),
            BinaryPrimitives.ReadUInt64LittleEndian(hash[16..]),
            BinaryPrimitives.ReadUInt64LittleEndian(hash[24..]));
    }
}
