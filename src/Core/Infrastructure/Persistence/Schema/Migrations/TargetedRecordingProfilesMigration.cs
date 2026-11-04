using Microsoft.Data.Sqlite;

namespace ResourceManager.App.Infrastructure.Persistence.Schema.Migrations;

/// <summary>Targeted recording profiles: the profile and item snapshot taken at start, the merged target
/// process list, the stored byte count and the size cap in effect.</summary>
internal sealed class TargetedRecordingProfilesMigration : ISqliteSchemaMigration
{
    public int Version => 16;
    public string Name => "targeted_recording_profiles";

    public async Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            ALTER TABLE targeted_recordings ADD COLUMN profile_id TEXT;
            ALTER TABLE targeted_recordings ADD COLUMN profile_name TEXT;
            ALTER TABLE targeted_recordings ADD COLUMN items_json TEXT NOT NULL DEFAULT '[]';
            ALTER TABLE targeted_recordings ADD COLUMN processes_json TEXT NOT NULL DEFAULT '[]';
            ALTER TABLE targeted_recordings ADD COLUMN stored_bytes INTEGER NOT NULL DEFAULT 0;
            ALTER TABLE targeted_recordings ADD COLUMN maximum_report_kib INTEGER NOT NULL DEFAULT 0;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
