using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Persistence;

namespace ResourceManager.App.Infrastructure.Optimization.Reports;

internal interface INativeReportCoordinatorPersistenceStore
{
    Task<NativeReportPersistenceOperation[]> LoadAsync(
        CancellationToken cancellationToken);

    Task<HostManagerReportTargetDescriptor[]> LoadTargetsAsync(CancellationToken cancellationToken);

    Task PersistAsync(
        ReadOnlyMemory<NativeReportPersistenceOperation> operations,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<ulong, HostManagerReportTargetDescriptor>? targets = null);
}

internal sealed class NativeReportCoordinatorPersistenceStore(
    ResourceManagerDatabase database) : INativeReportCoordinatorPersistenceStore
{
    private const int RowSize = 352;

    public async Task<NativeReportPersistenceOperation[]> LoadAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT operation_kind, identity_handle, slot_generation,
                   mutation_version, row_payload
            FROM host_manager_report_coordinator_rows
            ORDER BY operation_kind, identity_handle;
            """;

        var rows = new List<NativeReportPersistenceOperation>();
        var keys = new HashSet<(uint Kind, ulong Identity)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var kind = checked((uint)reader.GetInt32(0));
            var identity = unchecked((ulong)reader.GetInt64(1));
            var slotGeneration = unchecked((ulong)reader.GetInt64(2));
            var mutationVersion = unchecked((ulong)reader.GetInt64(3));
            var payload = reader.GetFieldValue<byte[]>(4);
            if (payload.Length != RowSize)
            {
                throw new InvalidDataException(
                    "A persisted report-coordinator row has an invalid payload size.");
            }

            var row = MemoryMarshal.Read<NativeReportPersistenceOperation>(payload);
            ValidateCanonicalStoredRow(
                row,
                kind,
                identity,
                slotGeneration,
                mutationVersion);
            row = NativeReportCoordinatorPersistenceMigration.UpgradeLoadedRow(row);
            if (!keys.Add((kind, identity)))
            {
                throw new InvalidDataException(
                    "The report-coordinator store contains a duplicate stable identity.");
            }
            rows.Add(row);
        }

        return [.. rows];
    }

    public async Task PersistAsync(
        ReadOnlyMemory<NativeReportPersistenceOperation> operations,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<ulong, HostManagerReportTargetDescriptor>? targets = null)
    {
        ValidatePlan(operations.Span);

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        for (var index = 0; index < operations.Length; index++)
        {
            var row = operations.Span[index];
            cancellationToken.ThrowIfCancellationRequested();
            if ((((NativeReportPersistenceFlags)row.Flags)
                    & NativeReportPersistenceFlags.Delete) != 0)
            {
                await DeleteAsync(connection, transaction, row, cancellationToken);
            }
            else
            {
                await UpsertAsync(connection, transaction, row, cancellationToken);
            }
        }
        await PersistTargetsAsync(connection, transaction, targets, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<HostManagerReportTargetDescriptor[]> LoadTargetsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT target_handle, software_id, software_name, software_kind, display_kind, process_evidence FROM host_manager_report_target_identities;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var targets = new List<HostManagerReportTargetDescriptor>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var process = reader.IsDBNull(5) ? null : System.Text.Json.JsonSerializer.Deserialize<ResourceManager.App.Domain.Optimization.OptimizationProcessEvidence>(reader.GetString(5))
                ?? throw new InvalidDataException("Persisted process report evidence is null.");
            targets.Add(new HostManagerReportTargetDescriptor(unchecked((ulong)reader.GetInt64(0)),
                reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                process is null ? [] : [process.Name], process is null ? [] : [process.ProcessId], process));
        }
        return targets.ToArray();
    }

    private static async Task PersistTargetsAsync(SqliteConnection connection, SqliteTransaction transaction,
        IReadOnlyDictionary<ulong, HostManagerReportTargetDescriptor>? targets, CancellationToken cancellationToken)
    {
        // Native rows own retention; this table only describes their live targets.
        var retained = new HashSet<ulong>();
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT row_payload FROM host_manager_report_coordinator_rows;";
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var row = MemoryMarshal.Read<NativeReportPersistenceOperation>(reader.GetFieldValue<byte[]>(0));
                if (row.TargetHandle != 0) retained.Add(row.TargetHandle);
            }
        }
        foreach (var target in targets?.Values ?? [])
        {
            if (!retained.Contains(target.TargetHandle)) continue;
            await using var write = connection.CreateCommand();
            write.Transaction = transaction;
            write.CommandText = """
                INSERT INTO host_manager_report_target_identities(target_handle, software_id, software_name, software_kind, display_kind, process_evidence)
                VALUES ($handle, $id, $name, $kind, $display, $process)
                ON CONFLICT(target_handle) DO UPDATE SET software_id=excluded.software_id,
                    software_name=excluded.software_name, software_kind=excluded.software_kind,
                    display_kind=excluded.display_kind, process_evidence=excluded.process_evidence;
                """;
            write.Parameters.AddWithValue("$handle", unchecked((long)target.TargetHandle));
            write.Parameters.AddWithValue("$id", target.SoftwareId);
            write.Parameters.AddWithValue("$name", target.SoftwareName);
            write.Parameters.AddWithValue("$kind", target.SoftwareKind);
            write.Parameters.AddWithValue("$display", target.DisplayKind);
            write.Parameters.AddWithValue("$process", target.Process is null ? DBNull.Value : System.Text.Json.JsonSerializer.Serialize(target.Process));
            await write.ExecuteNonQueryAsync(cancellationToken);
        }
        var obsolete = new List<long>();
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT target_handle FROM host_manager_report_target_identities;";
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                if (!retained.Contains(unchecked((ulong)reader.GetInt64(0)))) obsolete.Add(reader.GetInt64(0));
        }
        foreach (var handle in obsolete)
        {
            await using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM host_manager_report_target_identities WHERE target_handle=$handle;";
            delete.Parameters.AddWithValue("$handle", handle);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static void ValidatePlan(
        ReadOnlySpan<NativeReportPersistenceOperation> rows)
    {
        if (rows.IsEmpty)
        {
            throw new ArgumentException(
                "A report-coordinator persistence plan must contain at least one row.",
                nameof(rows));
        }

        var keys = new HashSet<(uint Kind, ulong Identity)>();
        var first = rows[0];
        if (first.SessionInstanceLow == 0
            || first.SessionInstanceHigh == 0
            || first.PlanEpoch == 0)
        {
            throw new ArgumentException(
                "The report-coordinator persistence plan identity is invalid.",
                nameof(rows));
        }

        ulong previousMutation = 0;
        foreach (ref readonly var row in rows)
        {
            ValidateCommon(in row);
            if (row.SessionInstanceLow != first.SessionInstanceLow
                || row.SessionInstanceHigh != first.SessionInstanceHigh
                || row.PlanEpoch != first.PlanEpoch
                || row.MutationVersion <= previousMutation)
            {
                throw new ArgumentException(
                    "The report-coordinator persistence plan is not one exact ordered batch.",
                    nameof(rows));
            }
            if (!keys.Add((row.OperationKind, row.IdentityHandle)))
            {
                throw new ArgumentException(
                    "The report-coordinator persistence plan contains a duplicate stable identity.",
                    nameof(rows));
            }
            previousMutation = row.MutationVersion;
        }
    }

    private static void ValidateCanonicalStoredRow(
        NativeReportPersistenceOperation row,
        uint expectedKind,
        ulong expectedIdentity,
        ulong expectedSlotGeneration,
        ulong expectedMutationVersion)
    {
        ValidateCommon(in row);
        if ((((NativeReportPersistenceFlags)row.Flags)
                & NativeReportPersistenceFlags.Delete) != 0
            || row.OperationKind != expectedKind
            || row.IdentityHandle != expectedIdentity
            || row.SlotGeneration != expectedSlotGeneration
            || row.MutationVersion != expectedMutationVersion)
        {
            throw new InvalidDataException(
                "A persisted report-coordinator row does not match its stable key.");
        }
    }

    private static void ValidateCommon(in NativeReportPersistenceOperation row)
    {
        if (row.StructSize != RowSize
            || (row.Flags & ~(uint)NativeReportPersistenceFlags.Known) != 0
            || row.SessionInstanceLow == 0
            || row.SessionInstanceHigh == 0
            || row.PlanEpoch == 0
            || row.MutationVersion == 0
            || row.IdentityHandle == 0
            || row.SlotGeneration == 0
            || row.OperationKind is < (uint)NativeReportPersistenceKind.Source
                or > (uint)NativeReportPersistenceKind.Metadata
            || row.SourceReservedU32 != 0
            || row.CheckpointReservedU32 != 0
            || !double.IsFinite(row.CurrentValue)
            || !double.IsFinite(row.ValueSum)
            || !double.IsFinite(row.PeakValue)
            || !double.IsFinite(row.WindowDeltaValue)
            || !double.IsFinite(row.SecondaryCurrentValue))
        {
            throw new InvalidDataException(
                "A report-coordinator persistence row violates the fixed v6 shape.");
        }
    }

    private static async Task UpsertAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        NativeReportPersistenceOperation row,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO host_manager_report_coordinator_rows(
                operation_kind, identity_handle, slot_generation,
                mutation_version, row_payload)
            VALUES ($kind, $identity, $slot, $mutation, $payload)
            ON CONFLICT(operation_kind, identity_handle) DO UPDATE SET
                slot_generation = excluded.slot_generation,
                mutation_version = excluded.mutation_version,
                row_payload = excluded.row_payload;
            """;
        command.Parameters.AddWithValue("$kind", checked((int)row.OperationKind));
        command.Parameters.AddWithValue("$identity", unchecked((long)row.IdentityHandle));
        command.Parameters.AddWithValue("$slot", unchecked((long)row.SlotGeneration));
        command.Parameters.AddWithValue("$mutation", unchecked((long)row.MutationVersion));
        command.Parameters.Add("$payload", SqliteType.Blob).Value = Serialize(in row);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task DeleteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        NativeReportPersistenceOperation row,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM host_manager_report_coordinator_rows
            WHERE operation_kind = $kind
              AND identity_handle = $identity;
            """;
        command.Parameters.AddWithValue("$kind", checked((int)row.OperationKind));
        command.Parameters.AddWithValue("$identity", unchecked((long)row.IdentityHandle));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static byte[] Serialize(in NativeReportPersistenceOperation row)
    {
        var copy = row;
        return MemoryMarshal.AsBytes(
            MemoryMarshal.CreateReadOnlySpan(ref copy, 1)).ToArray();
    }
}
