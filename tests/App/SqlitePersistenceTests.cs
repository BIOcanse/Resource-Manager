using ResourceManager.App.Infrastructure.Adaptation;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Infrastructure.Optimization;
using ResourceManager.App.Infrastructure.Indexing;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Persistence;
using ResourceManager.App.Infrastructure.SoftwareDiscovery;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Domain.SoftwareDiscovery;
using ResourceManager.App.Domain.RuntimeSpecialization;

namespace Resource_Manager_APP.Tests;

[Collection(SoftwareIdentityCatalogProcessStateCollection.Name)]
public sealed partial class SqlitePersistenceTests : IDisposable
{
    private readonly ResourceManagerLedgerOwner ledgerOwner = new();
    private readonly string testRoot = Path.Combine(Path.GetTempPath(), $"rm-sqlite-{Guid.NewGuid():N}");

    [Fact]
    public async Task PortableRegistryCapturedPublicationAfterStopDoesNotReadDatabase()
    {
        var result = await DeliverCapturedPublicationAfterStopAsync();
        // Loading persisted rows canonicalizes this legacy kind. The unchanged value
        // proves the captured callback never entered the database import path.
        Assert.Equal("Game", result.StoredKind);
    }

    [Fact]
    public async Task PortableRegistryCapturedPublicationAfterStopDoesNotReplaceContext()
    {
        var result = await DeliverCapturedPublicationAfterStopAsync();
        Assert.Same(result.Before, result.After);
    }

    private async Task<(object Before, object After, string StoredKind)> DeliverCapturedPublicationAfterStopAsync()
    {
        var deployment = new HostManagerDeploymentState();
        var provider = new RuntimePlanProvider(deployment);
        provider.Publish(CompiledRuntimePlan.Default with { Version = 1, HostManager = HostManagerTestPlanFactory.CreatePlan() });
        var captured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        provider.Published += plan =>
        {
            if (plan.Version != 2) return;
            captured.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(15)))
                throw new TimeoutException("The captured publication was not released.");
        };
        var database = CreateDatabase();
        using var registry = new SqlitePortableSoftwareRegistry(database, provider,
            new HostManagerPortableSoftwareRegistryRuntime(provider, deployment),
            NullLogger<SqlitePortableSoftwareRegistry>.Instance, ledgerOwner);
        await registry.StartAsync(default);
        var publication = Task.Run(() => provider.Publish(CompiledRuntimePlan.Default with
        {
            Version = 2,
            HostManager = HostManagerTestPlanFactory.CreatePlan(profile =>
                profile["host_recreate"]!["portable_software_registry"]!["capacity"]!["maximum_executable_path_byte_count"] = 64,
                planEpoch: 2)
        }));
        try
        {
            await captured.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await registry.StopAsync(default);
            var contextField = typeof(SqlitePortableSoftwareRegistry).GetField("context",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            var before = contextField.GetValue(registry)!;
            await InsertPortableRowAsync(database, "catalog:late", "late", "Late", "Game",
                "c:/late/app.exe", "c:/late",
                DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).UtcTicks, 0, 0);
            release.Set();
            Assert.Empty((await publication.WaitAsync(TimeSpan.FromSeconds(5))).DeliveryFailures);
            await using var connection = await database.OpenConnectionAsync(default);
            var kind = await ScalarStringAsync(connection, "SELECT kind FROM portable_software_executables;");
            return (before, contextField.GetValue(registry)!, kind);
        }
        finally
        {
            release.Set();
            await publication.WaitAsync(TimeSpan.FromSeconds(5));
            await registry.StopAsync(default);
        }
    }

    [Fact]
    public async Task PortableRegistryDelayedPriorRunPublicationCannotReplaceRestartedConfiguration()
    {
        var deployment = new HostManagerDeploymentState();
        var provider = new RuntimePlanProvider(deployment);
        provider.Publish(CompiledRuntimePlan.Default with { Version = 1, HostManager = HostManagerTestPlanFactory.CreatePlan() });
        var callbackCaptured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseCallback = new ManualResetEventSlim();
        provider.Published += plan =>
        {
            if (plan.Version != 2) return;
            callbackCaptured.TrySetResult();
            if (!releaseCallback.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("The held publication callback was not released.");
        };
        using var registry = new SqlitePortableSoftwareRegistry(CreateDatabase(), provider,
            new HostManagerPortableSoftwareRegistryRuntime(provider, deployment),
            NullLogger<SqlitePortableSoftwareRegistry>.Instance, ledgerOwner);
        await registry.StartAsync(default);
        var delayed = Task.Run(() => provider.Publish(CompiledRuntimePlan.Default with
        {
            Version = 2, HostManager = HostManagerTestPlanFactory.CreatePlan(planEpoch: 2)
        }));
        try
        {
            await callbackCaptured.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await registry.StopAsync(default);
            provider.Publish(CompiledRuntimePlan.Default with
            {
                Version = 3,
                HostManager = HostManagerTestPlanFactory.CreatePlan(profile =>
                    profile["host_recreate"]!["portable_software_registry"]!["capacity"]!["maximum_executable_path_byte_count"] = 64,
                    planEpoch: 3)
            });
            await registry.StartAsync(default);
            var observation = new PortableSoftwareObservation("catalog:delayed", "delayed", "Delayed", "other",
                "c:/portable/" + new string('a', 128) + ".exe", "c:/portable");
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => registry.ObserveAsync(observation, default));

            releaseCallback.Set();
            Assert.Empty((await delayed.WaitAsync(TimeSpan.FromSeconds(5))).DeliveryFailures);

            // The real projection must still enforce the restarted run's shorter path limit.
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => registry.ObserveAsync(observation, default));
        }
        finally
        {
            releaseCallback.Set();
            await delayed.WaitAsync(TimeSpan.FromSeconds(5));
            await registry.StopAsync(default);
        }
    }

    [Fact]
    public async Task SamePortableRegistryRestartsAndPersistsNewObservationBeforeStop()
    {
        var database = CreateDatabase();
        using var registry = CreatePortableRegistry(database);
        for (var run = 1; run <= 2; run++)
        {
            await registry.StartAsync(default);
            try
            {
                var root = Path.Combine(testRoot, "Portable", "Restart" + run);
                await registry.ObserveAsync(new PortableSoftwareObservation("catalog:restart" + run, "restart" + run,
                    "Restart " + run, "other", Path.Combine(root, "restart.exe"), root), default);
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await using var connection = await database.OpenConnectionAsync(deadline.Token);
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM portable_software_executables;";
                while ((long)(await command.ExecuteScalarAsync(deadline.Token))! < run)
                    await Task.Delay(1, deadline.Token);
                Assert.Equal(run, (long)(await command.ExecuteScalarAsync(deadline.Token))!);
            }
            finally { await registry.StopAsync(default); }
        }
    }

    [Theory]
    [InlineData("Game", SoftwareKinds.Game)]
    [InlineData("highperformance", SoftwareKinds.HighPerformance)]
    public async Task PortableSoftwareRegistry_RewritesLegacyKinds(string storedKind, string expected)
    {
        var database = CreateDatabase();
        var observedAt = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await InsertPortableRowAsync(database, "catalog:legacy", "legacy", "Legacy", storedKind,
            "e:/games/genshin/yuanshen.exe", "e:/games/genshin", observedAt.UtcTicks, 1, 1);
        using var registry = CreatePortableRegistry(database);
        await registry.StartAsync(CancellationToken.None);
        try
        {
            Assert.Equal(expected, Assert.Single(registry.GetSnapshot()).Kind);
            await using var connection = await database.OpenConnectionAsync(CancellationToken.None);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT kind FROM portable_software_executables;";
            Assert.Equal(expected, await command.ExecuteScalarAsync());
        }
        finally
        {
            await registry.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task PortableSoftwareRegistry_UnknownStoredKindFailsWithoutRewritingRows()
    {
        var database = CreateDatabase();
        await InsertPortableRowAsync(database, "catalog:legacy", "legacy", "Legacy", "not_a_kind",
            "e:/games/genshin/yuanshen.exe", "e:/games/genshin", 0, 1, 1);
        using var registry = CreatePortableRegistry(database);
        var failure = await Assert.ThrowsAsync<InvalidDataException>(() => registry.StartAsync(CancellationToken.None));
        var execution = registry.ExecuteTask;
        Assert.NotNull(execution);
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidDataException>(() => execution!));
        await registry.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(execution!.IsFaulted);
        await using var connection = await database.OpenConnectionAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT kind FROM portable_software_executables;";
        Assert.Equal("not_a_kind", await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task EnsureInitializedAsync_CreatesWalDatabaseWithFts5SchemaIdempotently()
    {
        var database = CreateDatabase();

        await database.EnsureInitializedAsync(CancellationToken.None);
        await database.EnsureInitializedAsync(CancellationToken.None);

        Assert.True(File.Exists(database.DatabasePath));
        await using var connection = await database.OpenConnectionAsync(CancellationToken.None);
        Assert.Equal(15L, await ScalarInt64Async(connection, "PRAGMA user_version;"));
        Assert.Equal(1L, await ScalarInt64Async(connection, "SELECT sqlite_compileoption_used('ENABLE_FTS5');"));
        Assert.Equal("wal", await ScalarStringAsync(connection, "PRAGMA journal_mode;"));
        Assert.Equal(1L, await ScalarInt64Async(
            connection,
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'software_file_name_fts';"));
        var updateTrigger = await ScalarStringAsync(
            connection,
            "SELECT sql FROM sqlite_master WHERE type = 'trigger' AND name = 'software_index_entries_au';");
        Assert.Contains("AFTER UPDATE OF file_name, relative_path", updateTrigger, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WHEN old.file_name <> new.file_name", updateTrigger, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1L, await ScalarInt64Async(
            connection,
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'portable_software_executables';"));
    }

    [Fact]
    public async Task EnsureInitializedAsync_QuarantinesConfirmedCorruptionAndCreatesHealthyDatabase()
    {
        var database = CreateDatabase();
        Directory.CreateDirectory(Path.GetDirectoryName(database.DatabasePath)!);
        var corruptBytes = "not-a-sqlite-database"u8.ToArray();
        await File.WriteAllBytesAsync(database.DatabasePath, corruptBytes);

        await database.EnsureInitializedAsync(CancellationToken.None);

        var state = Assert.IsType<ResourceManagerDatabaseInitializationState>(database.InitializationState);
        Assert.Equal(
            ResourceManagerDatabaseInitializationDisposition.RecoveredAfterCorruption,
            state.Disposition);
        Assert.NotNull(state.QuarantineDatabasePath);
        Assert.True(File.Exists(state.QuarantineDatabasePath));
        Assert.Equal(corruptBytes, await File.ReadAllBytesAsync(state.QuarantineDatabasePath));
        Assert.False(string.IsNullOrWhiteSpace(state.RecoveryReason));

        await using var connection = await database.OpenConnectionAsync(CancellationToken.None);
        Assert.Equal("ok", await ScalarStringAsync(connection, "PRAGMA quick_check(1);"));
        Assert.Equal(15L, await ScalarInt64Async(connection, "PRAGMA user_version;"));
    }

    [Fact]
    public void QuarantineFamily_MovesMainWalAndSharedMemoryAsOneArtifactSet()
    {
        var databasePath = Path.Combine(testRoot, "Database", "resource-manager.db");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        File.WriteAllBytes(databasePath, [1, 2, 3]);
        File.WriteAllBytes(databasePath + "-wal", [4, 5]);
        File.WriteAllBytes(databasePath + "-shm", [6]);

        var artifact = SqliteDatabaseRecovery.QuarantineFamily(
            databasePath,
            new DateTimeOffset(2026, 8, 21, 0, 0, 0, TimeSpan.Zero));

        Assert.False(File.Exists(databasePath));
        Assert.False(File.Exists(databasePath + "-wal"));
        Assert.False(File.Exists(databasePath + "-shm"));
        Assert.Equal([1, 2, 3], File.ReadAllBytes(artifact.DatabasePath));
        Assert.Equal([4, 5], File.ReadAllBytes(Assert.IsType<string>(artifact.WriteAheadLogPath)));
        Assert.Equal([6], File.ReadAllBytes(Assert.IsType<string>(artifact.SharedMemoryPath)));
    }

    [Fact]
    public async Task EnsureInitializedAsync_DoesNotQuarantineUnconfirmedOpenFailure()
    {
        var database = CreateDatabase();
        Directory.CreateDirectory(database.DatabasePath);

        await Assert.ThrowsAnyAsync<Exception>(
            () => database.EnsureInitializedAsync(CancellationToken.None));

        Assert.Null(database.InitializationState);
        Assert.True(Directory.Exists(database.DatabasePath));
        var parent = Path.GetDirectoryName(database.DatabasePath)!;
        Assert.Empty(Directory.GetFiles(parent, "resource-manager.corrupt.*.db"));
    }

    [Fact]
    public async Task EnsureInitializedAsync_MigratesLegacyFileIndexWithoutLosingEntries()
    {
        var database = CreateDatabase();
        await CreateLegacyFileIndexDatabaseAsync(database.DatabasePath);

        await database.EnsureInitializedAsync(CancellationToken.None);

        await using var connection = await database.OpenConnectionAsync(CancellationToken.None);
        Assert.Equal(15L, await ScalarInt64Async(connection, "PRAGMA user_version;"));
        Assert.Equal(0L, await ScalarInt64Async(
            connection,
            "SELECT COUNT(*) FROM pragma_table_info('software_index_entries') WHERE name IN ('software_id', 'software_name');"));
        Assert.Equal(1L, await ScalarInt64Async(connection, "SELECT COUNT(*) FROM software_index_entries;"));

        using var workspace = NativeFileQueryTestWorkspaceFactory.Create();
        var index = new SqliteSoftwareFileIndex(
            database,
            workspace,
            NullLogger<SqliteSoftwareFileIndex>.Instance);
        var match = Assert.Single(await index.SearchAsync("legacy", 10, CancellationToken.None));
        Assert.Equal("legacy-data.bin", match.FileName);
        Assert.Equal("Legacy App", match.SoftwareName);
    }

    [Fact]
    public async Task PortableSoftwareRegistry_PersistsUniquePathsAndRemovesMissingExecutablesOnRefresh()
    {
        var database = CreateDatabase();
        var portableRoot = Directory.CreateDirectory(Path.Combine(testRoot, "Portable", "Skyrim")).FullName;
        var canonicalPortableRoot = NativePortableSoftwareRegistryProjection.CanonicalPath(
            portableRoot,
            nameof(portableRoot));
        var executablePath = Path.Combine(portableRoot, "SkyrimSE.exe");
        await File.WriteAllBytesAsync(executablePath, [0x4D, 0x5A]);
        var observation = new PortableSoftwareObservation(
            "catalog:game-skyrim-special-edition",
            "game-skyrim-special-edition",
            "The Elder Scrolls V: Skyrim Special Edition",
            SoftwareKinds.Game,
            executablePath,
            portableRoot);

        var first = CreatePortableRegistry(database);
        await first.StartAsync(CancellationToken.None);
        await first.ObserveAsync(observation, CancellationToken.None);
        await first.ObserveAsync(observation, CancellationToken.None);
        var firstSnapshot = Assert.Single(first.GetSnapshot());
        Assert.Single(firstSnapshot.ExecutablePaths);
        Assert.Empty(firstSnapshot.RootPaths);
        Assert.Equal(canonicalPortableRoot, Assert.Single(firstSnapshot.SuggestedRootPaths!));
        Assert.True(firstSnapshot.RequiresRootPathConfirmation);

        var confirmation = await first.ConfirmRootPathAsync(
            new PortableSoftwareRootConfirmationRequest(observation.SoftwareId, portableRoot),
            CancellationToken.None);
        Assert.False(confirmation.RequiresRootPathConfirmation);
        var confirmedSnapshot = Assert.Single(first.GetSnapshot());
        Assert.Equal(canonicalPortableRoot, Assert.Single(confirmedSnapshot.RootPaths));
        Assert.True(confirmedSnapshot.IdentityConfirmed);
        await first.StopAsync(CancellationToken.None);

        var reloaded = CreatePortableRegistry(database);
        await reloaded.StartAsync(CancellationToken.None);
        var reloadedRegistration = Assert.Single(reloaded.GetSnapshot());
        Assert.False(reloadedRegistration.RequiresRootPathConfirmation);
        Assert.Equal(canonicalPortableRoot, Assert.Single(reloadedRegistration.RootPaths));
        File.Delete(executablePath);
        Assert.Empty(await reloaded.RefreshAsync(CancellationToken.None));
        await reloaded.StopAsync(CancellationToken.None);

        var afterDelete = CreatePortableRegistry(database);
        await afterDelete.StartAsync(CancellationToken.None);
        Assert.Empty(afterDelete.GetSnapshot());
        await afterDelete.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task PortableSoftwareRegistry_RewritesLegacyKeysToOneCanonicalPrimaryKeyTransaction()
    {
        var database = CreateDatabase();
        await database.EnsureInitializedAsync(CancellationToken.None);
        var root = Path.Combine(testRoot, "Portable", "Tool");
        var executable = Path.Combine(root, "Tool.exe");
        var observedAt = DateTimeOffset.FromUnixTimeMilliseconds(
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await InsertPortableRowAsync(
            database,
            "CATALOG:TOOL",
            "TOOL",
            " Tool ",
            "Other",
            executable.ToUpperInvariant(),
            root.ToUpperInvariant(),
            observedAt.UtcTicks,
            identityConfirmed: 1,
            rootConfirmed: 1);

        using var registry = CreatePortableRegistry(database);
        await registry.StartAsync(CancellationToken.None);
        var registration = Assert.Single(registry.GetSnapshot());
        await registry.StopAsync(CancellationToken.None);

        var canonicalExecutable = NativePortableSoftwareRegistryProjection.CanonicalPath(
            executable,
            nameof(executable));
        Assert.Equal("catalog:tool", registration.SoftwareId);
        Assert.Equal("tool", registration.CatalogEntryId);
        Assert.Equal("Tool", registration.Name);
        Assert.Equal("other", registration.Kind);
        Assert.Equal(canonicalExecutable, Assert.Single(registration.ExecutablePaths));
        await using var connection = await database.OpenConnectionAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT software_id, executable_path
            FROM portable_software_executables;
            """;
        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
        Assert.True(await reader.ReadAsync(CancellationToken.None));
        Assert.Equal("catalog:tool", reader.GetString(0));
        Assert.Equal(canonicalExecutable, reader.GetString(1));
        Assert.False(await reader.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task PortableSoftwareRegistry_ImportsFuturePersistedObservationWithoutClamping()
    {
        var database = CreateDatabase();
        await database.EnsureInitializedAsync(CancellationToken.None);
        var root = Path.Combine(testRoot, "Portable", "ClockRollback");
        var executable = Path.Combine(root, "ClockRollback.exe");
        var firstObservedAt = DateTimeOffset.FromUnixTimeMilliseconds(
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).AddHours(8);
        await InsertPortableRowAsync(
            database,
            "catalog:clock-rollback",
            "clock-rollback",
            "Clock Rollback",
            "other",
            executable,
            root,
            firstObservedAt.UtcTicks,
            identityConfirmed: 0,
            rootConfirmed: 0);

        using var registry = CreatePortableRegistry(database);
        await registry.StartAsync(CancellationToken.None);
        PortableSoftwareRegistration registration;
        try
        {
            registration = Assert.Single(registry.GetSnapshot());
        }
        finally
        {
            await registry.StopAsync(CancellationToken.None);
        }

        Assert.Equal(firstObservedAt, registration.FirstObservedAt);
        await using var connection = await database.OpenConnectionAsync(CancellationToken.None);
        Assert.Equal(
            firstObservedAt.UtcTicks,
            await ScalarInt64Async(
                connection,
                "SELECT first_observed_utc_ticks FROM portable_software_executables;"));
    }

    [Fact]
    public async Task EnsureInitializedAsync_MigratesPortableObservationTicksToPublishedMillisecondPrecision()
    {
        var database = CreateDatabase();
        await database.EnsureInitializedAsync(CancellationToken.None);
        var root = Path.Combine(testRoot, "Portable", "Legacy");
        var executable = Path.Combine(root, "Legacy.exe");
        var alignedTicks = DateTimeOffset.FromUnixTimeMilliseconds(
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).UtcTicks;
        var legacyTicks = checked(alignedTicks + 4_321);
        await InsertPortableRowAsync(
            database,
            "catalog:legacy",
            "legacy",
            "Legacy",
            "other",
            executable,
            root,
            legacyTicks,
            identityConfirmed: 0,
            rootConfirmed: 0);

        await using (var connection = await database.OpenConnectionAsync(CancellationToken.None))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                DROP TABLE targeted_frame_interval_chunks;
                DROP TABLE targeted_resource_samples;
                DROP TABLE targeted_recordings;
                DROP TABLE host_manager_report_target_identities;
                DROP TABLE host_manager_smart_record_history;
                DELETE FROM schema_migrations WHERE version IN (11, 12, 13, 14, 15);
                PRAGMA user_version=10;
                """;
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }

        var migrated = CreateDatabase();
        await migrated.EnsureInitializedAsync(CancellationToken.None);

        await using (var connection = await migrated.OpenConnectionAsync(CancellationToken.None))
        {
            Assert.Equal(15L, await ScalarInt64Async(connection, "PRAGMA user_version;"));
            Assert.Equal(
                alignedTicks,
                await ScalarInt64Async(
                    connection,
                    "SELECT first_observed_utc_ticks FROM portable_software_executables;"));
        }

        using var registry = CreatePortableRegistry(migrated);
        await registry.StartAsync(CancellationToken.None);
        Assert.Single(registry.GetSnapshot());
        await registry.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task PortableSoftwareRegistry_CanonicalCollisionFailsClosedWithoutRewritingRows()
    {
        var database = CreateDatabase();
        await database.EnsureInitializedAsync(CancellationToken.None);
        var root = Path.Combine(testRoot, "Portable", "Tool");
        var executable = Path.Combine(root, "Tool.exe");
        var observedAt = DateTimeOffset.FromUnixTimeMilliseconds(
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await InsertPortableRowAsync(
            database,
            "catalog:tool",
            "tool",
            "Tool",
            "other",
            executable,
            root,
            observedAt.UtcTicks,
            identityConfirmed: 0,
            rootConfirmed: 0);
        await InsertPortableRowAsync(
            database,
            "CATALOG:TOOL",
            "TOOL",
            "Tool",
            "Other",
            executable.ToUpperInvariant(),
            root.ToUpperInvariant(),
            observedAt.UtcTicks,
            identityConfirmed: 0,
            rootConfirmed: 0);

        using var registry = CreatePortableRegistry(database);
        var failure = await Assert.ThrowsAsync<InvalidDataException>(
            () => registry.StartAsync(CancellationToken.None));
        var execution = registry.ExecuteTask;
        Assert.NotNull(execution);
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidDataException>(() => execution!));
        await registry.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(execution!.IsFaulted);

        await using var connection = await database.OpenConnectionAsync(CancellationToken.None);
        Assert.Equal(
            2L,
            await ScalarInt64Async(
                connection,
                "SELECT COUNT(*) FROM portable_software_executables;"));
    }

    public void Dispose()
    {
        ledgerOwner.Dispose();
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
        Directory.CreateDirectory(Path.Combine(testRoot, "Resource Manager-APP"));
        return new ResourceManagerDatabase(CreateEnvironment());
    }

    private SqlitePortableSoftwareRegistry CreatePortableRegistry(ResourceManagerDatabase database)
    {
        var deployment = new HostManagerDeploymentState();
        var provider = new RuntimePlanProvider(deployment);
        provider.Publish(CompiledRuntimePlan.Default with
        {
            Version = 1,
            HostManager = HostManagerTestPlanFactory.CreatePlan()
        });
        return new SqlitePortableSoftwareRegistry(
            database,
            provider,
            new HostManagerPortableSoftwareRegistryRuntime(provider, deployment),
            NullLogger<SqlitePortableSoftwareRegistry>.Instance,
            ledgerOwner);
    }

    private static async Task InsertPortableRowAsync(
        ResourceManagerDatabase database,
        string softwareId,
        string catalogEntryId,
        string displayName,
        string kind,
        string executablePath,
        string rootPath,
        long firstObservedUtcTicks,
        int identityConfirmed,
        int rootConfirmed)
    {
        await using var connection = await database.OpenConnectionAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO portable_software_executables(
                software_id, catalog_entry_id, display_name, kind,
                executable_path, root_path, first_observed_utc_ticks,
                identity_confirmed, root_confirmed)
            VALUES (
                $softwareId, $catalogEntryId, $displayName, $kind,
                $executablePath, $rootPath, $firstObservedAt,
                $identityConfirmed, $rootConfirmed);
            """;
        command.Parameters.AddWithValue("$softwareId", softwareId);
        command.Parameters.AddWithValue("$catalogEntryId", catalogEntryId);
        command.Parameters.AddWithValue("$displayName", displayName);
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$executablePath", executablePath);
        command.Parameters.AddWithValue("$rootPath", rootPath);
        command.Parameters.AddWithValue("$firstObservedAt", firstObservedUtcTicks);
        command.Parameters.AddWithValue("$identityConfirmed", identityConfirmed);
        command.Parameters.AddWithValue("$rootConfirmed", rootConfirmed);
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private TestHostEnvironment CreateEnvironment()
        => new(Path.Combine(testRoot, "Resource Manager-APP"));

    private static async Task<long> ScalarInt64Async(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<string> ScalarStringAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static async Task CreateLegacyFileIndexDatabaseAsync(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE schema_migrations (
                version INTEGER NOT NULL PRIMARY KEY,
                name TEXT NOT NULL,
                applied_utc_ticks INTEGER NOT NULL
            ) STRICT;
            INSERT INTO schema_migrations VALUES (1, 'initial-dynamic-data-and-file-index', 1);
            PRAGMA user_version=1;

            CREATE TABLE storage_metadata (
                key TEXT NOT NULL PRIMARY KEY,
                value TEXT NOT NULL,
                updated_utc_ticks INTEGER NOT NULL
            ) STRICT;
            CREATE TABLE indexed_volumes (
                volume_key TEXT NOT NULL PRIMARY KEY,
                root_path TEXT NOT NULL,
                file_system TEXT,
                volume_serial TEXT,
                journal_id TEXT,
                next_usn INTEGER,
                last_full_index_utc_ticks INTEGER,
                last_incremental_index_utc_ticks INTEGER
            ) STRICT;
            CREATE TABLE optimization_observations (
                key TEXT NOT NULL PRIMARY KEY,
                report_id TEXT NOT NULL,
                type TEXT NOT NULL,
                target_type TEXT NOT NULL,
                target_key TEXT NOT NULL,
                software_id TEXT,
                last_observed_utc_ticks INTEGER NOT NULL,
                payload_json TEXT NOT NULL
            ) STRICT;
            CREATE TABLE software_index_roots (
                id INTEGER PRIMARY KEY,
                software_id TEXT NOT NULL,
                software_name TEXT NOT NULL,
                root_path TEXT NOT NULL,
                root_path_key TEXT NOT NULL,
                root_kind TEXT NOT NULL,
                total_bytes INTEGER NOT NULL DEFAULT 0,
                file_count INTEGER NOT NULL DEFAULT 0,
                last_indexed_utc_ticks INTEGER,
                UNIQUE(software_id, root_path_key)
            ) STRICT;
            CREATE TABLE software_index_entries (
                id INTEGER PRIMARY KEY,
                root_id INTEGER NOT NULL REFERENCES software_index_roots(id) ON DELETE CASCADE,
                software_id TEXT NOT NULL,
                software_name TEXT NOT NULL,
                relative_path TEXT NOT NULL,
                relative_path_key TEXT NOT NULL,
                file_name TEXT NOT NULL,
                extension TEXT NOT NULL,
                size_bytes INTEGER NOT NULL,
                last_write_utc_ticks INTEGER NOT NULL,
                attributes INTEGER NOT NULL,
                UNIQUE(root_id, relative_path_key)
            ) STRICT;
            CREATE INDEX ix_software_index_entries_root ON software_index_entries(root_id);
            CREATE INDEX ix_software_index_entries_software ON software_index_entries(software_id);
            CREATE INDEX ix_software_index_entries_extension ON software_index_entries(extension);
            CREATE VIRTUAL TABLE software_file_search_fts USING fts5(
                file_name,
                relative_path,
                software_name,
                content='software_index_entries',
                content_rowid='id',
                tokenize='trigram'
            );
            CREATE TRIGGER software_index_entries_ai AFTER INSERT ON software_index_entries BEGIN
                INSERT INTO software_file_search_fts(rowid, file_name, relative_path, software_name)
                VALUES (new.id, new.file_name, new.relative_path, new.software_name);
            END;

            INSERT INTO software_index_roots(
                id, software_id, software_name, root_path, root_path_key, root_kind, total_bytes, file_count, last_indexed_utc_ticks)
            VALUES (1, 'legacy-app', 'Legacy App', 'C:\Legacy', 'C:\LEGACY', 'Program', 5, 1, 1);
            INSERT INTO software_index_entries(
                id, root_id, software_id, software_name, relative_path, relative_path_key, file_name, extension,
                size_bytes, last_write_utc_ticks, attributes)
            VALUES (1, 1, 'legacy-app', 'Legacy App', 'legacy-data.bin', 'LEGACY-DATA.BIN', 'legacy-data.bin', '.bin', 5, 1, 0);
            """;
        await command.ExecuteNonQueryAsync();
    }

    private sealed class TestHostEnvironment(string contentRootPath) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "ResourceManager.Tests";
        public string ContentRootPath { get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
