using Microsoft.Data.Sqlite;
using ResourceManager.App.Infrastructure.Paths;
using ResourceManager.App.Infrastructure.Persistence.Schema;

namespace ResourceManager.App.Infrastructure.Persistence;

public sealed class ResourceManagerDatabase
{
    private readonly SemaphoreSlim initializationGate = new(1, 1);
    private readonly string connectionString;
    private readonly string initializationConnectionString;
    private readonly ILogger<ResourceManagerDatabase>? logger;
    private ResourceManagerDatabaseInitializationState? initializationState;
    private bool initialized;

    public ResourceManagerDatabase(
        IHostEnvironment environment,
        ILogger<ResourceManagerDatabase>? logger = null)
    {
        var packageRoot = PackagePathResolver.ResolvePackageRoot(environment.ContentRootPath);
        DatabasePath = Path.Combine(packageRoot, "UserData", "Database", "resource-manager.db");
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
            DefaultTimeout = 5
        }.ToString();
        initializationConnectionString = new SqliteConnectionStringBuilder(connectionString)
        {
            Pooling = false
        }.ToString();
        this.logger = logger;
    }

    public string DatabasePath { get; }

    public ResourceManagerDatabaseInitializationState? InitializationState
        => Volatile.Read(ref initializationState);

    public async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref initialized))
        {
            return;
        }

        await initializationGate.WaitAsync(cancellationToken);
        try
        {
            if (initialized)
            {
                return;
            }

            var existedAtStart = File.Exists(DatabasePath);
            try
            {
                await InitializeDatabaseAsync(existedAtStart, cancellationToken);
                Volatile.Write(
                    ref initializationState,
                    new ResourceManagerDatabaseInitializationState(
                        existedAtStart
                            ? ResourceManagerDatabaseInitializationDisposition.OpenedExisting
                            : ResourceManagerDatabaseInitializationDisposition.CreatedNew,
                        DatabasePath,
                        DateTimeOffset.UtcNow));
            }
            catch (Exception exception) when (
                existedAtStart && SqliteDatabaseRecovery.IsConfirmedCorruption(exception))
            {
                var artifact = SqliteDatabaseRecovery.QuarantineFamily(
                    DatabasePath,
                    DateTimeOffset.UtcNow);
                logger?.LogError(
                    exception,
                    "The Resource Manager SQLite database was corrupt and quarantined at {QuarantinePath}.",
                    artifact.DatabasePath);
                await InitializeDatabaseAsync(existingDatabase: false, cancellationToken);
                Volatile.Write(
                    ref initializationState,
                    new ResourceManagerDatabaseInitializationState(
                        ResourceManagerDatabaseInitializationDisposition.RecoveredAfterCorruption,
                        DatabasePath,
                        DateTimeOffset.UtcNow,
                        artifact.DatabasePath,
                        FormatRecoveryReason(exception)));
            }
            Volatile.Write(ref initialized, true);
        }
        finally
        {
            initializationGate.Release();
        }
    }

    public async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        var connection = CreateConnection();
        try
        {
            await connection.OpenAsync(cancellationToken);
            await ConfigureConnectionAsync(connection, initializeJournal: false, cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private async Task InitializeDatabaseAsync(
        bool existingDatabase,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        await using var connection = CreateInitializationConnection();
        await connection.OpenAsync(cancellationToken);
        if (existingDatabase)
        {
            await VerifyIntegrityAsync(connection, cancellationToken);
        }

        await ConfigureConnectionAsync(connection, initializeJournal: true, cancellationToken);
        await SqliteSchemaMigrator.MigrateAsync(connection, cancellationToken);
        try
        {
            await SqliteDatabaseMaintenance.RunPendingAsync(connection, DatabasePath, cancellationToken);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            logger?.LogWarning(ex, "Deferred SQLite maintenance could not complete; it will be retried on a later start.");
        }
        await VerifyIntegrityAsync(connection, cancellationToken);
    }

    private SqliteConnection CreateConnection() => new(connectionString);

    private SqliteConnection CreateInitializationConnection() => new(initializationConnectionString);

    private static async Task VerifyIntegrityAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA quick_check(1);";
        var result = Convert.ToString(
            await command.ExecuteScalarAsync(cancellationToken),
            System.Globalization.CultureInfo.InvariantCulture);
        if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
        {
            throw new SqliteDatabaseIntegrityException(
                $"SQLite quick_check failed: {result ?? "<no result>"}.");
        }
    }

    private static string FormatRecoveryReason(Exception exception)
    {
        var text = $"{exception.GetType().Name}: {exception.Message}";
        return text.Length <= 2048 ? text : text[..2048];
    }

    private static async Task ConfigureConnectionAsync(
        SqliteConnection connection,
        bool initializeJournal,
        CancellationToken cancellationToken)
    {
        if (initializeJournal)
        {
            await using var journalCommand = connection.CreateCommand();
            journalCommand.CommandText = "PRAGMA journal_mode=WAL;";
            var mode = Convert.ToString(await journalCommand.ExecuteScalarAsync(cancellationToken));
            if (!string.Equals(mode, "wal", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"SQLite WAL mode could not be enabled. Returned mode: {mode ?? "<null>"}.");
            }
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA foreign_keys=ON;
            PRAGMA synchronous=NORMAL;
            PRAGMA busy_timeout=5000;
            PRAGMA temp_store=MEMORY;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
