using Microsoft.Data.Sqlite;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Persistence;
using ResourceManager.App.Infrastructure.SoftwareDiscovery;

namespace Resource_Manager_APP.Tests;

public sealed class NativePortableSoftwareRegistryPersistenceStoreTests : IDisposable
{
    private readonly string testRoot = Path.Combine(
        Path.GetTempPath(),
        $"rm-native-portable-store-{Guid.NewGuid():N}");

    [Fact]
    public async Task PersistAsync_CommitsExactRowsThenReturnsExactFeedback()
    {
        var database = CreateDatabase();
        var catalog = new NativePortableSoftwareRegistryPayloadCatalog(firstHandle: 100);
        var store = new NativePortableSoftwareRegistryPersistenceStore(database, catalog);
        var firstObserved = DateTimeOffset.FromUnixTimeMilliseconds(1_234_000);
        var first = CreateOperation(
            catalog,
            mutationVersion: 1,
            executablePath: "c:/portable/tool.exe",
            rootPath: "c:/portable",
            firstObserved,
            NativePortableSoftwarePersistenceFlags.IdentityConfirmed);
        var second = CreateOperation(
            catalog,
            mutationVersion: 2,
            executablePath: "c:/portable/helper.exe",
            rootPath: "c:/portable",
            firstObserved.AddSeconds(1),
            NativePortableSoftwarePersistenceFlags.RootConfirmed);

        var feedback = await store.PersistAsync([first, second], CancellationToken.None);

        Assert.Equal(2, feedback.Length);
        Assert.Equal(1UL, feedback[0].MutationVersion);
        Assert.Equal(first.SoftwareHandle, feedback[0].SoftwareHandle);
        Assert.Equal(first.PathHandle, feedback[0].PathHandle);
        Assert.Equal(2UL, feedback[1].MutationVersion);
        await using var connection = await database.OpenConnectionAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT software_id, catalog_entry_id, display_name, kind,
                   executable_path, root_path, first_observed_utc_ticks,
                   identity_confirmed, root_confirmed
            FROM portable_software_executables
            ORDER BY executable_path;
            """;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("catalog:tool", reader.GetString(0));
        Assert.Equal("tool", reader.GetString(1));
        Assert.Equal("Tool", reader.GetString(2));
        Assert.Equal("portable", reader.GetString(3));
        Assert.Equal("c:/portable/helper.exe", reader.GetString(4));
        Assert.Equal("c:/portable", reader.GetString(5));
        Assert.Equal(firstObserved.AddSeconds(1).UtcTicks, reader.GetInt64(6));
        Assert.Equal(0, reader.GetInt32(7));
        Assert.Equal(1, reader.GetInt32(8));
        Assert.True(await reader.ReadAsync());
        Assert.Equal("c:/portable/tool.exe", reader.GetString(4));
        Assert.Equal(firstObserved.UtcTicks, reader.GetInt64(6));
        Assert.Equal(1, reader.GetInt32(7));
        Assert.Equal(0, reader.GetInt32(8));
        Assert.False(await reader.ReadAsync());
    }

    [Fact]
    public async Task InvalidLaterOperationLeavesTheWholeDatabaseUntouched()
    {
        var database = CreateDatabase();
        var catalog = new NativePortableSoftwareRegistryPayloadCatalog(firstHandle: 1);
        var store = new NativePortableSoftwareRegistryPersistenceStore(database, catalog);
        var valid = CreateOperation(
            catalog,
            mutationVersion: 1,
            executablePath: "c:/portable/tool.exe",
            rootPath: "c:/portable",
            DateTimeOffset.UnixEpoch,
            NativePortableSoftwarePersistenceFlags.None);
        var invalid = valid with
        {
            MutationVersion = 2,
            PathHandle = ulong.MaxValue
        };

        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.PersistAsync([valid, invalid], CancellationToken.None));

        await using var connection = await database.OpenConnectionAsync(CancellationToken.None);
        Assert.Equal(0L, await CountRowsAsync(connection));
    }

    [Fact]
    public async Task DeleteIsIdempotentAndOnlyRemovesTheExactCanonicalKey()
    {
        var database = CreateDatabase();
        var catalog = new NativePortableSoftwareRegistryPayloadCatalog(firstHandle: 1);
        var store = new NativePortableSoftwareRegistryPersistenceStore(database, catalog);
        var first = CreateOperation(
            catalog,
            mutationVersion: 1,
            executablePath: "c:/portable/tool.exe",
            rootPath: "c:/portable",
            DateTimeOffset.UnixEpoch,
            NativePortableSoftwarePersistenceFlags.None);
        var second = CreateOperation(
            catalog,
            mutationVersion: 2,
            executablePath: "c:/portable/helper.exe",
            rootPath: "c:/portable",
            DateTimeOffset.UnixEpoch,
            NativePortableSoftwarePersistenceFlags.None);
        await store.PersistAsync([first, second], CancellationToken.None);
        var delete = first with
        {
            MutationVersion = 3,
            Flags = (uint)NativePortableSoftwarePersistenceFlags.Delete
        };

        var feedback = await store.PersistAsync([delete], CancellationToken.None);
        var retry = delete with { MutationVersion = 4 };
        var retryFeedback = await store.PersistAsync([retry], CancellationToken.None);

        Assert.Equal(3UL, Assert.Single(feedback).MutationVersion);
        Assert.Equal(4UL, Assert.Single(retryFeedback).MutationVersion);
        await using var connection = await database.OpenConnectionAsync(CancellationToken.None);
        Assert.Equal(1L, await CountRowsAsync(connection));
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT executable_path FROM portable_software_executables;";
        Assert.Equal("c:/portable/helper.exe", Convert.ToString(await command.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task UnknownFlagsAndNonIncreasingMutationsFailBeforeOpeningAWriteTransaction()
    {
        var database = CreateDatabase();
        var catalog = new NativePortableSoftwareRegistryPayloadCatalog(firstHandle: 1);
        var store = new NativePortableSoftwareRegistryPersistenceStore(database, catalog);
        var first = CreateOperation(
            catalog,
            mutationVersion: 2,
            executablePath: "c:/portable/tool.exe",
            rootPath: "c:/portable",
            DateTimeOffset.UnixEpoch,
            NativePortableSoftwarePersistenceFlags.None);
        var unknownFlags = first with { Flags = 1U << 31 };

        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.PersistAsync([unknownFlags], CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.PersistAsync([first, first], CancellationToken.None));

        await using var connection = await database.OpenConnectionAsync(CancellationToken.None);
        Assert.Equal(0L, await CountRowsAsync(connection));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(testRoot, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (IOException)
        {
        }
    }

    private ResourceManagerDatabase CreateDatabase()
    {
        var contentRoot = Directory.CreateDirectory(Path.Combine(testRoot, "Resource Manager-APP")).FullName;
        return new ResourceManagerDatabase(new TestHostEnvironment(contentRoot));
    }

    private static NativePortableSoftwarePersistenceOperation CreateOperation(
        NativePortableSoftwareRegistryPayloadCatalog catalog,
        ulong mutationVersion,
        string executablePath,
        string rootPath,
        DateTimeOffset firstObservedAt,
        NativePortableSoftwarePersistenceFlags flags)
    {
        var handles = catalog.GetOrAddBatch(
        [
            new(NativePortableSoftwarePayloadKind.SoftwareId, "catalog:tool"),
            new(NativePortableSoftwarePayloadKind.CatalogEntryId, "tool"),
            new(NativePortableSoftwarePayloadKind.DisplayName, "Tool"),
            new(NativePortableSoftwarePayloadKind.SoftwareKind, "portable"),
            new(NativePortableSoftwarePayloadKind.ExecutablePath, executablePath),
            new(NativePortableSoftwarePayloadKind.RootPath, rootPath)
        ]);
        return new NativePortableSoftwarePersistenceOperation
        {
            StructSize = 88,
            Flags = (uint)flags,
            MutationVersion = mutationVersion,
            SoftwareHandle = handles[0],
            CatalogEntryHandle = handles[1],
            DisplayNameHandle = handles[2],
            SoftwareKindHandle = handles[3],
            PathHandle = handles[4],
            RootHandle = handles[5],
            FirstObservedUtcMilliseconds = firstObservedAt.ToUnixTimeMilliseconds()
        };
    }

    private static async Task<long> CountRowsAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM portable_software_executables;";
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed class TestHostEnvironment(string contentRootPath) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "ResourceManager.Tests";
        public string ContentRootPath { get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
