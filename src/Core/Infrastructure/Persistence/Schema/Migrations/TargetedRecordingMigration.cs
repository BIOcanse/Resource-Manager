using Microsoft.Data.Sqlite;

namespace ResourceManager.App.Infrastructure.Persistence.Schema.Migrations;

internal sealed class TargetedRecordingMigration : ISqliteSchemaMigration
{
    public int Version => 12;
    public string Name => "targeted-recordings";

    public async Task ApplyAsync(SqliteConnection connection, SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE targeted_recordings (
                id TEXT NOT NULL PRIMARY KEY,
                software_id TEXT NOT NULL,
                software_name TEXT NOT NULL,
                started_utc_ticks INTEGER NOT NULL,
                ended_utc_ticks INTEGER,
                maximum_duration_seconds INTEGER NOT NULL,
                status TEXT NOT NULL CHECK(status IN ('recording', 'completed')),
                stop_reason TEXT,
                incomplete INTEGER NOT NULL DEFAULT 0 CHECK(incomplete IN (0, 1))
            ) STRICT;
            CREATE INDEX ix_targeted_recordings_started
                ON targeted_recordings(started_utc_ticks DESC);
            CREATE TABLE targeted_frame_interval_chunks (
                id INTEGER PRIMARY KEY,
                recording_id TEXT NOT NULL REFERENCES targeted_recordings(id) ON DELETE CASCADE,
                first_utc_ticks INTEGER NOT NULL,
                last_utc_ticks INTEGER NOT NULL,
                intervals_json TEXT NOT NULL
            ) STRICT;
            CREATE INDEX ix_targeted_frames_recording
                ON targeted_frame_interval_chunks(recording_id, first_utc_ticks);
            CREATE TABLE targeted_resource_samples (
                id INTEGER PRIMARY KEY,
                recording_id TEXT NOT NULL REFERENCES targeted_recordings(id) ON DELETE CASCADE,
                captured_utc_ticks INTEGER NOT NULL,
                software_json TEXT NOT NULL,
                system_json TEXT NOT NULL,
                processes_json TEXT NOT NULL
            ) STRICT;
            CREATE INDEX ix_targeted_resources_recording
                ON targeted_resource_samples(recording_id, captured_utc_ticks);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
