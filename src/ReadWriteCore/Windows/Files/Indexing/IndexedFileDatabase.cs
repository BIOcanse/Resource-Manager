using Microsoft.Data.Sqlite;
using ResourceManager.App.Infrastructure.Persistence.Schema.Migrations;
namespace ResourceManager.ReadWrite.Windows.Files.Indexing;

/// <summary>Explicit index database binding. Borrowed product initialization stays external;
/// standalone initializes only the same original index schema and connection pragmas.</summary>
public sealed class IndexedFileDatabase
{
    private readonly Func<CancellationToken, Task<SqliteConnection>>? borrowedOpen;
    private readonly string? connectionString;
    private readonly SemaphoreSlim initializeGate = new(1, 1);
    private bool initialized;
    public string Identity { get; }
    public string DatabasePath { get; }
    public IndexedFileDatabase(string databasePath)
    {
        DatabasePath = Path.GetFullPath(databasePath);
        Identity = DatabasePath;
        connectionString = new SqliteConnectionStringBuilder { DataSource = DatabasePath, Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared, Pooling = false, DefaultTimeout = 5 }.ToString();
    }
    private IndexedFileDatabase(string identity, Func<CancellationToken, Task<SqliteConnection>> open)
    { Identity = identity; DatabasePath = identity; borrowedOpen = open; }
    public static IndexedFileDatabase Borrow(string identity, Func<CancellationToken, Task<SqliteConnection>> open)
    { ArgumentException.ThrowIfNullOrWhiteSpace(identity); ArgumentNullException.ThrowIfNull(open); return new(identity, open); }
    public async Task<SqliteConnection> OpenConnectionAsync(CancellationToken token)
    {
        if (borrowedOpen is not null) return await borrowedOpen(token);
        await initializeGate.WaitAsync(token);
        try
        {
            if (!initialized)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
                await using var initial = new SqliteConnection(connectionString); await initial.OpenAsync(token);
                await ConfigureConnectionAsync(initial, true, token);
                await using var command = initial.CreateCommand(); command.CommandText = SoftwareFileIndexSchemaSql.Current;
                await command.ExecuteNonQueryAsync(token); initialized = true;
            }
        }
        finally { initializeGate.Release(); }
        var connection = new SqliteConnection(connectionString);
        try { await connection.OpenAsync(token); await ConfigureConnectionAsync(connection, false, token); return connection; }
        catch { await connection.DisposeAsync(); throw; }
    }
    public static async Task ConfigureConnectionAsync(
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
