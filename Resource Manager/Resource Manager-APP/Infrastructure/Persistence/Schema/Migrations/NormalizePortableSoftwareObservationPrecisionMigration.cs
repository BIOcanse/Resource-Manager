using Microsoft.Data.Sqlite;

namespace ResourceManager.App.Infrastructure.Persistence.Schema.Migrations;

internal sealed class NormalizePortableSoftwareObservationPrecisionMigration : ISqliteSchemaMigration
{
    public int Version => 11;

    public string Name => "normalize-portable-software-observation-precision";

    public async Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using (var validate = connection.CreateCommand())
        {
            validate.Transaction = transaction;
            validate.CommandText = """
                SELECT COUNT(*)
                FROM portable_software_executables
                WHERE first_observed_utc_ticks < $minimumTicks
                   OR first_observed_utc_ticks > $maximumTicks;
                """;
            validate.Parameters.AddWithValue(
                "$minimumTicks",
                DateTimeOffset.UnixEpoch.UtcTicks);
            validate.Parameters.AddWithValue(
                "$maximumTicks",
                DateTimeOffset.MaxValue.UtcTicks);
            var invalidCount = Convert.ToInt64(
                await validate.ExecuteScalarAsync(cancellationToken),
                System.Globalization.CultureInfo.InvariantCulture);
            if (invalidCount != 0)
            {
                throw new InvalidDataException(
                    "Persisted portable software observation time is outside the published UTC ABI.");
            }
        }

        await using var normalize = connection.CreateCommand();
        normalize.Transaction = transaction;
        normalize.CommandText = """
            UPDATE portable_software_executables
            SET first_observed_utc_ticks =
                first_observed_utc_ticks
                - (first_observed_utc_ticks % $ticksPerMillisecond)
            WHERE first_observed_utc_ticks % $ticksPerMillisecond <> 0;
            """;
        normalize.Parameters.AddWithValue(
            "$ticksPerMillisecond",
            TimeSpan.TicksPerMillisecond);
        await normalize.ExecuteNonQueryAsync(cancellationToken);
    }
}
