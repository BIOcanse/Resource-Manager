using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using ResourceManager.App.Application.TrustedAccess;
using ResourceManager.App.Infrastructure.Paths;
using ResourceManager.Shared.Runtime.TrustedAccess;

namespace ResourceManager.App.Infrastructure.Security.TrustedAccess;

public sealed class SqliteTrustedAccessKeyStore : ITrustedAccessKeyStore
{
    private const int SchemaVersion = 2;
    private const int MaximumCredentialCount = 10000;
    private readonly ITrustedAccessStorageFileBoundary fileBoundary;
    private readonly string connectionString;

    public SqliteTrustedAccessKeyStore(IHostEnvironment environment)
        : this(
            PackagePathResolver.ResolvePackageRoot(environment.ContentRootPath),
            new WindowsTrustedAccessStorageFileBoundary())
    {
    }

    public SqliteTrustedAccessKeyStore(string packageRoot)
        : this(packageRoot, new WindowsTrustedAccessStorageFileBoundary())
    {
    }

    public SqliteTrustedAccessKeyStore(
        string packageRoot,
        ITrustedAccessStorageFileBoundary fileBoundary)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageRoot);
        ArgumentNullException.ThrowIfNull(fileBoundary);
        PackageRoot = Path.GetFullPath(packageRoot);
        this.fileBoundary = fileBoundary;
        DatabasePath = Path.Combine(PackageRoot, "UserData", "Security", "access-keys.db");
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 5
        }.ToString();
    }

    public string PackageRoot { get; }

    public string DatabasePath { get; }

    public bool DatabaseExists => fileBoundary.Exists(DatabasePath);

    public async Task<IDisposable> AcquireMutationLockAsync(CancellationToken cancellationToken, bool ensureInitialized = false)
    {
        if (ensureInitialized) fileBoundary.EnsureSecurityDirectory(PackageRoot);
        fileBoundary.VerifySecurityDirectory(PackageRoot);
        var lockPath = DatabasePath + ".lock";
        if (!fileBoundary.Exists(lockPath))
        {
            if (!ensureInitialized)
                throw new FileNotFoundException("Trusted access storage requires explicit administrator initialization.", lockPath);
            try
            {
                fileBoundary.CreatePrivateFile(lockPath);
            }
            catch (IOException) when (fileBoundary.Exists(lockPath))
            {
                // Another owner created the same private lock file.
            }
        }

        fileBoundary.VerifyPrivateFile(lockPath);
        var deadline = Environment.TickCount64 + 5000;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33
                && Environment.TickCount64 < deadline)
            {
                await Task.Delay(25, cancellationToken);
            }
        }
    }

    public IDisposable SubscribeToChanges(Action<Exception?> changed)
    {
        fileBoundary.VerifySecurityDirectory(PackageRoot);
        return new StoreChangeSubscription(DatabasePath, changed);
    }

    public async Task<TrustedAccessKeyStoreState> LoadAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenExistingConnectionAsync(cancellationToken);
        await VerifyIntegrityAsync(connection, cancellationToken);
        await VerifySchemaAsync(connection, cancellationToken);

        var (defaultEnabled, defaultCredentialId, legacyAiMigrationCompleted, revision) =
            await ReadStateAsync(connection, cancellationToken);
        var keys = await ReadKeysAsync(connection, cancellationToken);
        var result = new TrustedAccessKeyStoreState(
            keys,
            defaultEnabled,
            defaultCredentialId,
            legacyAiMigrationCompleted) { Revision = revision };
        ValidateState(result);
        return result;
    }

    public async Task InitializeAsync(
        TrustedAccessKeyStoreState initialState,
        CancellationToken cancellationToken)
    {
        ValidateState(initialState);
        fileBoundary.EnsureSecurityDirectory(PackageRoot);
        fileBoundary.VerifySecurityDirectory(PackageRoot);
        if (DatabaseExists)
        {
            throw new IOException("The trusted access database already exists.");
        }

        foreach (var familyPath in GetDatabaseFamilyPaths())
        {
            if (fileBoundary.Exists(familyPath))
            {
                throw new IOException("Trusted access database sidecars exist without the database file.");
            }
        }

        fileBoundary.CreatePrivateFile(DatabasePath);
        await CreateSchemaAndWriteStateAsync(initialState, cancellationToken);
    }

    public async Task SaveAsync(
        TrustedAccessKeyStoreState state,
        string expectedRevision,
        CancellationToken cancellationToken)
    {
        ValidateState(state);
        if (state.Revision == expectedRevision)
            throw new InvalidOperationException("A trusted access update must publish a new revision.");
        await using var connection = await OpenExistingConnectionAsync(cancellationToken);
        await VerifyIntegrityAsync(connection, cancellationToken);
        await VerifySchemaAsync(connection, cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await using (var revision = connection.CreateCommand())
        {
            revision.Transaction = transaction;
            revision.CommandText = "SELECT revision FROM trusted_access_state WHERE id = 1;";
            if (!string.Equals(await revision.ExecuteScalarAsync(cancellationToken) as string,
                expectedRevision, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Trusted access storage changed; the stale update was rejected.");
            }
        }

        await ExecuteAsync(connection, transaction, "DELETE FROM trusted_access_state WHERE id = 1;", cancellationToken);
        await ExecuteAsync(connection, transaction, "DELETE FROM trusted_access_keys;", cancellationToken);
        await InsertKeysAsync(connection, transaction, state.Keys, cancellationToken);
        await InsertStateAsync(connection, transaction, state, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await VerifyIntegrityAsync(connection, cancellationToken);
    }

    public async Task RecoverAsync(
        TrustedAccessKeyStoreState initialState,
        CancellationToken cancellationToken)
    {
        ValidateState(initialState);
        fileBoundary.EnsureSecurityDirectory(PackageRoot);
        fileBoundary.VerifySecurityDirectory(PackageRoot);
        foreach (var familyPath in GetDatabaseFamilyPaths())
        {
            fileBoundary.DeletePrivateFile(familyPath);
        }

        await InitializeAsync(initialState, cancellationToken);
    }

    public async Task<IReadOnlyList<StoredTrustedAccessKey>> ReadLegacyAiCredentialsAsync(
        CancellationToken cancellationToken)
    {
        var legacyPath = Path.Combine(PackageRoot, "UserData", "Database", "resource-manager.db");
        TrustedAccessKeyFile.VerifyNoReparsePoints(legacyPath);
        if (!fileBoundary.Exists(legacyPath))
        {
            return [];
        }

        var legacyConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = legacyPath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 5
        }.ToString();
        await using var connection = new SqliteConnection(legacyConnectionString);
        await connection.OpenAsync(cancellationToken);
        await VerifyIntegrityAsync(connection, cancellationToken);
        if (!await TableExistsAsync(connection, "ai_gateway_credentials", cancellationToken))
        {
            return [];
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, display_name, compatibility_profile, secret_hash, created_utc_ticks
            FROM ai_gateway_credentials
            ORDER BY created_utc_ticks DESC, id;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var credentials = new List<StoredTrustedAccessKey>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var purpose = AccessKeyPurposes.Normalize(reader.GetString(2));
            if (purpose is not (AccessKeyPurposes.OpenAi or AccessKeyPurposes.Anthropic))
            {
                throw new InvalidDataException("The original AI credential store contains an unsupported profile.");
            }

            var secretHash = reader.GetFieldValue<byte[]>(3);
            if (secretHash.Length != 32)
            {
                throw new InvalidDataException("The original AI credential store contains an invalid secret hash.");
            }

            credentials.Add(new StoredTrustedAccessKey(
                reader.GetString(0),
                reader.GetString(1),
                purpose,
                [],
                secretHash.ToArray(),
                new DateTimeOffset(reader.GetInt64(4), TimeSpan.Zero)));
            if (credentials.Count > MaximumCredentialCount)
            {
                throw new InvalidDataException("The original AI credential store exceeds its supported size.");
            }
        }

        return credentials;
    }

    private async Task<SqliteConnection> OpenExistingConnectionAsync(CancellationToken cancellationToken)
    {
        fileBoundary.VerifySecurityDirectory(PackageRoot);
        if (!DatabaseExists)
        {
            throw new FileNotFoundException("Trusted access storage has not been initialized.", DatabasePath);
        }

        VerifyDatabaseFamilyAcl();
        var connection = new SqliteConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA synchronous=FULL;";
            await command.ExecuteNonQueryAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private async Task CreateSchemaAndWriteStateAsync(
        TrustedAccessKeyStoreState state,
        CancellationToken cancellationToken)
    {
        fileBoundary.VerifyPrivateFile(DatabasePath);
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys=ON; PRAGMA journal_mode=DELETE; PRAGMA synchronous=FULL;";
            await pragma.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                CREATE TABLE trusted_access_keys (
                    id TEXT NOT NULL PRIMARY KEY,
                    name TEXT NOT NULL,
                    purpose TEXT NOT NULL CHECK (purpose IN ('control', 'openai', 'anthropic')),
                    secret_hash BLOB NOT NULL CHECK (length(secret_hash) = 32),
                    created_utc_ticks INTEGER NOT NULL,
                    UNIQUE (purpose, secret_hash)
                ) STRICT;

                CREATE TABLE trusted_access_key_scopes (
                    credential_id TEXT NOT NULL,
                    scope TEXT NOT NULL,
                    PRIMARY KEY (credential_id, scope),
                    FOREIGN KEY (credential_id) REFERENCES trusted_access_keys(id) ON DELETE CASCADE
                ) STRICT;

                CREATE TABLE trusted_access_state (
                    id INTEGER NOT NULL PRIMARY KEY CHECK (id = 1),
                    default_enabled INTEGER NOT NULL CHECK (default_enabled IN (0, 1)),
                    default_credential_id TEXT NULL,
                    legacy_ai_migration_completed INTEGER NOT NULL CHECK (legacy_ai_migration_completed IN (0, 1)),
                    revision TEXT NOT NULL CHECK (length(revision) = 32),
                    CHECK ((default_enabled = 0 AND default_credential_id IS NULL)
                        OR (default_enabled = 1 AND default_credential_id IS NOT NULL)),
                    FOREIGN KEY (default_credential_id) REFERENCES trusted_access_keys(id)
                ) STRICT;

                CREATE INDEX ix_trusted_access_keys_purpose_created
                    ON trusted_access_keys(purpose, created_utc_ticks DESC);
                PRAGMA user_version=2;
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await InsertKeysAsync(connection, transaction, state.Keys, cancellationToken);
        await InsertStateAsync(connection, transaction, state, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await VerifyIntegrityAsync(connection, cancellationToken);
        VerifyDatabaseFamilyAcl();
    }

    private async Task InsertKeysAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<StoredTrustedAccessKey> keys,
        CancellationToken cancellationToken)
    {
        foreach (var credential in keys)
        {
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO trusted_access_keys(id, name, purpose, secret_hash, created_utc_ticks)
                    VALUES ($id, $name, $purpose, $secretHash, $createdAt);
                    """;
                command.Parameters.AddWithValue("$id", credential.Id);
                command.Parameters.AddWithValue("$name", credential.Name);
                command.Parameters.AddWithValue("$purpose", credential.Purpose);
                command.Parameters.Add("$secretHash", SqliteType.Blob).Value = credential.SecretHash;
                command.Parameters.AddWithValue("$createdAt", credential.CreatedAt.UtcTicks);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            foreach (var scope in credential.Scopes)
            {
                await using var scopeCommand = connection.CreateCommand();
                scopeCommand.Transaction = transaction;
                scopeCommand.CommandText = "INSERT INTO trusted_access_key_scopes(credential_id, scope) VALUES ($id, $scope);";
                scopeCommand.Parameters.AddWithValue("$id", credential.Id);
                scopeCommand.Parameters.AddWithValue("$scope", scope);
                await scopeCommand.ExecuteNonQueryAsync(cancellationToken);
            }
        }
    }

    private static async Task InsertStateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TrustedAccessKeyStoreState state,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO trusted_access_state(
                id, default_enabled, default_credential_id, legacy_ai_migration_completed, revision)
            VALUES (1, $enabled, $credentialId, $migrationCompleted, $revision);
            """;
        command.Parameters.AddWithValue("$enabled", state.DefaultEnabled ? 1 : 0);
        command.Parameters.AddWithValue("$credentialId", (object?)state.DefaultCredentialId ?? DBNull.Value);
        command.Parameters.AddWithValue("$migrationCompleted", state.LegacyAiMigrationCompleted ? 1 : 0);
        command.Parameters.AddWithValue("$revision", state.Revision);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<(bool Enabled, string? CredentialId, bool MigrationCompleted, string Revision)> ReadStateAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT default_enabled, default_credential_id, legacy_ai_migration_completed, revision
            FROM trusted_access_state WHERE id = 1;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidDataException("The trusted access state row is missing.");
        }

        return (
            reader.GetInt32(0) == 1,
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.GetInt32(2) == 1,
            reader.GetString(3));
    }

    private static async Task<IReadOnlyList<StoredTrustedAccessKey>> ReadKeysAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT credential.id, credential.name, credential.purpose, credential.secret_hash, credential.created_utc_ticks, scope.scope
            FROM trusted_access_keys AS credential
            LEFT JOIN trusted_access_key_scopes AS scope ON scope.credential_id = credential.id
            ORDER BY credential.created_utc_ticks DESC, credential.id, scope.scope;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var credentials = new List<StoredTrustedAccessKey>();
        string? currentId = null;
        string currentName = string.Empty;
        string currentPurpose = string.Empty;
        byte[] currentHash = [];
        long currentTicks = 0;
        var scopes = new List<string>();

        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetString(0);
            if (!id.Equals(currentId, StringComparison.Ordinal))
            {
                if (currentId is not null)
                {
                    AddCurrent();
                }

                currentId = id;
                currentName = reader.GetString(1);
                currentPurpose = reader.GetString(2);
                currentHash = reader.GetFieldValue<byte[]>(3).ToArray();
                currentTicks = reader.GetInt64(4);
                scopes = [];
            }

            if (!reader.IsDBNull(5))
            {
                scopes.Add(reader.GetString(5));
            }

            if (credentials.Count > MaximumCredentialCount)
            {
                throw new InvalidDataException("The trusted access database exceeds its supported credential count.");
            }
        }

        if (currentId is not null)
        {
            AddCurrent();
        }

        return credentials;

        void AddCurrent()
        {
            credentials.Add(new StoredTrustedAccessKey(
                currentId!,
                currentName,
                currentPurpose,
                [.. scopes],
                currentHash.ToArray(),
                new DateTimeOffset(currentTicks, TimeSpan.Zero)));
        }
    }

    private static async Task VerifySchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        var version = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
        if (version != SchemaVersion)
        {
            throw new InvalidDataException($"Unsupported trusted access database schema version {version}.");
        }

        await using var state = connection.CreateCommand();
        state.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('trusted_access_keys', 'trusted_access_key_scopes', 'trusted_access_state');";
        if (Convert.ToInt32(await state.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture) != 3)
        {
            throw new InvalidDataException("The trusted access database schema is incomplete.");
        }
    }

    private static async Task VerifyIntegrityAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA quick_check(1);";
        var result = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
        if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Trusted access database integrity check failed: {result ?? "<no result>"}.");
        }
    }

    private static async Task<bool> TableExistsAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name LIMIT 1;";
        command.Parameters.AddWithValue("$name", tableName);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static Task ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string commandText,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = commandText;
        return ExecuteAndDisposeAsync(command, cancellationToken);
    }

    private static async Task ExecuteAndDisposeAsync(SqliteCommand command, CancellationToken cancellationToken)
    {
        await using (command)
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private void VerifyDatabaseFamilyAcl()
    {
        foreach (var path in GetDatabaseFamilyPaths())
        {
            if (fileBoundary.Exists(path))
            {
                fileBoundary.VerifyPrivateFile(path);
            }
        }
    }

    private string[] GetDatabaseFamilyPaths() =>
    [
        DatabasePath,
        $"{DatabasePath}-wal",
        $"{DatabasePath}-shm",
        $"{DatabasePath}-journal"
    ];

    private static void ValidateState(TrustedAccessKeyStoreState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!Guid.TryParseExact(state.Revision, "N", out _))
        {
            throw new InvalidDataException("The trusted access state revision is invalid.");
        }
        if (state.Keys.Count > MaximumCredentialCount)
        {
            throw new InvalidDataException("The trusted access state exceeds its supported credential count.");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in state.Keys)
        {
            if (string.IsNullOrWhiteSpace(key.Id) || !ids.Add(key.Id)
                || string.IsNullOrWhiteSpace(key.Name)
                || !AccessKeyPurposes.IsSupported(key.Purpose)
                || key.SecretHash.Length != 32
                || key.Scopes.Distinct(StringComparer.Ordinal).Count() != key.Scopes.Length
                || key.Scopes.Any(static scope => !AccessKeyScopes.IsSupported(scope)))
            {
                throw new InvalidDataException("The trusted access state contains an invalid credential row.");
            }
        }

        if (state.DefaultEnabled != (state.DefaultCredentialId is not null))
        {
            throw new InvalidDataException("The trusted access default state is inconsistent.");
        }

        var defaultKey = state.DefaultEnabled
            ? state.Keys.FirstOrDefault(key => key.Id.Equals(state.DefaultCredentialId, StringComparison.Ordinal))
            : null;
        if (state.DefaultEnabled
            && (defaultKey is null
                || !defaultKey.Purpose.Equals(AccessKeyPurposes.Control, StringComparison.Ordinal)
                || !defaultKey.Scopes.ToHashSet(StringComparer.Ordinal).SetEquals(AccessKeyScopes.Default)))
        {
            throw new InvalidDataException("The trusted access default key reference is invalid.");
        }
    }

    private sealed class StoreChangeSubscription : IDisposable
    {
        private readonly FileSystemWatcher watcher;
        private readonly Action<Exception?> changed;
        private readonly string databaseName;

        public StoreChangeSubscription(string databasePath, Action<Exception?> changed)
        {
            this.changed = changed;
            databaseName = Path.GetFileName(databasePath);
            watcher = new FileSystemWatcher(Path.GetDirectoryName(databasePath)!)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Security
            };
            watcher.Changed += OnChanged;
            watcher.Created += OnChanged;
            watcher.Deleted += OnChanged;
            watcher.Renamed += OnChanged;
            watcher.Error += OnError;
            try
            {
                watcher.EnableRaisingEvents = true;
            }
            catch
            {
                watcher.Dispose();
                throw;
            }
        }

        private void OnChanged(object sender, FileSystemEventArgs args)
        {
            if (IsStateFile(args.Name) || args is RenamedEventArgs renamed && IsStateFile(renamed.OldName))
                changed(null);
        }

        private bool IsStateFile(string? name) =>
            string.Equals(name, databaseName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, databaseName + "-wal", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, databaseName + "-shm", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, databaseName + "-journal", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "default-key", StringComparison.OrdinalIgnoreCase);

        private void OnError(object sender, ErrorEventArgs args) => changed(args.GetException());

        public void Dispose() => watcher.Dispose();
    }
}
