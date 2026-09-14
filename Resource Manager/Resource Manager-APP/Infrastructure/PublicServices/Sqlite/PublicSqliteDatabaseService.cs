using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ResourceManager.App.Application.PublicServices.Sqlite;
using ResourceManager.App.Domain.PublicServices.Sqlite;
using ResourceManager.App.Infrastructure.Paths;

namespace ResourceManager.App.Infrastructure.PublicServices.Sqlite;

public sealed class PublicSqliteDatabaseService : IPublicSqliteDatabaseService
{
    private const int DefaultMaxRows = 1_000;
    private const int MaximumRows = 10_000;
    private const int MaximumBatchCommands = 128;
    private const int MaximumSqlLength = 1_048_576;
    private readonly string databaseRoot;

    public PublicSqliteDatabaseService(IHostEnvironment environment)
    {
        databaseRoot = Path.Combine(
            PackagePathResolver.ResolvePackageRoot(environment.ContentRootPath),
            "UserData",
            "Database",
            "Public");
    }

    public Task<IReadOnlyList<PublicSqliteDatabaseInfo>> ListAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(databaseRoot))
        {
            return Task.FromResult<IReadOnlyList<PublicSqliteDatabaseInfo>>([]);
        }

        var databases = Directory.EnumerateFiles(databaseRoot, "*.db", SearchOption.TopDirectoryOnly)
            .Select(CreateInfo)
            .OrderBy(static item => item.DatabaseId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return Task.FromResult<IReadOnlyList<PublicSqliteDatabaseInfo>>(databases);
    }

    public Task<PublicSqliteDatabaseInfo?> GetAsync(
        string databaseId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = ResolveExistingPath(databaseId);
        return Task.FromResult(path is null ? null : CreateInfo(path));
    }

    public async Task<PublicSqliteDatabaseInfo> CreateAsync(
        string databaseId,
        CancellationToken cancellationToken)
    {
        var path = ResolvePath(databaseId);
        Directory.CreateDirectory(databaseRoot);
        await using var connection = await OpenAsync(path, create: true, cancellationToken);
        return CreateInfo(path);
    }

    public async Task<PublicSqliteCommandResult> QueryAsync(
        string databaseId,
        PublicSqliteCommandRequest request,
        CancellationToken cancellationToken)
    {
        var path = RequireExistingPath(databaseId);
        ValidateSql(request.Sql);
        await using var connection = await OpenAsync(path, create: false, cancellationToken);
        return await QueryCoreAsync(connection, transaction: null, request.Sql, request.Parameters, request.MaxRows, cancellationToken);
    }

    public async Task<PublicSqliteCommandResult> ExecuteAsync(
        string databaseId,
        PublicSqliteCommandRequest request,
        CancellationToken cancellationToken)
    {
        var path = RequireExistingPath(databaseId);
        ValidateSql(request.Sql);
        await using var connection = await OpenAsync(path, create: false, cancellationToken);
        return await ExecuteCoreAsync(connection, transaction: null, request.Sql, request.Parameters, cancellationToken);
    }

    public async Task<PublicSqliteBatchResult> BatchAsync(
        string databaseId,
        PublicSqliteBatchRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Commands.Count is < 1 or > MaximumBatchCommands)
        {
            throw new ArgumentException($"Batch command count must be between 1 and {MaximumBatchCommands}.");
        }

        var path = RequireExistingPath(databaseId);
        await using var connection = await OpenAsync(path, create: false, cancellationToken);
        var deferred = !string.Equals(request.TransactionMode, "immediate", StringComparison.OrdinalIgnoreCase);
        await using var transaction = connection.BeginTransaction(deferred);
        var results = new List<PublicSqliteCommandResult>(request.Commands.Count);
        try
        {
            foreach (var item in request.Commands)
            {
                ValidateSql(item.Sql);
                if (string.Equals(item.Kind, "query", StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(await QueryCoreAsync(
                        connection,
                        transaction,
                        item.Sql,
                        item.Parameters,
                        item.MaxRows,
                        cancellationToken));
                    continue;
                }

                if (!string.Equals(item.Kind, "execute", StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException("Batch command kind must be 'query' or 'execute'.");
                }

                results.Add(await ExecuteCoreAsync(
                    connection,
                    transaction,
                    item.Sql,
                    item.Parameters,
                    cancellationToken));
            }

            await transaction.CommitAsync(cancellationToken);
            return new PublicSqliteBatchResult(results);
        }
        catch
        {
            try
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
            catch (InvalidOperationException)
            {
            }
            catch (SqliteException)
            {
            }
            throw;
        }
    }

    public async Task<PublicSqliteCheckpointResult> CheckpointAsync(
        string databaseId,
        CancellationToken cancellationToken)
    {
        var path = RequireExistingPath(databaseId);
        await using var connection = await OpenAsync(path, create: false, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new PublicSqliteCheckpointResult(0, 0, 0);
        }

        return new PublicSqliteCheckpointResult(
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.GetInt32(2));
    }

    private static async Task<PublicSqliteCommandResult> QueryCoreAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        IReadOnlyDictionary<string, JsonElement>? parameters,
        int? requestedMaxRows,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, sql, parameters);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var columns = Enumerable.Range(0, reader.FieldCount)
            .Select(reader.GetName)
            .ToArray();
        var rows = new List<IReadOnlyList<PublicSqliteValue>>();
        var maxRows = Math.Clamp(requestedMaxRows ?? DefaultMaxRows, 1, MaximumRows);
        var truncated = false;
        while (await reader.ReadAsync(cancellationToken))
        {
            if (rows.Count >= maxRows)
            {
                truncated = true;
                break;
            }

            var values = new PublicSqliteValue[reader.FieldCount];
            for (var index = 0; index < values.Length; index++)
            {
                values[index] = ReadValue(reader.GetValue(index));
            }

            rows.Add(values);
        }

        return new PublicSqliteCommandResult(
            "query",
            columns,
            rows,
            RowsAffected: reader.RecordsAffected,
            SQLitePCL.raw.sqlite3_last_insert_rowid(connection.Handle),
            truncated);
    }

    private static async Task<PublicSqliteCommandResult> ExecuteCoreAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        IReadOnlyDictionary<string, JsonElement>? parameters,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, sql, parameters);
        var rowsAffected = await command.ExecuteNonQueryAsync(cancellationToken);
        return new PublicSqliteCommandResult(
            "execute",
            [],
            [],
            rowsAffected,
            SQLitePCL.raw.sqlite3_last_insert_rowid(connection.Handle),
            Truncated: false);
    }

    private static SqliteCommand CreateCommand(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        IReadOnlyDictionary<string, JsonElement>? parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 30;
        command.Transaction = transaction;
        foreach (var pair in parameters ?? new Dictionary<string, JsonElement>())
        {
            var name = NormalizeParameterName(pair.Key);
            command.Parameters.AddWithValue(name, ConvertParameter(pair.Value));
        }

        return command;
    }

    private static object ConvertParameter(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.Null => DBNull.Value,
            JsonValueKind.True => 1L,
            JsonValueKind.False => 0L,
            JsonValueKind.Number when value.TryGetInt64(out var integer) => integer,
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Object => DecodeBlob(value),
            _ => throw new ArgumentException("SQLite parameters support null, boolean, number, string, or a blob object.")
        };
    }

    private static byte[] DecodeBlob(JsonElement value)
    {
        if (!value.TryGetProperty("type", out var type)
            || !string.Equals(type.GetString(), "blob", StringComparison.OrdinalIgnoreCase)
            || !value.TryGetProperty("base64", out var encoded)
            || encoded.ValueKind != JsonValueKind.String)
        {
            throw new ArgumentException("A blob parameter must be { type: 'blob', base64: '...' }.");
        }

        try
        {
            return Convert.FromBase64String(encoded.GetString() ?? string.Empty);
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("The blob parameter is not valid base64.", exception);
        }
    }

    private static PublicSqliteValue ReadValue(object value)
    {
        return value switch
        {
            DBNull => new PublicSqliteValue("null", null),
            byte[] bytes => new PublicSqliteValue("blob", Convert.ToBase64String(bytes)),
            sbyte or byte or short or ushort or int or uint or long or ulong =>
                new PublicSqliteValue("integer", Convert.ToInt64(value, CultureInfo.InvariantCulture)),
            float or double or decimal =>
                new PublicSqliteValue("real", Convert.ToDouble(value, CultureInfo.InvariantCulture)),
            string text => new PublicSqliteValue("text", text),
            _ => new PublicSqliteValue("text", Convert.ToString(value, CultureInfo.InvariantCulture))
        };
    }

    private async Task<SqliteConnection> OpenAsync(
        string path,
        bool create,
        CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = create ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
            ForeignKeys = true,
            DefaultTimeout = 5
        }.ToString());
        try
        {
            await connection.OpenAsync(cancellationToken);
            connection.EnableExtensions(enable: false);
            PublicSqliteAuthorizer.Apply(connection);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous = NORMAL;
                PRAGMA foreign_keys = ON;
                PRAGMA busy_timeout = 5000;
                PRAGMA trusted_schema = OFF;
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private string? ResolveExistingPath(string databaseId)
    {
        var path = ResolvePath(databaseId);
        return File.Exists(path) ? path : null;
    }

    private string RequireExistingPath(string databaseId)
    {
        return ResolveExistingPath(databaseId)
            ?? throw new FileNotFoundException("The managed SQLite database does not exist.", databaseId);
    }

    private string ResolvePath(string databaseId)
    {
        if (!IsValidDatabaseId(databaseId))
        {
            throw new ArgumentException("Database ID must be 1-64 ASCII letters, numbers, dots, underscores, or hyphens.");
        }

        return Path.Combine(databaseRoot, $"{databaseId}.db");
    }

    private static bool IsValidDatabaseId(string databaseId)
    {
        return databaseId.Length is >= 1 and <= 64
            && databaseId.All(static character =>
                character is >= 'a' and <= 'z'
                or >= 'A' and <= 'Z'
                or >= '0' and <= '9'
                or '.' or '_' or '-');
    }

    private static string NormalizeParameterName(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length is < 1 or > 128)
        {
            throw new ArgumentException("SQLite parameter names must be 1-128 characters.");
        }

        return trimmed[0] is '$' or '@' or ':' ? trimmed : $"${trimmed}";
    }

    private static void ValidateSql(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql) || sql.Length > MaximumSqlLength)
        {
            throw new ArgumentException($"SQL must contain 1-{MaximumSqlLength} characters.");
        }
    }

    private static PublicSqliteDatabaseInfo CreateInfo(string path)
    {
        var file = new FileInfo(path);
        return new PublicSqliteDatabaseInfo(
            Path.GetFileNameWithoutExtension(file.Name),
            file.Exists ? file.Length : 0,
            file.Exists ? file.LastWriteTimeUtc : DateTimeOffset.UtcNow);
    }
}
