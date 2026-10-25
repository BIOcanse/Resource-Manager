namespace ResourceManager.App.Application.TrustedAccess;

public sealed record StoredTrustedAccessKey(
    string Id,
    string Name,
    string Purpose,
    string[] Scopes,
    byte[] SecretHash,
    DateTimeOffset CreatedAt);

public sealed record TrustedAccessKeyStoreState(
    IReadOnlyList<StoredTrustedAccessKey> Keys,
    bool DefaultEnabled,
    string? DefaultCredentialId,
    bool LegacyAiMigrationCompleted)
{
    public string Revision { get; init; } = Guid.NewGuid().ToString("N");
}

public interface ITrustedAccessKeyStore
{
    string PackageRoot { get; }

    bool DatabaseExists { get; }

    // The owner holds this across database and default-key file mutations.
    Task<IDisposable> AcquireMutationLockAsync(CancellationToken cancellationToken, bool ensureInitialized = false);

    IDisposable SubscribeToChanges(Action<Exception?> changed);

    Task<TrustedAccessKeyStoreState> LoadAsync(CancellationToken cancellationToken);

    Task InitializeAsync(
        TrustedAccessKeyStoreState initialState,
        CancellationToken cancellationToken);

    Task SaveAsync(
        TrustedAccessKeyStoreState state,
        string expectedRevision,
        CancellationToken cancellationToken);

    Task RecoverAsync(
        TrustedAccessKeyStoreState initialState,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<StoredTrustedAccessKey>> ReadLegacyAiCredentialsAsync(
        CancellationToken cancellationToken);
}

public interface ITrustedAccessStorageFileBoundary
{
    bool Exists(string path);

    void EnsureSecurityDirectory(string packageRoot);

    void VerifySecurityDirectory(string packageRoot);

    void CreatePrivateFile(string path);

    void VerifyPrivateFile(string path);

    void DeletePrivateFile(string path);
}
