using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Infrastructure.Persistence;

namespace ResourceManager.App.Infrastructure.Optimization;

public sealed class SqliteHostManagerSmartRecordHistoryStore(ResourceManagerDatabase database)
    : IHostManagerSmartRecordHistoryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new SmartRecordHistoryJsonContext(
        new JsonSerializerOptions(JsonSerializerDefaults.Web)).Options;

    // SQLite's async API performs synchronous I/O. The ledger owner dispatches these
    // operations away from its scheduling loop; no connection is retained here.
    public async Task<HostManagerRollbackStateDocument> PrepareAsync(
        HostManagerRollbackStateDocument previous,
        HostManagerRollbackStateDocument next,
        bool retain,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var prior = Rows(previous).ToDictionary(static row => HostManagerRecordHistoryKey.Create(row.Placement, row.Record));
        var placements = new HostManagerAppliedPlacementReceipt[next.AppliedPlacements.Count];
        var recordBuffers = new HostManagerAppliedRecord[placements.Length][];
        var reservations = new List<(int Placement, int Record)>();
        for (var index = 0; index < placements.Length; index++)
        {
            var placement = next.AppliedPlacements[index];
            var records = new HostManagerAppliedRecord[placement.Records.Count];
            recordBuffers[index] = records;
            placements[index] = placement with { Records = records };
            for (var recordIndex = 0; recordIndex < records.Length; recordIndex++)
            {
                var record = placement.Records[recordIndex];
                if (prior.TryGetValue(HostManagerRecordHistoryKey.Create(placement, record), out var existing)
                    && existing.Record.Sequence is > 0)
                {
                    records[recordIndex] = record with
                    {
                        Sequence = existing.Record.Sequence,
                        RecordedAt = existing.Record.RecordedAt ?? now.ToUniversalTime()
                    };
                    continue;
                }

                records[recordIndex] = record with { Sequence = null, RecordedAt = null };
                if (retain)
                    reservations.Add((index, recordIndex));
            }
        }
        if (reservations.Count == 0)
            return next with { AppliedPlacements = placements };

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        foreach (var (placementIndex, recordIndex) in reservations)
        {
            var recordedAt = now.ToUniversalTime();
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO host_manager_smart_record_history
                    (state, payload_json, recorded_utc_ticks, ledger_committed)
                VALUES ('active', $payload, $recorded, 0)
                RETURNING sequence;
                """;
            command.Parameters.AddWithValue("$payload", SerializeRow(placements[placementIndex], recordBuffers[placementIndex][recordIndex]));
            command.Parameters.AddWithValue("$recorded", recordedAt.UtcTicks);
            var sequence = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
            recordBuffers[placementIndex][recordIndex] = recordBuffers[placementIndex][recordIndex] with
                { Sequence = sequence, RecordedAt = recordedAt };
        }
        await transaction.CommitAsync(cancellationToken);
        return next with { AppliedPlacements = placements };
    }

    public async Task StageRemovalsAsync(
        HostManagerRollbackStateDocument previous,
        HostManagerRollbackStateDocument next,
        DateTimeOffset now,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<long, string>? restoreResults = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var retained = Sequences(next);
        var removed = Rows(previous).Where(row => row.Record.Sequence is > 0
            && !retained.Contains(row.Record.Sequence.Value)).ToArray();
        if (removed.Length == 0)
            return;
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        foreach (var row in removed)
        {
            var result = RestoreResult(row.Record.Sequence!.Value, restoreResults);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE host_manager_smart_record_history
                SET payload_json = $payload, restored_utc_ticks = $restored, restore_result = $result
                WHERE sequence = $sequence AND state = 'active';
                """;
            command.Parameters.AddWithValue("$sequence", row.Record.Sequence!.Value);
            command.Parameters.AddWithValue("$payload", SerializeRow(row.Placement, row.Record));
            command.Parameters.AddWithValue("$restored", now.UtcTicks);
            command.Parameters.AddWithValue("$result", result);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task CompleteAsync(
        HostManagerRollbackStateDocument previous,
        HostManagerRollbackStateDocument next,
        DateTimeOffset now,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<long, string>? restoreResults = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var retained = Sequences(next);
        if (retained.Count == 0 && !Rows(previous).Any(static row => row.Record.Sequence is > 0))
            return;
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        foreach (var row in Rows(next))
        {
            if (row.Record.Sequence is > 0)
                await UpsertActiveAsync(connection, transaction, row, now, cancellationToken);
        }
        foreach (var row in Rows(previous))
        {
            if (row.Record.Sequence is not > 0 || retained.Contains(row.Record.Sequence.Value))
                continue;
            var result = RestoreResult(row.Record.Sequence.Value, restoreResults);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE host_manager_smart_record_history
                SET state = 'history', payload_json = $payload,
                    restored_utc_ticks = COALESCE(restored_utc_ticks, $restored),
                    restore_result = COALESCE(restore_result, $result), ledger_committed = 1
                WHERE sequence = $sequence AND state = 'active';
                """;
            command.Parameters.AddWithValue("$sequence", row.Record.Sequence.Value);
            command.Parameters.AddWithValue("$payload", SerializeRow(row.Placement, row.Record));
            command.Parameters.AddWithValue("$restored", now.UtcTicks);
            command.Parameters.AddWithValue("$result", result);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task ReconcileAsync(
        HostManagerRollbackStateDocument restoredLedger,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        // The temporary set is bounded by the active ledger. SQL performs the history
        // transition without materializing accumulated history or active orphan rows.
        await using (var create = connection.CreateCommand())
        {
            create.Transaction = transaction;
            create.CommandText = "CREATE TEMP TABLE smart_record_ledger_sequences(sequence INTEGER PRIMARY KEY);";
            await create.ExecuteNonQueryAsync(cancellationToken);
        }
        foreach (var row in Rows(restoredLedger))
        {
            if (row.Record.Sequence is not > 0)
                continue;
            await UpsertActiveAsync(connection, transaction, row, now, cancellationToken);
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO smart_record_ledger_sequences(sequence) VALUES ($sequence);";
            insert.Parameters.AddWithValue("$sequence", row.Record.Sequence.Value);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var reconcile = connection.CreateCommand())
        {
            reconcile.Transaction = transaction;
            reconcile.CommandText = """
                DELETE FROM host_manager_smart_record_history
                WHERE state = 'active' AND ledger_committed = 0
                    AND sequence NOT IN (SELECT sequence FROM smart_record_ledger_sequences);
                UPDATE host_manager_smart_record_history
                SET state = 'history', restored_utc_ticks = COALESCE(restored_utc_ticks, $restored),
                    restore_result = COALESCE(restore_result, 'unknown')
                WHERE state = 'active'
                    AND sequence NOT IN (SELECT sequence FROM smart_record_ledger_sequences);
                DROP TABLE smart_record_ledger_sequences;
                """;
            reconcile.Parameters.AddWithValue("$restored", now.UtcTicks);
            await reconcile.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<HostManagerSmartRecordHistoryPage> ReadHistoryAsync(
        long? beforeSequence,
        int limit,
        CancellationToken cancellationToken)
    {
        if (beforeSequence is <= 0)
            throw new ArgumentOutOfRangeException(nameof(beforeSequence));
        if (limit < 1 || limit > OptimizationSettings.ProductDefaults.SmartRecordHistory.MaximumPageRows)
            throw new ArgumentOutOfRangeException(nameof(limit));
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = beforeSequence.HasValue
            ? """
                SELECT sequence, payload_json, recorded_utc_ticks, restored_utc_ticks, restore_result
                FROM host_manager_smart_record_history
                WHERE state = 'history' AND sequence < $before ORDER BY sequence DESC LIMIT $limit;
                """
            : """
                SELECT sequence, payload_json, recorded_utc_ticks, restored_utc_ticks, restore_result
                FROM host_manager_smart_record_history
                WHERE state = 'history' ORDER BY sequence DESC LIMIT $limit;
                """;
        if (beforeSequence.HasValue)
            command.Parameters.AddWithValue("$before", beforeSequence.Value);
        command.Parameters.AddWithValue("$limit", limit + 1);
        var records = new List<HostManagerSmartRecordHistoryEntry>(limit + 1);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (records.Count <= limit && await reader.ReadAsync(cancellationToken))
        {
            var placement = JsonSerializer.Deserialize<HostManagerAppliedPlacementReceipt>(reader.GetString(1), JsonOptions)
                ?? throw new InvalidDataException("The smart record history payload cannot be null.");
            if (placement.Records.Count != 1 || reader.IsDBNull(3) || reader.IsDBNull(4))
                throw new InvalidDataException("A smart record history row must contain one restored record.");
            var record = placement.Records[0];
            var result = reader.GetString(4);
            if (!HostManagerSmartRecordRestoreResults.IsSupported(result))
                throw new InvalidDataException("The smart record history restore result is invalid.");
            records.Add(new HostManagerSmartRecordHistoryEntry(
                reader.GetInt64(0), "history", record.Kind, record.RecordId, placement.TargetId, placement.SoftwareId,
                placement.ResourceKind, placement.AppliedAt,
                placement.ProcessId, placement.ProcessStartKey, placement.ProcessName,
                new DateTimeOffset(reader.GetInt64(2), TimeSpan.Zero),
                new DateTimeOffset(reader.GetInt64(3), TimeSpan.Zero), result));
        }
        var more = records.Count > limit;
        if (more)
            records.RemoveAt(limit);
        return new(records, more ? records[^1].Sequence : null);
    }

    public async Task<long> ClearHistoryAsync(CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM host_manager_smart_record_history WHERE state = 'history'; SELECT changes();";
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task UpsertActiveAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RecordRow row,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO host_manager_smart_record_history
                (sequence, state, payload_json, recorded_utc_ticks, ledger_committed)
            VALUES ($sequence, 'active', $payload, $recorded, 1)
            ON CONFLICT(sequence) DO UPDATE SET state = 'active', payload_json = excluded.payload_json,
                recorded_utc_ticks = excluded.recorded_utc_ticks,
                restored_utc_ticks = NULL, restore_result = NULL, ledger_committed = 1;
            """;
        command.Parameters.AddWithValue("$sequence", row.Record.Sequence!.Value);
        command.Parameters.AddWithValue("$payload", SerializeRow(row.Placement, row.Record));
        command.Parameters.AddWithValue("$recorded", (row.Record.RecordedAt ?? now).UtcTicks);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string SerializeRow(HostManagerAppliedPlacementReceipt placement, HostManagerAppliedRecord record)
        => JsonSerializer.Serialize(placement with { Records = [record] }, JsonOptions);

    private static IEnumerable<RecordRow> Rows(HostManagerRollbackStateDocument ledger)
        => ledger.AppliedPlacements.SelectMany(placement => placement.Records.Select(record => new RecordRow(placement, record)));

    private static HashSet<long> Sequences(HostManagerRollbackStateDocument ledger)
        => Rows(ledger).Where(static row => row.Record.Sequence is > 0)
            .Select(static row => row.Record.Sequence!.Value).ToHashSet();

    private static string RestoreResult(long sequence, IReadOnlyDictionary<long, string>? results)
    {
        var result = results is not null && results.TryGetValue(sequence, out var value)
            ? value : HostManagerSmartRecordRestoreResults.Unknown;
        return HostManagerSmartRecordRestoreResults.IsSupported(result) ? result
            : throw new ArgumentException("A smart record restore result must use the history result vocabulary.", nameof(results));
    }

    private readonly record struct RecordRow(HostManagerAppliedPlacementReceipt Placement, HostManagerAppliedRecord Record);
}

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(HostManagerAppliedPlacementReceipt))]
internal partial class SmartRecordHistoryJsonContext : JsonSerializerContext;
