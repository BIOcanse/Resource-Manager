using System.Security.Cryptography;
using ResourceManager.App.Application.TrustedAccess;
using ResourceManager.App.Application.TrustedAccess;
using ResourceManager.App.Domain.TrustedAccess;
using Microsoft.Data.Sqlite;
using ResourceManager.App.Infrastructure.Security.TrustedAccess;
using ResourceManager.Shared.Runtime.TrustedAccess;

namespace Resource_Manager_APP.Tests;

public sealed class TrustedAccessKeyStoreTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MappedRevisionDetectsRecoveryWithEveryNotificationSuppressed(bool streamFirst)
    {
        using var temporary = new TrustedAccessTemporaryDirectory();
        var boundary = new TestTrustedAccessStorageFileBoundary();
        using var runningStore = new SqliteTrustedAccessKeyStore(temporary.Root, boundary);
        using var adminStore = new SqliteTrustedAccessKeyStore(temporary.Root, boundary);
        var keyFile = new TrustedAccessTestKeyFileAccess();
        using var running = new TrustedAccessKeyService(new SilentKeyStore(runningStore), keyFile);
        await running.InitializeAsync(false, CancellationToken.None);
        var defaultKey = keyFile.Value!;
        var defaultSummary = await running.AuthenticateAsync(AccessKeyPurposes.Control, defaultKey, CancellationToken.None);
        var ordinary = await running.CreateAsync(new AccessKeyCreateRequest("independent", AccessKeyPurposes.Control,
            [AccessKeyScopes.Read]), CancellationToken.None);
        var oldDefaultToken = running.GetRevocationToken(defaultSummary!.Id);
        var oldToken = running.GetRevocationToken(ordinary.Credential.Id);
        var oldState = await runningStore.LoadAsync(CancellationToken.None);
        using var administrator = new TrustedAccessKeyService(new SilentKeyStore(adminStore), keyFile);

        await administrator.InitializeAsync(true, CancellationToken.None);

        Assert.False(runningStore.IsRevisionCurrent(oldState.Revision));
        if (streamFirst)
            Assert.False(await running.ValidateCredentialAsync(ordinary.Credential.Id, CancellationToken.None));
        else
            Assert.Null(await running.AuthenticateAsync(AccessKeyPurposes.Control, defaultKey, CancellationToken.None));
        Assert.True(oldToken.IsCancellationRequested);
        Assert.True(oldDefaultToken.IsCancellationRequested);
        Assert.Null(await running.AuthenticateAsync(AccessKeyPurposes.Control, ordinary.ApiKey, CancellationToken.None));
        Assert.False(await running.ValidateCredentialAsync(defaultSummary.Id, CancellationToken.None));
        Assert.NotNull(await running.AuthenticateAsync(AccessKeyPurposes.Control, keyFile.Value, CancellationToken.None));
    }

    [Fact]
    public async Task InterruptedMappedRevisionRequiresALockHeldDatabaseReloadAndPreservesUnchangedKeys()
    {
        using var temporary = new TrustedAccessTemporaryDirectory();
        using var store = new SqliteTrustedAccessKeyStore(temporary.Root, new TestTrustedAccessStorageFileBoundary());
        var keyFile = new TrustedAccessTestKeyFileAccess();
        using var running = new TrustedAccessKeyService(new SilentKeyStore(store), keyFile);
        await running.InitializeAsync(false, CancellationToken.None);
        var summary = await running.AuthenticateAsync(AccessKeyPurposes.Control, keyFile.Value, CancellationToken.None);
        var token = running.GetRevocationToken(summary!.Id);
        var revision = (await store.LoadAsync(CancellationToken.None)).Revision;
        // Releasing without a verified load or commit simulates a writer that
        // died after marking the revision unavailable.
        using (await store.AcquireMutationLockAsync(CancellationToken.None))
            Assert.False(store.IsRevisionCurrent(revision));
        Assert.False(store.IsRevisionCurrent(revision));

        Assert.Same(summary, await running.AuthenticateAsync(AccessKeyPurposes.Control, keyFile.Value, CancellationToken.None));

        Assert.True(store.IsRevisionCurrent(revision));
        Assert.False(token.IsCancellationRequested);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedDefaultReplacementKeepsBothOwnersAtTheSameCommittedRevision(bool failAfterReplacement)
    {
        using var temporary = new TrustedAccessTemporaryDirectory();
        var boundary = new TestTrustedAccessStorageFileBoundary();
        using var runningStore = new SqliteTrustedAccessKeyStore(temporary.Root, boundary);
        using var adminStore = new SqliteTrustedAccessKeyStore(temporary.Root, boundary);
        var keyFile = new TrustedAccessTestKeyFileAccess();
        using var running = new TrustedAccessKeyService(new SilentKeyStore(runningStore), keyFile);
        await running.InitializeAsync(false, CancellationToken.None);
        var key = keyFile.Value!;
        var original = await running.AuthenticateAsync(AccessKeyPurposes.Control, key, CancellationToken.None);
        var token = running.GetRevocationToken(original!.Id);
        var state = await runningStore.LoadAsync(CancellationToken.None);
        using var administrator = new TrustedAccessKeyService(new SilentKeyStore(adminStore), keyFile);
        var adminOriginal = await administrator.AuthenticateAsync(AccessKeyPurposes.Control, key, CancellationToken.None);
        var adminToken = administrator.GetRevocationToken(original.Id);
        if (failAfterReplacement)
            keyFile.WriteAfterReplacementFailureOnce = new IOException("final file verification failed after replacement");
        else
            keyFile.WriteFailure = new IOException("atomic file replacement failed");

        await Assert.ThrowsAsync<IOException>(() => administrator.RotateAsync(original.Id, CancellationToken.None));

        Assert.True(runningStore.IsRevisionCurrent(state.Revision));
        Assert.True(adminStore.IsRevisionCurrent(state.Revision));
        Assert.Equal(state.Revision, (await runningStore.LoadAsync(CancellationToken.None)).Revision);
        Assert.Equal(key, keyFile.Value);
        Assert.Same(original, await running.AuthenticateAsync(AccessKeyPurposes.Control, key, CancellationToken.None));
        Assert.Same(adminOriginal, await administrator.AuthenticateAsync(AccessKeyPurposes.Control, key, CancellationToken.None));
        Assert.False(token.IsCancellationRequested);
        Assert.False(adminToken.IsCancellationRequested);
    }

    [Fact]
    public async Task FailedRestorationKeepsTheSharedRevisionUnavailableToEveryOwner()
    {
        using var temporary = new TrustedAccessTemporaryDirectory();
        var boundary = new TestTrustedAccessStorageFileBoundary();
        using var runningStore = new SqliteTrustedAccessKeyStore(temporary.Root, boundary);
        using var adminStore = new SqliteTrustedAccessKeyStore(temporary.Root, boundary);
        var keyFile = new TrustedAccessTestKeyFileAccess();
        using var running = new TrustedAccessKeyService(new SilentKeyStore(runningStore), keyFile);
        await running.InitializeAsync(false, CancellationToken.None);
        var key = keyFile.Value!;
        var original = await running.AuthenticateAsync(AccessKeyPurposes.Control, key, CancellationToken.None);
        var token = running.GetRevocationToken(original!.Id);
        var state = await runningStore.LoadAsync(CancellationToken.None);
        using var administrator = new TrustedAccessKeyService(new SilentKeyStore(adminStore), keyFile);
        await administrator.AuthenticateAsync(AccessKeyPurposes.Control, key, CancellationToken.None);
        var adminToken = administrator.GetRevocationToken(original.Id);
        keyFile.WriteAfterReplacementFailureOnce = new IOException("verification failed after replacement");
        keyFile.SubsequentWriteFailure = new IOException("restoration was denied");

        await Assert.ThrowsAsync<IOException>(() => administrator.RotateAsync(original.Id, CancellationToken.None));

        Assert.NotEqual(key, keyFile.Value);
        Assert.True(adminToken.IsCancellationRequested);
        Assert.False(runningStore.IsRevisionCurrent(state.Revision));
        Assert.False(adminStore.IsRevisionCurrent(state.Revision));
        await Assert.ThrowsAsync<InvalidDataException>(() => running.AuthenticateAsync(
            AccessKeyPurposes.Control, key, CancellationToken.None));
        Assert.True(token.IsCancellationRequested);
        Assert.False(runningStore.IsRevisionCurrent(state.Revision));
        await Assert.ThrowsAsync<InvalidDataException>(() => running.ValidateCredentialAsync(original.Id, CancellationToken.None).AsTask());
        Assert.False(runningStore.IsRevisionCurrent(state.Revision));
        Assert.Equal(state.Revision, (await runningStore.LoadAsync(CancellationToken.None)).Revision);
    }

    [Fact]
    public async Task MissingMappedRevisionIsNotRecreatedByAuthenticationAndExplicitInitializationPreservesDisabledDefault()
    {
        using var temporary = new TrustedAccessTemporaryDirectory();
        var boundary = new TestTrustedAccessStorageFileBoundary();
        var keyFile = new TrustedAccessTestKeyFileAccess();
        string revisionPath;
        using (var originalStore = new SqliteTrustedAccessKeyStore(temporary.Root, boundary))
        using (var original = new TrustedAccessKeyService(originalStore, keyFile))
        {
            await original.InitializeAsync(false, CancellationToken.None);
            await original.SetDefaultAsync(false, CancellationToken.None);
            revisionPath = originalStore.DatabasePath + ".revision";
        }
        File.Delete(revisionPath);
        using var store = new SqliteTrustedAccessKeyStore(temporary.Root, boundary);
        using var owner = new TrustedAccessKeyService(store, keyFile);

        await Assert.ThrowsAsync<FileNotFoundException>(() => owner.ListAsync(CancellationToken.None));
        Assert.False(File.Exists(revisionPath));
        await owner.InitializeAsync(false, CancellationToken.None);

        Assert.True(File.Exists(revisionPath));
        Assert.Empty(await owner.ListAsync(CancellationToken.None));
        Assert.Equal(string.Empty, keyFile.Value);
        Assert.False((await store.LoadAsync(CancellationToken.None)).DefaultEnabled);
    }

    [Fact]
    public async Task ActiveMappedRevisionHandlesDenyDeletionAndReplacement()
    {
        using var temporary = new TrustedAccessTemporaryDirectory();
        using var store = new SqliteTrustedAccessKeyStore(temporary.Root, new TestTrustedAccessStorageFileBoundary());
        using var owner = new TrustedAccessKeyService(store, new TrustedAccessTestKeyFileAccess());
        await owner.InitializeAsync(false, CancellationToken.None);
        var path = store.DatabasePath + ".revision";

        Assert.Throws<IOException>(() => File.Delete(path));
        Assert.Throws<IOException>(() => File.Move(path, path + ".orphan"));
        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + ".orphan"));
    }

    [Fact]
    public async Task OrdinaryLeaseAcquisitionDoesNotCreateMissingSecurityStorage()
    {
        using var temporary = new TrustedAccessTemporaryDirectory();
        var boundary = new TestTrustedAccessStorageFileBoundary();
        using var store = new SqliteTrustedAccessKeyStore(temporary.Root, boundary);
        var directory = Path.GetDirectoryName(store.DatabasePath)!;

        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => store.AcquireMutationLockAsync(CancellationToken.None));
        Assert.False(Directory.Exists(directory));
        boundary.EnsureSecurityDirectory(temporary.Root);
        await Assert.ThrowsAsync<FileNotFoundException>(() => store.AcquireMutationLockAsync(CancellationToken.None));
        Assert.False(File.Exists(store.DatabasePath + ".lock"));

        using var lease = await store.AcquireMutationLockAsync(CancellationToken.None, ensureInitialized: true);
        Assert.True(File.Exists(store.DatabasePath + ".lock"));
        Assert.False(store.DatabaseExists);
    }

    [Fact]
    public async Task CorruptionNotificationInvalidatesAWarmOwnerAndStopsItsStreams()
    {
        using var temporary = new TrustedAccessTemporaryDirectory();
        using var store = new SqliteTrustedAccessKeyStore(temporary.Root, new TestTrustedAccessStorageFileBoundary());
        var keyFile = new TrustedAccessTestKeyFileAccess();
        using var owner = new TrustedAccessKeyService(store, keyFile);
        await owner.InitializeAsync(false, CancellationToken.None);
        var credential = (await owner.ListAsync(CancellationToken.None)).Single();
        var token = owner.GetRevocationToken(credential.Id);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var callback = token.Register(() => stopped.TrySetResult());

        await File.WriteAllTextAsync(store.DatabasePath, "corrupt database");

        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<Exception>(() => owner.AuthenticateAsync(AccessKeyPurposes.Control, keyFile.Value, CancellationToken.None));
        Assert.True(token.IsCancellationRequested);
    }

    [Fact]
    public async Task RecoveryInAnotherOwnerRevokesCachedCredentialsAndActiveStreamsAndRejectsStaleWrites()
    {
        using var temporary = new TrustedAccessTemporaryDirectory();
        var boundary = new TestTrustedAccessStorageFileBoundary();
        using var runningStore = new SqliteTrustedAccessKeyStore(temporary.Root, boundary);
        using var adminStore = new SqliteTrustedAccessKeyStore(temporary.Root, boundary);
        var keyFile = new TrustedAccessTestKeyFileAccess();
        using var running = new TrustedAccessKeyService(runningStore, keyFile);
        await running.InitializeAsync(false, CancellationToken.None);
        var defaultKey = keyFile.Value!;
        var ordinary = await running.CreateAsync(
            new AccessKeyCreateRequest("running client", AccessKeyPurposes.Control, [AccessKeyScopes.Read]),
            CancellationToken.None);
        var cachedDefault = await running.AuthenticateAsync(AccessKeyPurposes.Control, defaultKey, CancellationToken.None);
        Assert.NotNull(cachedDefault);
        var oldToken = running.GetRevocationToken(ordinary.Credential.Id);
        var defaultToken = running.GetRevocationToken(cachedDefault.Id);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stopCallback = oldToken.Register(() => stopped.TrySetResult());
        var staleState = await runningStore.LoadAsync(CancellationToken.None);
        using var administrator = new TrustedAccessKeyService(adminStore, keyFile);

        await administrator.InitializeAsync(true, CancellationToken.None);

        // No request is needed to terminate a subscription: the filesystem signal reloads the live owner.
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(defaultToken.IsCancellationRequested);
        Assert.Null(await running.AuthenticateAsync(AccessKeyPurposes.Control, defaultKey, CancellationToken.None));
        Assert.Null(await running.AuthenticateAsync(AccessKeyPurposes.Control, ordinary.ApiKey, CancellationToken.None));
        Assert.NotNull(await running.AuthenticateAsync(AccessKeyPurposes.Control, keyFile.Value, CancellationToken.None));
        using (await runningStore.AcquireMutationLockAsync(CancellationToken.None))
            await Assert.ThrowsAsync<InvalidOperationException>(() => runningStore.SaveAsync(
                staleState with { Revision = Guid.NewGuid().ToString("N") }, staleState.Revision, CancellationToken.None));

        await running.CreateAsync(new AccessKeyCreateRequest("after recovery", AccessKeyPurposes.Control, [AccessKeyScopes.Read]), CancellationToken.None);
        Assert.DoesNotContain((await runningStore.LoadAsync(CancellationToken.None)).Keys,
            key => key.Id == ordinary.Credential.Id || key.Id == cachedDefault.Id);
    }

    [Fact]
    public async Task MutationLeaseSerializesIndependentStoreInstances()
    {
        using var temporary = new TrustedAccessTemporaryDirectory();
        var boundary = new TestTrustedAccessStorageFileBoundary();
        using var first = new SqliteTrustedAccessKeyStore(temporary.Root, boundary);
        using var second = new SqliteTrustedAccessKeyStore(temporary.Root, boundary);
        using var lease = await first.AcquireMutationLockAsync(CancellationToken.None, ensureInitialized: true);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second.AcquireMutationLockAsync(cancellation.Token));
    }

    [Fact]
    public async Task RestartLoadsCommittedKeysAndDoesNotReenableRevokedDefault()
    {
        using var temporary = new TrustedAccessTemporaryDirectory();
        using var store = new SqliteTrustedAccessKeyStore(temporary.Root, new TestTrustedAccessStorageFileBoundary());
        var keyFile = new TrustedAccessTestKeyFileAccess();
        using var first = new TrustedAccessKeyService(store, keyFile);
        await first.InitializeAsync(recover: false, cancellationToken: CancellationToken.None);
        var originalDefault = Assert.Single((await first.ListAsync(CancellationToken.None))
            .Where(static key => key.IsDefault));
        var originalApiKey = keyFile.Value!;
        var created = await first.CreateAsync(
            new ResourceManager.App.Domain.TrustedAccess.AccessKeyCreateRequest(
                "persistent client",
                AccessKeyPurposes.Control,
                [AccessKeyScopes.Read]),
            CancellationToken.None);

        Assert.Null(await first.SetDefaultAsync(enabled: false, cancellationToken: CancellationToken.None));

        using var restarted = new TrustedAccessKeyService(store, keyFile);
        Assert.Null(await restarted.AuthenticateAsync(
            AccessKeyPurposes.Control,
            originalApiKey,
            CancellationToken.None));
        Assert.NotNull(await restarted.AuthenticateAsync(
            AccessKeyPurposes.Control,
            created.ApiKey,
            CancellationToken.None));
        Assert.Empty((await restarted.ListAsync(CancellationToken.None)).Where(static key => key.IsDefault));

        await restarted.InitializeAsync(recover: false, cancellationToken: CancellationToken.None);
        Assert.Empty((await restarted.ListAsync(CancellationToken.None)).Where(static key => key.IsDefault));
        Assert.DoesNotContain((await restarted.ListAsync(CancellationToken.None)), key => key.Id == originalDefault.Id);
    }

    [Fact]
    public async Task MissingOrCorruptDatabaseIsNotCreatedOrRecoveredByAuthentication()
    {
        using var temporary = new TrustedAccessTemporaryDirectory();
        var boundary = new TestTrustedAccessStorageFileBoundary();
        using var store = new SqliteTrustedAccessKeyStore(temporary.Root, boundary);
        var keyFile = new TrustedAccessTestKeyFileAccess();
        using var owner = new TrustedAccessKeyService(store, keyFile);

        await Assert.ThrowsAsync<InvalidOperationException>(() => owner.AuthenticateAsync(
            AccessKeyPurposes.Control,
            "rm_sk_" + new string('a', 64),
            CancellationToken.None));
        Assert.False(store.DatabaseExists);

        boundary.EnsureSecurityDirectory(temporary.Root);
        File.WriteAllText(store.DatabasePath, "not a database");
        var bytesBefore = File.ReadAllBytes(store.DatabasePath);
        using var corruptOwner = new TrustedAccessKeyService(store, keyFile);
        await Assert.ThrowsAnyAsync<Exception>(() => corruptOwner.ListAsync(CancellationToken.None));
        await Assert.ThrowsAnyAsync<Exception>(() => corruptOwner.InitializeAsync(
            recover: false,
            cancellationToken: CancellationToken.None));
        Assert.Equal(bytesBefore, File.ReadAllBytes(store.DatabasePath));

        await corruptOwner.InitializeAsync(recover: true, cancellationToken: CancellationToken.None);
        Assert.True(store.DatabaseExists);
        Assert.False(string.IsNullOrEmpty(keyFile.Value));
        Assert.Single((await corruptOwner.ListAsync(CancellationToken.None)).Where(static key => key.IsDefault));
    }

    [Fact]
    public async Task ExplicitInitializationImportsAiRowsFromReadOnlyBusinessDatabase()
    {
        using var temporary = new TrustedAccessTemporaryDirectory();
        var legacyPath = Path.Combine(temporary.Root, "UserData", "Database", "resource-manager.db");
        Directory.CreateDirectory(Path.GetDirectoryName(legacyPath)!);
        const string legacyApiKey = "rm_sk_0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var legacyHash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(legacyApiKey));
        var createdAt = DateTimeOffset.UtcNow.AddDays(-11);
        await CreateLegacyDatabaseAsync(legacyPath, legacyHash, createdAt);
        var originalBytes = SHA256.HashData(File.ReadAllBytes(legacyPath));

        using var store = new SqliteTrustedAccessKeyStore(temporary.Root, new TestTrustedAccessStorageFileBoundary());
        var keyFile = new TrustedAccessTestKeyFileAccess();
        using var owner = new TrustedAccessKeyService(store, keyFile);
        await owner.InitializeAsync(recover: false, cancellationToken: CancellationToken.None);

        var imported = (await owner.ListAsync(CancellationToken.None))
            .Single(key => key.Id == "legacy-ai-id");
        Assert.Equal("migrated AI key", imported.Name);
        Assert.Equal(AccessKeyPurposes.OpenAi, imported.Purpose);
        Assert.Equal(createdAt, imported.CreatedAt);
        Assert.Empty(imported.Scopes);
        Assert.Equal("legacy-ai-id", (await owner.AuthenticateAsync(
            AccessKeyPurposes.OpenAi,
            legacyApiKey,
            CancellationToken.None))!.Id);

        using var restarted = new TrustedAccessKeyService(store, keyFile);
        Assert.Equal("legacy-ai-id", (await restarted.AuthenticateAsync(
            AccessKeyPurposes.OpenAi,
            legacyApiKey,
            CancellationToken.None))!.Id);
        Assert.Equal(originalBytes, SHA256.HashData(File.ReadAllBytes(legacyPath)));
    }

    private static async Task CreateLegacyDatabaseAsync(
        string path,
        byte[] secretHash,
        DateTimeOffset createdAt)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE ai_gateway_credentials (
                id TEXT NOT NULL PRIMARY KEY,
                display_name TEXT NOT NULL,
                compatibility_profile TEXT NOT NULL,
                secret_hash BLOB NOT NULL,
                created_utc_ticks INTEGER NOT NULL
            ) STRICT;
            INSERT INTO ai_gateway_credentials(
                id, display_name, compatibility_profile, secret_hash, created_utc_ticks)
            VALUES ('legacy-ai-id', 'migrated AI key', 'openai', $hash, $created);
            """;
        command.Parameters.Add("$hash", SqliteType.Blob).Value = secretHash;
        command.Parameters.AddWithValue("$created", createdAt.UtcTicks);
        await command.ExecuteNonQueryAsync();
    }

    private sealed class SilentKeyStore(ITrustedAccessKeyStore inner) : ITrustedAccessKeyStore
    {
        public string PackageRoot => inner.PackageRoot;
        public bool DatabaseExists => inner.DatabaseExists;
        public bool IsRevisionCurrent(string revision) => inner.IsRevisionCurrent(revision);
        public void InvalidateRevision() => inner.InvalidateRevision();
        public Task<IDisposable> AcquireMutationLockAsync(CancellationToken cancellationToken, bool ensureInitialized = false)
            => inner.AcquireMutationLockAsync(cancellationToken, ensureInitialized);
        public IDisposable SubscribeToChanges(Action<Exception?> changed) => new SilentSubscription();
        public Task<TrustedAccessKeyStoreState> LoadAsync(CancellationToken cancellationToken) => inner.LoadAsync(cancellationToken);
        public Task InitializeAsync(TrustedAccessKeyStoreState state, CancellationToken cancellationToken) => inner.InitializeAsync(state, cancellationToken);
        public Task SaveAsync(TrustedAccessKeyStoreState state, string expectedRevision, CancellationToken cancellationToken)
            => inner.SaveAsync(state, expectedRevision, cancellationToken);
        public Task RecoverAsync(TrustedAccessKeyStoreState state, CancellationToken cancellationToken) => inner.RecoverAsync(state, cancellationToken);
        public Task<IReadOnlyList<StoredTrustedAccessKey>> ReadLegacyAiCredentialsAsync(CancellationToken cancellationToken)
            => inner.ReadLegacyAiCredentialsAsync(cancellationToken);
        private sealed class SilentSubscription : IDisposable { public void Dispose() { } }
    }

    internal sealed class TestTrustedAccessStorageFileBoundary : ITrustedAccessStorageFileBoundary
    {
        public bool Exists(string path)
        {
            try
            {
                _ = File.GetAttributes(path);
                return true;
            }
            catch (FileNotFoundException)
            {
                return false;
            }
            catch (DirectoryNotFoundException)
            {
                return false;
            }
        }

        public void EnsureSecurityDirectory(string packageRoot) =>
            Directory.CreateDirectory(Path.Combine(packageRoot, "UserData", "Security"));

        public void VerifySecurityDirectory(string packageRoot)
        {
            if (!Directory.Exists(Path.Combine(packageRoot, "UserData", "Security")))
            {
                throw new DirectoryNotFoundException();
            }
        }

        public void CreatePrivateFile(string path)
        {
            using var _ = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        }

        public void VerifyPrivateFile(string path)
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                throw new UnauthorizedAccessException("Test store does not follow reparse points.");
            }
        }

        public void DeletePrivateFile(string path)
        {
            if (Exists(path))
            {
                VerifyPrivateFile(path);
                File.Delete(path);
            }
        }
    }
}

internal sealed class TrustedAccessTemporaryDirectory : IDisposable
{
    private const int MaximumEntries = 4096;

    public TrustedAccessTemporaryDirectory()
    {
        Root = Path.Combine(Path.GetTempPath(), $"trusted-access-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public void Dispose()
    {
        if (!Directory.Exists(Root))
        {
            return;
        }

        var fullRoot = Path.GetFullPath(Root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var pending = new Stack<string>();
        var postOrder = new List<string>();
        var visited = 0;
        pending.Push(Path.GetFullPath(Root));
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            postOrder.Add(directory);
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                visited++;
                if (visited > MaximumEntries)
                {
                    throw new IOException("Temporary test directory exceeded its cleanup bound.");
                }

                var fullEntry = Path.GetFullPath(entry);
                if (!fullEntry.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException("Temporary test cleanup path escaped its root.");
                }

                var attributes = File.GetAttributes(fullEntry);
                var isDirectory = (attributes & FileAttributes.Directory) != 0;
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    if (isDirectory)
                    {
                        Directory.Delete(fullEntry, recursive: false);
                    }
                    else
                    {
                        File.Delete(fullEntry);
                    }
                }
                else if (isDirectory)
                {
                    pending.Push(fullEntry);
                }
                else
                {
                    File.Delete(fullEntry);
                }
            }
        }

        for (var index = postOrder.Count - 1; index >= 0; index--)
        {
            Directory.Delete(postOrder[index], recursive: false);
        }
    }
}
