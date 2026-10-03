using System.Text.Json;
using Microsoft.Data.Sqlite;
using ResourceManager.App.Domain.FrameTiming;
using ResourceManager.App.Infrastructure.Persistence;

namespace ResourceManager.App.Infrastructure.Monitoring.TargetedRecording;

public sealed class TargetedRecordingStore(ResourceManagerDatabase database)
{
    public const int CompletedRetention = 50;

    public async Task RecoverInterruptedAsync(CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE targeted_recordings
            SET status = 'completed', stop_reason = 'interrupted', incomplete = 1,
                ended_utc_ticks = $now
            WHERE status = 'recording';
            DELETE FROM targeted_recordings
            WHERE id IN (
                SELECT id FROM targeted_recordings WHERE status = 'completed'
                ORDER BY started_utc_ticks DESC LIMIT -1 OFFSET $retain
            );
            """;
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.UtcTicks);
        command.Parameters.AddWithValue("$retain", CompletedRetention);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task CreateAsync(TargetedRecordingHeader recording, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO targeted_recordings
                (id, software_id, software_name, started_utc_ticks, maximum_duration_seconds, status)
            VALUES ($id, $softwareId, $softwareName, $started, $maximum, 'recording');
            """;
        command.Parameters.AddWithValue("$id", recording.Id);
        command.Parameters.AddWithValue("$softwareId", recording.SoftwareId);
        command.Parameters.AddWithValue("$softwareName", recording.SoftwareName);
        command.Parameters.AddWithValue("$started", recording.StartedAt.UtcTicks);
        command.Parameters.AddWithValue("$maximum", recording.MaximumDurationSeconds);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task AppendAsync(string recordingId, IReadOnlyList<FrameIntervalSample> intervals,
        TargetedResourceSample? resource, CancellationToken cancellationToken)
    {
        if (intervals.Count == 0 && resource is null)
        {
            return;
        }

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        if (intervals.Count > 0)
        {
            await using var frames = connection.CreateCommand();
            frames.Transaction = transaction;
            frames.CommandText = """
                INSERT INTO targeted_frame_interval_chunks
                    (recording_id, first_utc_ticks, last_utc_ticks, intervals_json)
                VALUES ($id, $first, $last, $json);
                """;
            frames.Parameters.AddWithValue("$id", recordingId);
            frames.Parameters.AddWithValue("$first", intervals[0].EndedAt.UtcTicks);
            frames.Parameters.AddWithValue("$last", intervals[^1].EndedAt.UtcTicks);
            frames.Parameters.AddWithValue("$json", JsonSerializer.Serialize(intervals));
            await frames.ExecuteNonQueryAsync(cancellationToken);
        }

        if (resource is not null)
        {
            await using var sample = connection.CreateCommand();
            sample.Transaction = transaction;
            sample.CommandText = """
                INSERT INTO targeted_resource_samples
                    (recording_id, captured_utc_ticks, software_json, system_json, processes_json)
                VALUES ($id, $captured, $software, $system, $processes);
                """;
            sample.Parameters.AddWithValue("$id", recordingId);
            sample.Parameters.AddWithValue("$captured", resource.CapturedAt.UtcTicks);
            sample.Parameters.AddWithValue("$software", JsonSerializer.Serialize(resource.Software));
            sample.Parameters.AddWithValue("$system", JsonSerializer.Serialize(resource.System));
            sample.Parameters.AddWithValue("$processes", JsonSerializer.Serialize(resource.Processes));
            await sample.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task FinishAsync(string recordingId, string reason, bool incomplete,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using (var finish = connection.CreateCommand())
        {
            finish.Transaction = transaction;
            finish.CommandText = """
                UPDATE targeted_recordings
                SET status = 'completed', stop_reason = $reason, incomplete = $incomplete,
                    ended_utc_ticks = $ended
                WHERE id = $id AND status = 'recording';
                """;
            finish.Parameters.AddWithValue("$id", recordingId);
            finish.Parameters.AddWithValue("$reason", reason);
            finish.Parameters.AddWithValue("$incomplete", incomplete ? 1 : 0);
            finish.Parameters.AddWithValue("$ended", DateTimeOffset.UtcNow.UtcTicks);
            await finish.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var prune = connection.CreateCommand())
        {
            prune.Transaction = transaction;
            prune.CommandText = """
                DELETE FROM targeted_recordings
                WHERE id IN (
                    SELECT id FROM targeted_recordings WHERE status = 'completed'
                    ORDER BY started_utc_ticks DESC LIMIT -1 OFFSET $retain
                );
                """;
            prune.Parameters.AddWithValue("$retain", CompletedRetention);
            await prune.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<TargetedRecordingHeader>> ListAsync(CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, software_id, software_name, started_utc_ticks, ended_utc_ticks,
                   maximum_duration_seconds, status, stop_reason, incomplete
            FROM targeted_recordings ORDER BY started_utc_ticks DESC;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var results = new List<TargetedRecordingHeader>();
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(ReadHeader(reader));
        }
        return results;
    }

    public async Task<TargetedRecordingReport?> GetAsync(string id, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var headerCommand = connection.CreateCommand();
        headerCommand.CommandText = """
            SELECT id, software_id, software_name, started_utc_ticks, ended_utc_ticks,
                   maximum_duration_seconds, status, stop_reason, incomplete
            FROM targeted_recordings WHERE id = $id;
            """;
        headerCommand.Parameters.AddWithValue("$id", id);
        TargetedRecordingHeader header;
        await using (var reader = await headerCommand.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }
            header = ReadHeader(reader);
        }

        var intervals = new List<FrameIntervalSample>();
        await using (var frames = connection.CreateCommand())
        {
            frames.CommandText = """
                SELECT intervals_json FROM targeted_frame_interval_chunks
                WHERE recording_id = $id ORDER BY first_utc_ticks, id;
                """;
            frames.Parameters.AddWithValue("$id", id);
            await using var reader = await frames.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                intervals.AddRange(JsonSerializer.Deserialize<FrameIntervalSample[]>(reader.GetString(0)) ?? []);
            }
        }

        var resources = new List<TargetedResourceSample>();
        await using (var samples = connection.CreateCommand())
        {
            samples.CommandText = """
                SELECT captured_utc_ticks, software_json, system_json, processes_json
                FROM targeted_resource_samples WHERE recording_id = $id
                ORDER BY captured_utc_ticks, id;
                """;
            samples.Parameters.AddWithValue("$id", id);
            await using var reader = await samples.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                resources.Add(new TargetedResourceSample(
                    new DateTimeOffset(reader.GetInt64(0), TimeSpan.Zero),
                    JsonSerializer.Deserialize<Dictionary<string, double?>>(reader.GetString(1)) ?? [],
                    JsonSerializer.Deserialize<Dictionary<string, double?>>(reader.GetString(2)) ?? [],
                    JsonSerializer.Deserialize<TargetedProcessIdentity[]>(reader.GetString(3)) ?? []));
            }
        }

        var statistics = FrameIntervalStatistics.Compute(intervals.Select(static item => item.DurationMs).ToArray());
        var distribution = intervals
            .GroupBy(static item => (int)Math.Floor(1000d / item.DurationMs / 10d) * 10)
            .OrderBy(static group => group.Key)
            .Select(static group => new FpsDistributionBucket(group.Key, group.Key + 10, group.Count()))
            .ToArray();
        return new TargetedRecordingReport(header, statistics, intervals, distribution, resources);
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM targeted_recordings WHERE id = $id AND status = 'completed';";
        command.Parameters.AddWithValue("$id", id);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    private static TargetedRecordingHeader ReadHeader(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2),
        new DateTimeOffset(reader.GetInt64(3), TimeSpan.Zero),
        reader.IsDBNull(4) ? null : new DateTimeOffset(reader.GetInt64(4), TimeSpan.Zero),
        reader.GetInt32(5), reader.GetString(6),
        reader.IsDBNull(7) ? null : reader.GetString(7),
        reader.GetInt32(8) != 0);
}
