using System.Text.Json;

namespace ResourceManager.App.Domain.PublicServices.Sqlite;

public sealed record PublicSqliteDatabaseInfo(
    string DatabaseId,
    long SizeBytes,
    DateTimeOffset LastModifiedAt);

public sealed record PublicSqliteCommandRequest(
    string Sql,
    IReadOnlyDictionary<string, JsonElement>? Parameters,
    int? MaxRows);

public sealed record PublicSqliteBatchCommand(
    string Kind,
    string Sql,
    IReadOnlyDictionary<string, JsonElement>? Parameters,
    int? MaxRows);

public sealed record PublicSqliteBatchRequest(
    string? TransactionMode,
    IReadOnlyList<PublicSqliteBatchCommand> Commands);

public sealed record PublicSqliteValue(
    string Type,
    object? Value);

public sealed record PublicSqliteCommandResult(
    string Kind,
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<PublicSqliteValue>> Rows,
    int RowsAffected,
    long LastInsertRowId,
    bool Truncated);

public sealed record PublicSqliteBatchResult(
    IReadOnlyList<PublicSqliteCommandResult> Results);

public sealed record PublicSqliteCheckpointResult(
    int BusyFrames,
    int LogFrames,
    int CheckpointedFrames);
