using Microsoft.Data.Sqlite;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization.Reports;
using ResourceManager.App.Infrastructure.Persistence;

namespace Resource_Manager_APP.Tests;

public sealed class NativeReportCoordinatorPersistenceStoreTests : IDisposable
{
    private readonly string testRoot = Path.Combine(
        Path.GetTempPath(),
        $"rm-native-report-store-{Guid.NewGuid():N}");

    [Fact]
    public async Task PersistAndLoadRoundTripKeepsExactCanonicalRows()
    {
        var store = new NativeReportCoordinatorPersistenceStore(CreateDatabase());
        var first = CreateRow(
            NativeReportPersistenceKind.Source,
            identity: 11,
            mutation: 1,
            slotGeneration: 21);
        var second = CreateRow(
            NativeReportPersistenceKind.Trust,
            identity: 12,
            mutation: 2,
            slotGeneration: 22);

        await store.PersistAsync(
            new NativeReportPersistenceOperation[] { first, second },
            CancellationToken.None);
        var rows = await store.LoadAsync(CancellationToken.None);

        Assert.Equal(2, rows.Length);
        Assert.Equal(first.OperationKind, rows[0].OperationKind);
        Assert.Equal(first.IdentityHandle, rows[0].IdentityHandle);
        Assert.Equal(first.MutationVersion, rows[0].MutationVersion);
        Assert.Equal(second.OperationKind, rows[1].OperationKind);
        Assert.Equal(second.IdentityHandle, rows[1].IdentityHandle);
        Assert.Equal(second.PayloadHandle, rows[1].PayloadHandle);
    }

    [Fact]
    public async Task OneTransactionAppliesUpsertAndDeleteTogether()
    {
        var store = new NativeReportCoordinatorPersistenceStore(CreateDatabase());
        var first = CreateRow(
            NativeReportPersistenceKind.Source,
            identity: 11,
            mutation: 1,
            slotGeneration: 21);
        var second = CreateRow(
            NativeReportPersistenceKind.Trust,
            identity: 12,
            mutation: 2,
            slotGeneration: 22);
        await store.PersistAsync(
            new NativeReportPersistenceOperation[] { first, second },
            CancellationToken.None);

        var updated = first with
        {
            MutationVersion = 3,
            CurrentValue = 9.5
        };
        var deleted = second with
        {
            Flags = (uint)NativeReportPersistenceFlags.Delete,
            MutationVersion = 4
        };
        await store.PersistAsync(
            new NativeReportPersistenceOperation[] { updated, deleted },
            CancellationToken.None);
        var rows = await store.LoadAsync(CancellationToken.None);

        var only = Assert.Single(rows);
        Assert.Equal(updated.IdentityHandle, only.IdentityHandle);
        Assert.Equal(9.5, only.CurrentValue);
        Assert.Equal(3UL, only.MutationVersion);
    }

    [Fact]
    public async Task InvalidLaterRowLeavesDatabaseUntouched()
    {
        var database = CreateDatabase();
        var store = new NativeReportCoordinatorPersistenceStore(database);
        var first = CreateRow(
            NativeReportPersistenceKind.Source,
            identity: 11,
            mutation: 1,
            slotGeneration: 21);
        var invalid = CreateRow(
            NativeReportPersistenceKind.Trust,
            identity: 12,
            mutation: 1,
            slotGeneration: 22);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.PersistAsync(
                new NativeReportPersistenceOperation[] { first, invalid },
                CancellationToken.None));

        await using var connection = await database.OpenConnectionAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM host_manager_report_coordinator_rows;";
        Assert.Equal(0L, Convert.ToInt64(await command.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task DuplicateStableIdentityAndMixedPlanIdentityAreRejectedBeforeIo()
    {
        var store = new NativeReportCoordinatorPersistenceStore(CreateDatabase());
        var first = CreateRow(
            NativeReportPersistenceKind.Source,
            identity: 11,
            mutation: 1,
            slotGeneration: 21);
        var duplicate = first with { MutationVersion = 2 };
        var mixed = first with
        {
            IdentityHandle = 12,
            MutationVersion = 2,
            PlanEpoch = 99
        };

        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.PersistAsync(
                new NativeReportPersistenceOperation[] { first, duplicate },
                CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.PersistAsync(
                new NativeReportPersistenceOperation[] { first, mixed },
                CancellationToken.None));
    }

    [Fact]
    public async Task NonFiniteSecondaryScalarIsRejectedBeforeIo()
    {
        var store = new NativeReportCoordinatorPersistenceStore(CreateDatabase());
        var invalid = CreateRow(
            NativeReportPersistenceKind.Observation,
            identity: 11,
            mutation: 1,
            slotGeneration: 21) with
        {
            Flags = (uint)NativeReportPersistenceFlags.SecondaryCurrentValid,
            SecondaryCurrentValue = double.NaN
        };

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.PersistAsync(
                new NativeReportPersistenceOperation[] { invalid },
                CancellationToken.None));
    }

    [Fact]
    public async Task PreviousMetadataCheckpointIsUpgradedAtThePersistenceBoundary()
    {
        var store = new NativeReportCoordinatorPersistenceStore(CreateDatabase());
        var metadata = CreateRow(
            NativeReportPersistenceKind.Metadata,
            identity: 1,
            mutation: 1,
            slotGeneration: 1) with
        {
            CheckpointSchemaVersion = 0x0005_0000,
            CheckpointLogicalUtcMilliseconds = 1234
        };

        await store.PersistAsync(
            new NativeReportPersistenceOperation[] { metadata },
            CancellationToken.None);
        var loaded = Assert.Single(await store.LoadAsync(CancellationToken.None));

        Assert.Equal(NativeReportCoordinatorAbi.Version, loaded.CheckpointSchemaVersion);
        Assert.Equal(1234, loaded.CheckpointLogicalUtcMilliseconds);
    }

    [Fact]
    public void LegacyProfileGenerationIsReboundOnlyForExactRuleIdentity()
    {
        var plan = HostManagerTestPlanFactory.CreatePlan().ReportCoordinator;
        var rule = plan.Recreate.Rules[0];
        const ulong legacyGeneration = 42UL << 32;
        var source = CreateRow(
            NativeReportPersistenceKind.Source,
            identity: 11,
            mutation: 1,
            slotGeneration: 21) with
        {
            SourceHandle = rule.SourceHandle,
            CoverageScopeHandle = rule.CoverageScopeHandle
        };
        var matching = CreateRow(
            NativeReportPersistenceKind.Observation,
            identity: 12,
            mutation: 2,
            slotGeneration: 22) with
        {
            SourceHandle = rule.SourceHandle,
            CoverageScopeHandle = rule.CoverageScopeHandle,
            RuleHandle = rule.RuleHandle,
            RuleGeneration = legacyGeneration,
            TargetHandle = rule.CoverageScopeHandle
        };
        var incompatible = matching with
        {
            IdentityHandle = 13,
            MutationVersion = 3,
            SourceHandle = rule.SourceHandle + 1
        };
        var unknownGeneration = matching with
        {
            IdentityHandle = 14,
            MutationVersion = 4,
            RuleGeneration = legacyGeneration + 1
        };
        var unsupportedLegacyRevision = matching with
        {
            IdentityHandle = 15,
            MutationVersion = 5,
            RuleGeneration = 40UL << 32
        };
        var metadata = CreateRow(
            NativeReportPersistenceKind.Metadata,
            identity: 1,
            mutation: 6,
            slotGeneration: 1) with
        {
            CheckpointSchemaVersion = NativeReportCoordinatorAbi.Version
        };

        var rebound = NativeReportCoordinatorPersistenceMigration.RebindLoadedRows(
            [
                source,
                matching,
                incompatible,
                unknownGeneration,
                unsupportedLegacyRevision,
                metadata
            ],
            plan);

        Assert.Equal(3, rebound.Length);
        Assert.Equal(
            rule.RuleGeneration,
            rebound.Single(row => row.IdentityHandle == matching.IdentityHandle)
                .RuleGeneration);
        Assert.DoesNotContain(
            rebound,
            row => row.IdentityHandle is 13 or 14 or 15);
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
        var contentRoot = Directory.CreateDirectory(
            Path.Combine(testRoot, "Resource Manager-APP")).FullName;
        return new ResourceManagerDatabase(new TestHostEnvironment(contentRoot));
    }

    private static NativeReportPersistenceOperation CreateRow(
        NativeReportPersistenceKind kind,
        ulong identity,
        ulong mutation,
        ulong slotGeneration)
    {
        return new NativeReportPersistenceOperation
        {
            StructSize = 352,
            SessionInstanceLow = 101,
            SessionInstanceHigh = 102,
            PlanEpoch = 7,
            MutationVersion = mutation,
            IdentityHandle = identity,
            SlotGeneration = slotGeneration,
            OperationKind = (uint)kind,
            PayloadHandle = kind == NativeReportPersistenceKind.Trust ? 42UL : 0,
            CurrentValue = kind == NativeReportPersistenceKind.Source ? 1 : 0
        };
    }

    private sealed class TestHostEnvironment(string contentRootPath) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "ResourceManager.Tests";
        public string ContentRootPath { get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
