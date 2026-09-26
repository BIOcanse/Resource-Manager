using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Persistence;

namespace ResourceManager.App.Infrastructure.Optimization.Reports;

internal interface INativeReportCoordinatorPersistenceStore
{
    Task<NativeReportPersistenceOperation[]> LoadAsync(
        CancellationToken cancellationToken);

    Task PersistAsync(
        ReadOnlyMemory<NativeReportPersistenceOperation> operations,
        CancellationToken cancellationToken);
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
        CancellationToken cancellationToken)
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
        await transaction.CommitAsync(cancellationToken);
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
