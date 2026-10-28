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

    [Theory]
    [InlineData(0x0005_0000U)]
    [InlineData(0x0006_0000U)]
    [InlineData(0x0007_0000U)]
    public async Task PreviousMetadataCheckpointIsUpgradedAtThePersistenceBoundary(uint version)
    {
        var store = new NativeReportCoordinatorPersistenceStore(CreateDatabase());
        var metadata = CreateRow(
            NativeReportPersistenceKind.Metadata,
            identity: 1,
            mutation: 1,
            slotGeneration: 1) with
        {
            CheckpointSchemaVersion = version,
            CheckpointLogicalUtcMilliseconds = 1234
        };

        await store.PersistAsync(
            new NativeReportPersistenceOperation[] { metadata },
            CancellationToken.None);
        var loaded = Assert.Single(await store.LoadAsync(CancellationToken.None));

        Assert.Equal(NativeReportCoordinatorAbi.Version, loaded.CheckpointSchemaVersion);
        Assert.Equal(1234, loaded.CheckpointLogicalUtcMilliseconds);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task LegacyReportUpgradeRetainsLifecycleTrustAndEvidenceAcrossRepeatedLoads(
        bool active, bool trusted)
    {
        var store = new NativeReportCoordinatorPersistenceStore(CreateDatabase());
        var observation = CreateRow(NativeReportPersistenceKind.Observation, 11, 1, 1) with
        {
            SourceHandle = 101, CoverageScopeHandle = 102, RuleHandle = 103, TargetHandle = 104,
            Flags = (uint)NativeReportPersistenceFlags.CurrentValid,
            CurrentValue = 75, ValueSum = 210, PeakValue = 90,
            SampleCount = 3, ActiveSampleCount = 2
        };
        var report = CreateRow(NativeReportPersistenceKind.Report, 12, 2, 1) with
        {
            SourceHandle = 101, CoverageScopeHandle = 102, RuleHandle = 103, TargetHandle = 104,
            ReportHandle = 12,
            Flags = (uint)((active ? NativeReportPersistenceFlags.Active : 0)
                | (trusted ? NativeReportPersistenceFlags.Trusted : 0))
        };
        var metadata = CreateRow(NativeReportPersistenceKind.Metadata, 1, 3, 1) with
        {
            CheckpointSchemaVersion = 0x0006_0000
        };
        await store.PersistAsync(new[] { observation, report, metadata }, CancellationToken.None);

        for (var restart = 0; restart < 2; restart++)
        {
            var loaded = await store.LoadAsync(CancellationToken.None);
            var retained = Assert.Single(loaded, row => row.OperationKind == (uint)NativeReportPersistenceKind.Report);
            Assert.Equal(report.Flags, retained.Flags);
            Assert.Equal(75, retained.CurrentValue);
            Assert.Equal(70, retained.ValueSum);
            Assert.Equal(90, retained.PeakValue);
            Assert.Equal(3UL, retained.SampleCount);
            Assert.Equal(2UL, retained.ActiveSampleCount);
            Assert.Equal(NativeReportCoordinatorAbi.Version,
                Assert.Single(loaded, row => row.OperationKind == (uint)NativeReportPersistenceKind.Metadata)
                    .CheckpointSchemaVersion);
            Assert.Equal(report.ReportHandle,
                Assert.Single(loaded, row => row.OperationKind == (uint)NativeReportPersistenceKind.Metadata)
                    .ReportHandle);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RuleRebindingKeepsReportStateAndEvidenceButDropsChangedRuleObservations(bool active)
    {
        var plan = HostManagerTestPlanFactory.CreatePlan().ReportCoordinator;
        var rule = plan.Recreate.Rules[0];
        var source = CreateRow(NativeReportPersistenceKind.Source, 10, 1, 1) with
        {
            SourceHandle = rule.SourceHandle, CoverageScopeHandle = rule.CoverageScopeHandle
        };
        var observation = CreateRow(NativeReportPersistenceKind.Observation, 11, 1, 1) with
        {
            SourceHandle = rule.SourceHandle, CoverageScopeHandle = rule.CoverageScopeHandle,
            RuleHandle = rule.RuleHandle, RuleGeneration = rule.RuleGeneration ^ 1, TargetHandle = 104
        };
        var report = observation with
        {
            OperationKind = (uint)NativeReportPersistenceKind.Report, IdentityHandle = 12,
            ReportHandle = 12, MutationVersion = 2, FamilyHandle = rule.FamilyHandle,
            ReportTypeHandle = rule.ReportTypeHandle, ResourceKindHandle = rule.ResourceKindHandle,
            PayloadHandle = rule.PayloadHandle,
            Flags = (uint)(NativeReportPersistenceFlags.Trusted
                | (active ? NativeReportPersistenceFlags.Active : 0)),
            CurrentValue = 91, ValueSum = 87, PeakValue = 94, SampleCount = 6
        };
        var rebound = NativeReportCoordinatorPersistenceMigration.RebindLoadedRows([source, observation, report], plan);

        Assert.Equal(2, rebound.Length);
        Assert.Contains(source, rebound);
        var retained = Assert.Single(rebound, row => row.OperationKind == (uint)NativeReportPersistenceKind.Report);
        Assert.Equal(report with { RuleGeneration = rule.RuleGeneration }, retained);
    }

    [Theory]
    [InlineData(0x0007_0000U)]
    [InlineData(0x0008_0000U)]
    public async Task CurrentCheckpointKeepsReportEvidenceIndependentOfLaterObservations(uint version)
    {
        var store = new NativeReportCoordinatorPersistenceStore(CreateDatabase());
        var observation = CreateRow(NativeReportPersistenceKind.Observation, 11, 1, 1) with
        {
            SourceHandle = 101, CoverageScopeHandle = 102, RuleHandle = 103, TargetHandle = 104,
            Flags = (uint)NativeReportPersistenceFlags.CurrentValid,
            CurrentValue = 1, ValueSum = 1, PeakValue = 1, SampleCount = 1
        };
        var report = observation with
        {
            OperationKind = (uint)NativeReportPersistenceKind.Report, IdentityHandle = 12,
            ReportHandle = 12, MutationVersion = 2, Flags = (uint)NativeReportPersistenceFlags.Trusted,
            CurrentValue = 91, ValueSum = 87, PeakValue = 94, SampleCount = 6,
            WindowDeltaValue = 18, SecondaryCurrentValue = 54
        };
        var metadata = CreateRow(NativeReportPersistenceKind.Metadata, 1, 3, 1) with
        {
            CheckpointSchemaVersion = version,
            ReportHandle = version == 0x0007_0000U ? 0UL : 77UL
        };
        await store.PersistAsync(new[] { observation, report, metadata }, CancellationToken.None);

        var loaded = await store.LoadAsync(CancellationToken.None);
        Assert.Equal(report, Assert.Single(loaded,
            row => row.OperationKind == (uint)NativeReportPersistenceKind.Report));
        Assert.Equal(version == 0x0007_0000U ? report.ReportHandle : 77UL,
            Assert.Single(loaded, row => row.OperationKind == (uint)NativeReportPersistenceKind.Metadata).ReportHandle);
    }

    [Fact]
    public async Task UnsupportedCheckpointIsRejectedBeforeMigrationWrites()
    {
        var database = CreateDatabase();
        var store = new NativeReportCoordinatorPersistenceStore(database);
        var metadata = CreateRow(NativeReportPersistenceKind.Metadata, 1, 1, 1) with
        {
            CheckpointSchemaVersion = 0x0004_0000
        };
        await store.PersistAsync(new[] { metadata }, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync(CancellationToken.None));
        await using var connection = await database.OpenConnectionAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT row_payload FROM host_manager_report_coordinator_rows;";
        var payload = Assert.IsType<byte[]>(await command.ExecuteScalarAsync());
        Assert.Equal(0x0004_0000U,
            System.Runtime.InteropServices.MemoryMarshal.Read<NativeReportPersistenceOperation>(payload)
                .CheckpointSchemaVersion);
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

    [Fact]
    public async Task MinimalIdentitySurvivesRestartOnlyWhileNativeRowsReferenceIt()
    {
        var database = CreateDatabase();
        var store = new NativeReportCoordinatorPersistenceStore(database);
        var target = new HostManagerReportTargetDescriptor(123, "runtime:editor", "Editor", "other", "other", ["old.exe"], [42]);
        var row = CreateRow(NativeReportPersistenceKind.Trust, 12, 1, 22) with { TargetHandle = 123 };
        await store.PersistAsync(new[] { row }, CancellationToken.None, new Dictionary<ulong, HostManagerReportTargetDescriptor> { [123] = target });
        var restored = Assert.Single(await new NativeReportCoordinatorPersistenceStore(database).LoadTargetsAsync(CancellationToken.None));
        Assert.Equal(target.SoftwareId, restored.SoftwareId);
        Assert.Equal(target.SoftwareName, restored.SoftwareName);
        Assert.Empty(restored.ProcessIds);
        await store.PersistAsync(new[] { row with { MutationVersion = 2, Flags = (uint)NativeReportPersistenceFlags.Delete } }, CancellationToken.None);
        Assert.Empty(await store.LoadTargetsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ProcessInstanceEvidenceSurvivesRestartWithItsSoftwareDescriptor()
    {
        var database = CreateDatabase();
        var store = new NativeReportCoordinatorPersistenceStore(database);
        var evidence = new ResourceManager.App.Domain.Optimization.OptimizationProcessEvidence(42, "123456789123456789",
            "pwsh", 50 * 1024 * 1024, 900, "pwsh -File \"任务.ps1\"");
        var target = new HostManagerReportTargetDescriptor(123, "tool", "PowerShell", "other", "other", ["pwsh"], [42], evidence);
        var row = CreateRow(NativeReportPersistenceKind.Trust, 12, 1, 22) with { TargetHandle = 123 };
        await store.PersistAsync(new[] { row }, CancellationToken.None, new Dictionary<ulong, HostManagerReportTargetDescriptor> { [123] = target });
        var restored = Assert.Single(await new NativeReportCoordinatorPersistenceStore(database).LoadTargetsAsync(CancellationToken.None));
        Assert.Equal(evidence, restored.Process);
        Assert.Equal(target.SoftwareName, restored.SoftwareName);
        Assert.Equal(target.ProcessIds, restored.ProcessIds);
        Assert.Equal(target.ProcessNames, restored.ProcessNames);
    }

    [Fact]
    public async Task LoadTargetsCanonicalizesLegacyStoredSoftwareKinds()
    {
        var database = CreateDatabase();
        var store = new NativeReportCoordinatorPersistenceStore(database);
        var target = new HostManagerReportTargetDescriptor(
            123,
            "runtime:editor",
            "Editor",
            "other",
            "other",
            [],
            []);
        var row = CreateRow(NativeReportPersistenceKind.Trust, 12, 1, 22) with
        {
            TargetHandle = target.TargetHandle
        };
        await store.PersistAsync(
            new[] { row },
            CancellationToken.None,
            new Dictionary<ulong, HostManagerReportTargetDescriptor>
            {
                [target.TargetHandle] = target
            });
        await SetStoredTargetKindsAsync(database, "HighPerformance", "HighPerformance");

        var restored = Assert.Single(await store.LoadTargetsAsync(CancellationToken.None));

        Assert.Equal("high_performance", restored.SoftwareKind);
        Assert.Equal("high_performance", restored.DisplayKind);
    }

    [Theory]
    [InlineData("unknown_software_kind", "other")]
    [InlineData("other", "unknown_display_kind")]
    public async Task LoadTargetsRejectsUnknownStoredKinds(
        string softwareKind,
        string displayKind)
    {
        var database = CreateDatabase();
        var store = new NativeReportCoordinatorPersistenceStore(database);
        var target = new HostManagerReportTargetDescriptor(
            123,
            "runtime:editor",
            "Editor",
            "other",
            "other",
            [],
            []);
        var row = CreateRow(NativeReportPersistenceKind.Trust, 12, 1, 22) with
        {
            TargetHandle = target.TargetHandle
        };
        await store.PersistAsync(
            new[] { row },
            CancellationToken.None,
            new Dictionary<ulong, HostManagerReportTargetDescriptor>
            {
                [target.TargetHandle] = target
            });
        await SetStoredTargetKindsAsync(database, softwareKind, displayKind);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => store.LoadTargetsAsync(CancellationToken.None));
    }

    private static async Task SetStoredTargetKindsAsync(
        ResourceManagerDatabase database,
        string softwareKind,
        string displayKind)
    {
        await using var connection = await database.OpenConnectionAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE host_manager_report_target_identities
            SET software_kind = $softwareKind, display_kind = $displayKind;
            """;
        command.Parameters.AddWithValue("$softwareKind", softwareKind);
        command.Parameters.AddWithValue("$displayKind", displayKind);
        await command.ExecuteNonQueryAsync(CancellationToken.None);
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
