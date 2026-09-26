using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.Persistence;

internal sealed class SqliteDatabaseIntegrityException(string message)
    : Exception(message);

internal sealed record SqliteDatabaseQuarantineArtifact(
    string DatabasePath,
    string? WriteAheadLogPath,
    string? SharedMemoryPath);

internal static class SqliteDatabaseRecovery
{
    private const int SqliteCorrupt = 11;
    private const int SqliteNotADatabase = 26;

    public static bool IsConfirmedCorruption(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        Exception? current = exception;
        while (current is not null)
        {
            if (current is SqliteDatabaseIntegrityException)
            {
                return true;
            }

            if (current is SqliteException sqlite
                && sqlite.SqliteErrorCode is SqliteCorrupt or SqliteNotADatabase)
            {
                return true;
            }

            current = current.InnerException;
        }

        return false;
    }

    public static SqliteDatabaseQuarantineArtifact QuarantineFamily(
        string databasePath,
        DateTimeOffset detectedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var canonicalDatabasePath = Path.GetFullPath(databasePath);
        if (!File.Exists(canonicalDatabasePath))
        {
            throw new FileNotFoundException(
                "The corrupt SQLite database disappeared before quarantine.",
                canonicalDatabasePath);
        }

        var directory = Path.GetDirectoryName(canonicalDatabasePath)
            ?? throw new InvalidOperationException("The SQLite database has no parent directory.");
        var digest = ComputeSha256(canonicalDatabasePath);
        var quarantineDatabasePath = Path.Combine(
            directory,
            $"{Path.GetFileNameWithoutExtension(canonicalDatabasePath)}.corrupt." +
            $"{detectedAt.UtcDateTime:yyyyMMddTHHmmssfffffffZ}.{digest[..16]}." +
            $"{Guid.NewGuid():N}{Path.GetExtension(canonicalDatabasePath)}");

        var moves = new List<(string Source, string Destination)>(3)
        {
            (canonicalDatabasePath, quarantineDatabasePath)
        };
        AddSidecarIfPresent(moves, canonicalDatabasePath, quarantineDatabasePath, "-wal");
        AddSidecarIfPresent(moves, canonicalDatabasePath, quarantineDatabasePath, "-shm");

        var completed = 0;
        try
        {
            while (completed < moves.Count)
            {
                var move = moves[completed];
                WindowsNativeAtomicFileCommitter.CommitNew(move.Source, move.Destination);
                completed++;
            }
        }
        catch (Exception quarantineFailure)
        {
            var rollbackFailures = new List<Exception>();
            var rollbackIndex = completed - 1;
            while (rollbackIndex >= 0)
            {
                var move = moves[rollbackIndex];
                try
                {
                    WindowsNativeAtomicFileCommitter.CommitNew(move.Destination, move.Source);
                }
                catch (Exception rollbackFailure)
                {
                    rollbackFailures.Add(rollbackFailure);
                }
                rollbackIndex--;
            }

            if (rollbackFailures.Count > 0)
            {
                rollbackFailures.Insert(0, quarantineFailure);
                throw new IOException(
                    "SQLite quarantine failed and its namespace rollback was incomplete.",
                    new AggregateException(rollbackFailures));
            }

            throw new IOException(
                "SQLite quarantine failed; completed moves were rolled back.",
                quarantineFailure);
        }

        return new SqliteDatabaseQuarantineArtifact(
            quarantineDatabasePath,
            moves.Count > 1 && moves[1].Source.EndsWith("-wal", StringComparison.Ordinal)
                ? moves[1].Destination
                : null,
            moves.FirstOrDefault(static move =>
                move.Source.EndsWith("-shm", StringComparison.Ordinal)).Destination);
    }

    private static void AddSidecarIfPresent(
        ICollection<(string Source, string Destination)> moves,
        string databasePath,
        string quarantineDatabasePath,
        string suffix)
    {
        var source = databasePath + suffix;
        if (File.Exists(source))
        {
            moves.Add((source, quarantineDatabasePath + suffix));
        }
    }

    private static string ComputeSha256(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 1024,
            FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
