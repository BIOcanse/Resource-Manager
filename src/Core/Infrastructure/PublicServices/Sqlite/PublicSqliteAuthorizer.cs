using SQLitePCL;

namespace ResourceManager.App.Infrastructure.PublicServices.Sqlite;

internal static class PublicSqliteAuthorizer
{
    private static readonly strdelegate_authorizer Callback = Authorize;
    private static readonly HashSet<string> DeniedFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "load_extension",
        "readfile",
        "writefile"
    };
    private static readonly HashSet<string> DeniedPragmas = new(StringComparer.OrdinalIgnoreCase)
    {
        "data_store_directory",
        "temp_store_directory",
        "writable_schema"
    };

    public static void Apply(Microsoft.Data.Sqlite.SqliteConnection connection)
    {
        var result = raw.sqlite3_set_authorizer(connection.Handle, Callback, null);
        if (result != raw.SQLITE_OK)
        {
            throw new InvalidOperationException($"Unable to install the SQLite authorizer ({result}).");
        }

        raw.sqlite3_limit(connection.Handle, raw.SQLITE_LIMIT_ATTACHED, 0);
        raw.sqlite3_limit(connection.Handle, raw.SQLITE_LIMIT_SQL_LENGTH, 1_048_576);
        raw.sqlite3_limit(connection.Handle, raw.SQLITE_LIMIT_VARIABLE_NUMBER, 999);
        raw.sqlite3_limit(connection.Handle, raw.SQLITE_LIMIT_COLUMN, 512);
    }

    private static int Authorize(
        object? userData,
        int actionCode,
        string? parameter0,
        string? parameter1,
        string? databaseName,
        string? triggerOrView)
    {
        if (actionCode is raw.SQLITE_ATTACH or raw.SQLITE_DETACH)
        {
            return raw.SQLITE_DENY;
        }

        if (actionCode == raw.SQLITE_FUNCTION
            && (DeniedFunctions.Contains(parameter0 ?? string.Empty)
                || DeniedFunctions.Contains(parameter1 ?? string.Empty)))
        {
            return raw.SQLITE_DENY;
        }

        if (actionCode == raw.SQLITE_PRAGMA
            && DeniedPragmas.Contains(parameter0 ?? string.Empty))
        {
            return raw.SQLITE_DENY;
        }

        return raw.SQLITE_OK;
    }
}
