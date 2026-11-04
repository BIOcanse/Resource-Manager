using ResourceManager.ReadWrite.Windows.Frames;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ResourceManager.App.Infrastructure.Persistence;

namespace ResourceManager.App.Infrastructure.Monitoring.TargetedRecording;

public sealed class TargetedRecordingStore(ResourceManagerDatabase database)
{
    public async Task RecoverInterruptedAsync(CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE targeted_recordings
            SET status = 'completed', stop_reason = 'interrupted', incomplete = 1,
                ended_utc_ticks = $now
            WHERE status = 'recording';
            """;
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.UtcTicks);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task CreateAsync(TargetedRecordingHeader recording, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO targeted_recordings
                (id, software_id, software_name, started_utc_ticks, maximum_duration_seconds, status,
                 profile_id, profile_name, items_json, processes_json, maximum_report_kib)
            VALUES ($id, $softwareId, $softwareName, $started, $maximum, 'recording',
                    $profileId, $profileName, $items, $processes, $maximumKib);
            """;
        command.Parameters.AddWithValue("$profileId", (object?)recording.ProfileId ?? DBNull.Value);
        command.Parameters.AddWithValue("$profileName", (object?)recording.ProfileName ?? DBNull.Value);
        command.Parameters.AddWithValue("$items", JsonSerializer.Serialize(recording.Items, ColumnJson));
        command.Parameters.AddWithValue("$processes", JsonSerializer.Serialize(recording.Processes, ColumnJson));
        command.Parameters.AddWithValue("$maximumKib", recording.MaximumReportKib);
        command.Parameters.AddWithValue("$id", recording.Id);
        command.Parameters.AddWithValue("$softwareId", recording.SoftwareId);
        command.Parameters.AddWithValue("$softwareName", recording.SoftwareName);
        command.Parameters.AddWithValue("$started", recording.StartedAt.UtcTicks);
        command.Parameters.AddWithValue("$maximum", recording.MaximumDurationSeconds);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Appends frames and one sample row; returns the bytes of the stored payloads.</summary>
    public async Task<long> AppendAsync(string recordingId, IReadOnlyList<FrameIntervalSample> intervals,
        TargetedResourceSample? resource, CancellationToken cancellationToken)
    {
        if (intervals.Count == 0 && resource is null)
        {
            return 0;
        }
        long bytes = 0;

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
            var encoded = TargetedFrameChunkCodec.Encode(intervals);
            bytes += encoded.Length + 24;
            frames.Parameters.AddWithValue("$json", encoded);
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
            var softwareJson = JsonSerializer.Serialize(resource.Software);
            var systemJson = JsonSerializer.Serialize(resource.System);
            var processesJson = JsonSerializer.Serialize(resource.Processes);
            bytes += softwareJson.Length + systemJson.Length + processesJson.Length + 24;
            sample.Parameters.AddWithValue("$software", softwareJson);
            sample.Parameters.AddWithValue("$system", systemJson);
            sample.Parameters.AddWithValue("$processes", processesJson);
            await sample.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return bytes;
    }

    /// <summary>Removes the frames and samples of a recording that found no target process.</summary>
    public async Task DiscardSamplesAsync(string recordingId, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM targeted_frame_interval_chunks WHERE recording_id = $id;
            DELETE FROM targeted_resource_samples WHERE recording_id = $id;
            """;
        command.Parameters.AddWithValue("$id", recordingId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Finishes a recording with its final item reports, merged process list and stored bytes.</summary>
    public async Task FinishAsync(string recordingId, string reason, bool incomplete,
        IReadOnlyList<TargetedRecordingItemReport> items, IReadOnlyList<TargetedRecordingProcess> processes,
        long storedBytes, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var finish = connection.CreateCommand();
        finish.CommandText = """
            UPDATE targeted_recordings
            SET status = 'completed', stop_reason = $reason, incomplete = $incomplete,
                ended_utc_ticks = $ended, items_json = $items, processes_json = $processes,
                stored_bytes = $bytes
            WHERE id = $id AND status = 'recording';
            """;
        finish.Parameters.AddWithValue("$id", recordingId);
        finish.Parameters.AddWithValue("$reason", reason);
        finish.Parameters.AddWithValue("$incomplete", incomplete ? 1 : 0);
        finish.Parameters.AddWithValue("$ended", DateTimeOffset.UtcNow.UtcTicks);
        finish.Parameters.AddWithValue("$items", JsonSerializer.Serialize(items, ColumnJson));
        finish.Parameters.AddWithValue("$processes", JsonSerializer.Serialize(processes, ColumnJson));
        finish.Parameters.AddWithValue("$bytes", storedBytes);
        await finish.ExecuteNonQueryAsync(cancellationToken);
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

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<TargetedRecordingHeader>> ListAsync(CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, software_id, software_name, started_utc_ticks, ended_utc_ticks,
                   maximum_duration_seconds, status, stop_reason, incomplete,
                   profile_id, profile_name, items_json, processes_json, stored_bytes, maximum_report_kib
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
                   maximum_duration_seconds, status, stop_reason, incomplete,
                   profile_id, profile_name, items_json, processes_json, stored_bytes, maximum_report_kib
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
                intervals.AddRange(TargetedFrameChunkCodec.Decode(reader.GetString(0)));
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

    public async Task<int> ClearHistoryAsync(CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        // The same cascading foreign keys as single deletion remove intervals and resource samples.
        command.CommandText = "DELETE FROM targeted_recordings WHERE status = 'completed';";
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Header and item columns are stored in the wire's camelCase shape.</summary>
    private static readonly JsonSerializerOptions ColumnJson = new(JsonSerializerDefaults.Web);

    private static TargetedRecordingHeader ReadHeader(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2),
        new DateTimeOffset(reader.GetInt64(3), TimeSpan.Zero),
        reader.IsDBNull(4) ? null : new DateTimeOffset(reader.GetInt64(4), TimeSpan.Zero),
        reader.GetInt32(5), reader.GetString(6),
        reader.IsDBNull(7) ? null : reader.GetString(7),
        reader.GetInt32(8) != 0)
    {
        ProfileId = reader.IsDBNull(9) ? null : reader.GetString(9),
        ProfileName = reader.IsDBNull(10) ? null : reader.GetString(10),
        Items = JsonSerializer.Deserialize<TargetedRecordingItemReport[]>(reader.GetString(11), ColumnJson) ?? [],
        Processes = JsonSerializer.Deserialize<TargetedRecordingProcess[]>(reader.GetString(12), ColumnJson) ?? [],
        StoredBytes = reader.GetInt64(13),
        MaximumReportKib = reader.GetInt32(14)
    };
}
