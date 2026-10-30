using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using ResourceManager.App.Domain.Software;
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

    // This store is the only writer of both report tables. The gate keeps the
    // committed mirror below equal to what the last successful write left.
    private readonly SemaphoreSlim writeGate = new(1, 1);

    // Committed row targets and target identities. Null until first read and
    // from the start of every write until its commit, so a failed or cancelled
    // write reads the database again instead of trusting a guess.
    private CommittedTables? committed;

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
            if (!keys.Add((kind, identity)))
            {
                throw new InvalidDataException(
                    "The report-coordinator store contains a duplicate stable identity.");
            }
            rows.Add(row);
        }

        await reader.DisposeAsync();
        var upgraded = NativeReportCoordinatorPersistenceMigration.UpgradeLoadedRows(rows);
        var changed = upgraded.Where((row, index) => !row.Equals(rows[index])).ToArray();
        if (changed.Length != 0)
        {
            await writeGate.WaitAsync(cancellationToken);
            try
            {
                committed = null;
                // Rewrite the checkpoint and report evidence together, so a later
                // restart cannot see a v7 checkpoint beside unconverted report rows.
                await using var transaction =
                    (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
                await using var upsert = CreateUpsertCommand(connection, transaction);
                foreach (var row in changed)
                    await UpsertAsync(upsert, row, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            finally { writeGate.Release(); }
        }
        return upgraded;
    }

    public async Task PersistAsync(
        ReadOnlyMemory<NativeReportPersistenceOperation> operations,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<ulong, HostManagerReportTargetDescriptor>? targets = null)
    {
        ValidatePlan(operations.Span);

        await writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await database.OpenConnectionAsync(cancellationToken);
            await using var transaction =
                (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            var tables = committed ?? await ReadCommittedAsync(connection, transaction, cancellationToken);
            committed = null;
            // One statement per kind for the whole batch: SQLite prepares it on
            // first use and every later row only rebinds its parameters.
            await using var upsert = CreateUpsertCommand(connection, transaction);
            await using var delete = CreateDeleteCommand(connection, transaction);
            for (var index = 0; index < operations.Length; index++)
            {
                var row = operations.Span[index];
                cancellationToken.ThrowIfCancellationRequested();
                var key = (row.OperationKind, row.IdentityHandle);
                if ((((NativeReportPersistenceFlags)row.Flags)
                        & NativeReportPersistenceFlags.Delete) != 0)
                {
                    await DeleteAsync(delete, row, cancellationToken);
                    tables.RowTargets.Remove(key);
                }
                else
                {
                    await UpsertAsync(upsert, row, cancellationToken);
                    if (row.TargetHandle != 0) tables.RowTargets[key] = row.TargetHandle;
                    else tables.RowTargets.Remove(key);
                }
            }
            await PersistTargetsAsync(connection, transaction, tables, targets, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            committed = tables;
        }
        finally { writeGate.Release(); }
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
                reader.GetString(1), reader.GetString(2), SoftwareKinds.ReadStored(reader.GetString(3)),
                StoredOptimizationWireValues.ReadSoftwareDisplayKind(reader.GetString(4)),
                process is null ? [] : [process.Name], process is null ? [] : [process.ProcessId], process));
        }
        return targets.ToArray();
    }

    private static async Task<CommittedTables> ReadCommittedAsync(SqliteConnection connection,
        SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        var tables = new CommittedTables();
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT operation_kind, identity_handle, row_payload FROM host_manager_report_coordinator_rows;";
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var row = MemoryMarshal.Read<NativeReportPersistenceOperation>(reader.GetFieldValue<byte[]>(2));
                if (row.TargetHandle != 0)
                    tables.RowTargets[(checked((uint)reader.GetInt32(0)), unchecked((ulong)reader.GetInt64(1)))] = row.TargetHandle;
            }
        }
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT target_handle, software_id, software_name, software_kind, display_kind, process_evidence FROM host_manager_report_target_identities;";
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                tables.Targets[unchecked((ulong)reader.GetInt64(0))] = new StoredTarget(reader.GetString(1), reader.GetString(2),
                    reader.GetString(3), reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5));
        }
        return tables;
    }

    private static async Task PersistTargetsAsync(SqliteConnection connection, SqliteTransaction transaction,
        CommittedTables tables, IReadOnlyDictionary<ulong, HostManagerReportTargetDescriptor>? targets,
        CancellationToken cancellationToken)
    {
        // Native rows own retention; this table describes the target of every
        // retained row. A target is written only when its stored values differ.
        var retained = tables.RowTargets.Values.ToHashSet();
        await using var write = connection.CreateCommand();
        write.Transaction = transaction;
        write.CommandText = """
            INSERT INTO host_manager_report_target_identities(target_handle, software_id, software_name, software_kind, display_kind, process_evidence)
            VALUES ($handle, $id, $name, $kind, $display, $process)
            ON CONFLICT(target_handle) DO UPDATE SET software_id=excluded.software_id,
                software_name=excluded.software_name, software_kind=excluded.software_kind,
                display_kind=excluded.display_kind, process_evidence=excluded.process_evidence;
            """;
        write.Parameters.Add("$handle", SqliteType.Integer);
        write.Parameters.Add("$id", SqliteType.Text);
        write.Parameters.Add("$name", SqliteType.Text);
        write.Parameters.Add("$kind", SqliteType.Text);
        write.Parameters.Add("$display", SqliteType.Text);
        write.Parameters.Add("$process", SqliteType.Text);
        foreach (var target in targets?.Values ?? [])
        {
            if (!retained.Contains(target.TargetHandle)) continue;
            var stored = new StoredTarget(target.SoftwareId, target.SoftwareName, target.SoftwareKind, target.DisplayKind,
                target.Process is null ? null : System.Text.Json.JsonSerializer.Serialize(target.Process));
            if (tables.Targets.TryGetValue(target.TargetHandle, out var current) && current == stored) continue;
            write.Parameters["$handle"].Value = unchecked((long)target.TargetHandle);
            write.Parameters["$id"].Value = stored.SoftwareId;
            write.Parameters["$name"].Value = stored.SoftwareName;
            write.Parameters["$kind"].Value = stored.SoftwareKind;
            write.Parameters["$display"].Value = stored.DisplayKind;
            write.Parameters["$process"].Value = stored.Process is null ? DBNull.Value : stored.Process;
            await write.ExecuteNonQueryAsync(cancellationToken);
            tables.Targets[target.TargetHandle] = stored;
        }
        var obsolete = tables.Targets.Keys.Where(handle => !retained.Contains(handle)).ToArray();
        if (obsolete.Length == 0) return;
        await using var delete = connection.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = "DELETE FROM host_manager_report_target_identities WHERE target_handle=$handle;";
        delete.Parameters.Add("$handle", SqliteType.Integer);
        foreach (var handle in obsolete)
        {
            delete.Parameters["$handle"].Value = unchecked((long)handle);
            await delete.ExecuteNonQueryAsync(cancellationToken);
            tables.Targets.Remove(handle);
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
                "A report-coordinator persistence row violates the fixed v8 shape.");
        }
    }

    private static SqliteCommand CreateUpsertCommand(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        var command = connection.CreateCommand();
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
        command.Parameters.Add("$kind", SqliteType.Integer);
        command.Parameters.Add("$identity", SqliteType.Integer);
        command.Parameters.Add("$slot", SqliteType.Integer);
        command.Parameters.Add("$mutation", SqliteType.Integer);
        command.Parameters.Add("$payload", SqliteType.Blob);
        return command;
    }

    private static async Task UpsertAsync(
        SqliteCommand command,
        NativeReportPersistenceOperation row,
        CancellationToken cancellationToken)
    {
        command.Parameters["$kind"].Value = checked((int)row.OperationKind);
        command.Parameters["$identity"].Value = unchecked((long)row.IdentityHandle);
        command.Parameters["$slot"].Value = unchecked((long)row.SlotGeneration);
        command.Parameters["$mutation"].Value = unchecked((long)row.MutationVersion);
        command.Parameters["$payload"].Value = Serialize(in row);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static SqliteCommand CreateDeleteCommand(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM host_manager_report_coordinator_rows
            WHERE operation_kind = $kind
              AND identity_handle = $identity;
            """;
        command.Parameters.Add("$kind", SqliteType.Integer);
        command.Parameters.Add("$identity", SqliteType.Integer);
        return command;
    }

    private static async Task DeleteAsync(
        SqliteCommand command,
        NativeReportPersistenceOperation row,
        CancellationToken cancellationToken)
    {
        command.Parameters["$kind"].Value = checked((int)row.OperationKind);
        command.Parameters["$identity"].Value = unchecked((long)row.IdentityHandle);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static byte[] Serialize(in NativeReportPersistenceOperation row)
    {
        var copy = row;
        return MemoryMarshal.AsBytes(
            MemoryMarshal.CreateReadOnlySpan(ref copy, 1)).ToArray();
    }

    private sealed class CommittedTables
    {
        internal Dictionary<(uint Kind, ulong Identity), ulong> RowTargets { get; } = [];

        internal Dictionary<ulong, StoredTarget> Targets { get; } = [];
    }

    // The exact column values of one target identity row.
    private readonly record struct StoredTarget(
        string SoftwareId,
        string SoftwareName,
        string SoftwareKind,
        string DisplayKind,
        string? Process);
}
