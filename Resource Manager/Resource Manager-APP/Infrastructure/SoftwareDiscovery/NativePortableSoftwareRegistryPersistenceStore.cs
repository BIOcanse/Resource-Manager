using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Persistence;

namespace ResourceManager.App.Infrastructure.SoftwareDiscovery;

internal sealed class NativePortableSoftwareRegistryPersistenceStore
{
    private readonly ResourceManagerDatabase database;
    private readonly NativePortableSoftwareRegistryPayloadCatalog payloadCatalog;

    public NativePortableSoftwareRegistryPersistenceStore(
        ResourceManagerDatabase database,
        NativePortableSoftwareRegistryPayloadCatalog payloadCatalog)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(payloadCatalog);
        this.database = database;
        this.payloadCatalog = payloadCatalog;
    }

    public async Task<NativePortableSoftwarePersistenceFeedback[]> PersistAsync(
        IReadOnlyList<NativePortableSoftwarePersistenceOperation> operations,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operations);
        if (operations.Count == 0)
        {
            throw new ArgumentException("Portable software persistence batch cannot be empty.", nameof(operations));
        }

        var commands = ValidateBatch(operations);
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        foreach (var command in commands)
        {
            await ApplyAsync(connection, transaction, command, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return commands
            .Select(static command => new NativePortableSoftwarePersistenceFeedback
            {
                StructSize = (uint)Marshal.SizeOf<NativePortableSoftwarePersistenceFeedback>(),
                MutationVersion = command.MutationVersion,
                SoftwareHandle = command.SoftwareHandle,
                PathHandle = command.PathHandle
            })
            .ToArray();
    }

    private PersistenceCommand[] ValidateBatch(
        IReadOnlyList<NativePortableSoftwarePersistenceOperation> operations)
    {
        var commands = new PersistenceCommand[operations.Count];
        ulong previousMutationVersion = 0;
        for (var index = 0; index < operations.Count; index++)
        {
            var operation = operations[index];
            ValidateOperation(operation, index, previousMutationVersion);
            previousMutationVersion = operation.MutationVersion;
            commands[index] = new PersistenceCommand(
                operation.MutationVersion,
                operation.SoftwareHandle,
                operation.PathHandle,
                Resolve(NativePortableSoftwarePayloadKind.SoftwareId, operation.SoftwareHandle, index),
                Resolve(NativePortableSoftwarePayloadKind.CatalogEntryId, operation.CatalogEntryHandle, index),
                Resolve(NativePortableSoftwarePayloadKind.DisplayName, operation.DisplayNameHandle, index),
                Resolve(NativePortableSoftwarePayloadKind.SoftwareKind, operation.SoftwareKindHandle, index),
                Resolve(NativePortableSoftwarePayloadKind.ExecutablePath, operation.PathHandle, index),
                Resolve(NativePortableSoftwarePayloadKind.RootPath, operation.RootHandle, index),
                DateTimeOffset.FromUnixTimeMilliseconds(operation.FirstObservedUtcMilliseconds).UtcTicks,
                (operation.Flags & (uint)NativePortableSoftwarePersistenceFlags.IdentityConfirmed) != 0,
                (operation.Flags & (uint)NativePortableSoftwarePersistenceFlags.RootConfirmed) != 0,
                (operation.Flags & (uint)NativePortableSoftwarePersistenceFlags.Delete) != 0);
        }

        return commands;
    }

    private string Resolve(
        NativePortableSoftwarePayloadKind kind,
        ulong handle,
        int operationIndex)
    {
        try
        {
            return payloadCatalog.ResolveRequired(kind, handle).Text;
        }
        catch (KeyNotFoundException ex)
        {
            throw new ArgumentException(
                $"Portable software persistence operation {operationIndex} has no {kind} payload for handle {handle}.",
                "operations",
                ex);
        }
    }

    private static unsafe void ValidateOperation(
        NativePortableSoftwarePersistenceOperation operation,
        int operationIndex,
        ulong previousMutationVersion)
    {
        if (operation.StructSize != (uint)Marshal.SizeOf<NativePortableSoftwarePersistenceOperation>()
            || operation.MutationVersion == 0
            || operation.MutationVersion <= previousMutationVersion
            || operation.SoftwareHandle == 0
            || operation.CatalogEntryHandle == 0
            || operation.DisplayNameHandle == 0
            || operation.SoftwareKindHandle == 0
            || operation.PathHandle == 0
            || operation.RootHandle == 0
            || operation.FirstObservedUtcMilliseconds < 0
            || (operation.Flags & ~(uint)NativePortableSoftwarePersistenceFlags.Known) != 0
            || operation.Reserved[0] != 0
            || operation.Reserved[1] != 0)
        {
            throw new ArgumentException(
                $"Portable software persistence operation {operationIndex} violates the native POD contract.",
                "operations");
        }

        try
        {
            _ = DateTimeOffset.FromUnixTimeMilliseconds(operation.FirstObservedUtcMilliseconds);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new ArgumentException(
                $"Portable software persistence operation {operationIndex} has an invalid UTC timestamp.",
                "operations",
                ex);
        }
    }

    private static async Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PersistenceCommand operation,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        if (operation.Delete)
        {
            command.CommandText = """
                DELETE FROM portable_software_executables
                WHERE software_id = $softwareId AND executable_path = $executablePath;
                """;
            command.Parameters.AddWithValue("$softwareId", operation.SoftwareId);
            command.Parameters.AddWithValue("$executablePath", operation.ExecutablePath);
        }
        else
        {
            command.CommandText = """
                INSERT INTO portable_software_executables(
                    software_id, catalog_entry_id, display_name, kind,
                    executable_path, root_path, first_observed_utc_ticks,
                    identity_confirmed, root_confirmed)
                VALUES (
                    $softwareId, $catalogEntryId, $displayName, $kind,
                    $executablePath, $rootPath, $firstObservedAt,
                    $identityConfirmed, $rootConfirmed)
                ON CONFLICT(software_id, executable_path) DO UPDATE SET
                    catalog_entry_id = excluded.catalog_entry_id,
                    display_name = excluded.display_name,
                    kind = excluded.kind,
                    root_path = excluded.root_path,
                    identity_confirmed = excluded.identity_confirmed,
                    root_confirmed = excluded.root_confirmed;
                """;
            command.Parameters.AddWithValue("$softwareId", operation.SoftwareId);
            command.Parameters.AddWithValue("$catalogEntryId", operation.CatalogEntryId);
            command.Parameters.AddWithValue("$displayName", operation.DisplayName);
            command.Parameters.AddWithValue("$kind", operation.SoftwareKind);
            command.Parameters.AddWithValue("$executablePath", operation.ExecutablePath);
            command.Parameters.AddWithValue("$rootPath", operation.RootPath);
            command.Parameters.AddWithValue("$firstObservedAt", operation.FirstObservedUtcTicks);
            command.Parameters.AddWithValue("$identityConfirmed", operation.IdentityConfirmed ? 1 : 0);
            command.Parameters.AddWithValue("$rootConfirmed", operation.RootConfirmed ? 1 : 0);
        }

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private sealed record PersistenceCommand(
        ulong MutationVersion,
        ulong SoftwareHandle,
        ulong PathHandle,
        string SoftwareId,
        string CatalogEntryId,
        string DisplayName,
        string SoftwareKind,
        string ExecutablePath,
        string RootPath,
        long FirstObservedUtcTicks,
        bool IdentityConfirmed,
        bool RootConfirmed,
        bool Delete);
}
