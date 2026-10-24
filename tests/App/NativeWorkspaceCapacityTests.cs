using System.Runtime.InteropServices;
using ResourceManager.App.Infrastructure.Adaptation;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization;

namespace Resource_Manager_APP.Tests;

public sealed class NativeWorkspaceCapacityTests
{
    [Fact]
    public void SeparateDiagnosticAbiUsesExactNativeLayouts()
    {
        Assert.Equal(0x0002_0000U, NativeWorkspaceCapacityAbi.GetAbiVersion());
        Assert.Equal(32, Marshal.SizeOf<NativeWorkspaceGrowthEvent>());
        Assert.Equal(264, Marshal.SizeOf<NativeWorkspaceGrowthLog>());
        Assert.Equal(296, Marshal.SizeOf<NativeOperationCoordinatorWorkspaceCapacity>());
        Assert.Equal(288, Marshal.SizeOf<NativeAppliedOwnershipWorkspaceCapacity>());
        Assert.Equal(296, Marshal.SizeOf<NativeTransactionJournalWorkspaceCapacity>());
        Assert.Equal(344, Marshal.SizeOf<NativeSmartCoordinatorWorkspaceCapacity>());
        Assert.Equal(304, Marshal.SizeOf<NativeReportCoordinatorWorkspaceCapacity>());
        Assert.Equal(32, Marshal.OffsetOf<NativeSmartCoordinatorWorkspaceCapacity>(
            nameof(NativeSmartCoordinatorWorkspaceCapacity.ProcessIndexCapacity)).ToInt32());
        Assert.Equal(72, Marshal.OffsetOf<NativeSmartCoordinatorWorkspaceCapacity>(
            nameof(NativeSmartCoordinatorWorkspaceCapacity.ResidentBytes)).ToInt32());
        Assert.Equal(16, Marshal.OffsetOf<NativeAppliedOwnershipWorkspaceCapacity>(
            nameof(NativeAppliedOwnershipWorkspaceCapacity.ResidentBytes)).ToInt32());
    }

    [Fact]
    public void AllFiveNativeOwnersAreVisibleBeforeScratchUseAndDisposeTheirDiagnostics()
    {
        using var ledger = new ResourceManagerLedgerOwner();
        using var ownership = new NativeAppliedOwnershipSession(OwnershipConfiguration());
        using var journal = new NativeTransactionJournalSession(JournalConfiguration());
        using var ownershipWorkspace = new NativeAppliedOwnershipWorkspace(ownership.Capacity, ledger.CreateScratch, ownership);
        using var journalWorkspace = new NativeTransactionJournalWorkspace(journal.Capacity, ledger.CreateScratch, journal);
        using var operations = new NativeOperationCoordinatorWorkspace(NativeOperationScratchTests.Plan(),
            new(1, 2), new(3, 4), ledger.CreateScratch);
        var smartPlan = HostManagerTestPlanFactory.SmartCoordinator;
        var smartConfiguration = NativeSmartCoordinatorConfigurationWriter.Create(
            smartPlan.Build, smartPlan.Recreate, smartPlan.HotPublish, 1);
        using var smart = new NativeSmartCoordinatorWorkspace(smartConfiguration, ledger.CreateScratch);
        using var reports = new NativeReportCoordinatorWorkspace(
            HostManagerTestPlanFactory.CreatePlan().ReportCoordinator, 101, 102, ledger.CreateScratch);

        var native = NativeRows(ledger);
        Assert.Equal(5, native.Length);
        Assert.Equal(new[] { "journal-native", "operation-output-native", "ownership-native", "report-coordinator-native", "smart-coordinator-native" },
            native.Select(row => row.WorkArea).Order(StringComparer.Ordinal).ToArray());
        Assert.All(native, row =>
        {
            Assert.False(row.InLedger);
            Assert.True(row.Bytes > 0);
            Assert.NotEmpty(row.Rows);
        });
        Assert.Equal(0UL, ledger.ReadTable().RegisteredBytes);
        Assert.Equal(16U, ownership.WorkspaceCapacity.RecordCapacity);
        Assert.Equal(16U, journal.WorkspaceCapacity.RecordCapacity);
        Assert.Equal(ownership.WorkspaceCapacity.ResidentBytes,
            Assert.Single(native, row => row.WorkArea == "ownership-native").Bytes);
        Assert.Equal(reports.Session.WorkspaceCapacity.ResidentByteCount,
            Assert.Single(native, row => row.WorkArea == "report-coordinator-native").Bytes);
        Assert.Equal(native.Select(row => row.Uid).Order().ToArray(), NativeRows(ledger).Select(row => row.Uid).Order().ToArray());

        reports.Dispose();
        smart.Dispose();
        operations.Dispose();
        journalWorkspace.Dispose();
        ownershipWorkspace.Dispose();
        Assert.Empty(ledger.ReadWorkspaceCapacities());
        // External native authority is still live until its separate owner closes.
        Assert.Equal(16U, ownership.WorkspaceCapacity.RecordCapacity);
        Assert.Equal(16U, journal.WorkspaceCapacity.RecordCapacity);
    }

    [Fact]
    public void NativeGrowthUpdatesTheSameDiagnosticIdentityWithoutAllocatingLedgerScratch()
    {
        using var ledger = new ResourceManagerLedgerOwner();
        using var journal = new NativeTransactionJournalSession(JournalConfiguration());
        using var workspace = new NativeTransactionJournalWorkspace(journal.Capacity, ledger.CreateScratch, journal);
        var before = Assert.Single(NativeRows(ledger));
        for (uint id = 1; id <= 33; id++)
        {
            var input = new NativeTransactionJournalPrepareInput
            {
                AbiVersion = NativeTransactionJournalAbi.Version,
                StructSize = NativeTransactionJournalSession.SizeOf<NativeTransactionJournalPrepareInput>(),
                ExpectedJournalRevision = id,
                Identity = new()
                {
                    ConfigurationGeneration = 7, PlanEpoch = 19, ActionId = id,
                    HostSessionIncarnation = 23, TargetId = id, SoftwareId = id,
                    ProcessStartKey = id, ProcessId = id
                },
                Scope = (uint)NativeTransactionJournalScope.Process,
                Disposition = (uint)NativeTransactionJournalDisposition.Apply,
                DomainMask = (uint)NativeTransactionJournalDomain.Process,
                GradeValidMask = (uint)NativeTransactionJournalGradeValidity.Process,
                ProcessFromGrade = (int)NativeTransactionJournalProcessGrade.Normal,
                ProcessToGrade = (int)NativeTransactionJournalProcessGrade.Level1,
                StableSystemStatus = 100,
                PayloadKind = (uint)NativeTransactionJournalPayloadKind.Durable,
                PayloadSlot = id, PayloadGeneration = 3, PayloadLength = 512,
                PayloadDigestLow = 0x1234, PayloadDigestHigh = 0x5678,
                PayloadProvenanceDigestLow = 0x9abc, PayloadProvenanceDigestHigh = 0xdef0,
                NowUtcMilliseconds = 100 + id,
                MaximumRecoveryAttempts = 3, RecoveryDeadlineUtcMilliseconds = 10000 + id
            };
            Assert.Equal(NativeTransactionJournalStatus.Ok, journal.Prepare(input));
        }
        var after = Assert.Single(NativeRows(ledger));
        Assert.Equal(before.Uid, after.Uid);
        Assert.False(after.InLedger);
        Assert.True(after.Bytes > before.Bytes);
        Assert.Equal(64U, journal.WorkspaceCapacity.RecordCapacity);
        Assert.Equal(journal.WorkspaceCapacity.ResidentBytes, after.Bytes);
        Assert.Contains("records=64", after.Rows);
        Assert.Equal(0UL, ledger.ReadTable().RegisteredBytes);
        workspace.Dispose();
        Assert.Empty(ledger.ReadWorkspaceCapacities());
    }

    [Fact]
    public async Task DisposalWaitsForCapacityReadBeforeTheNativeOwnerCanBeDestroyed()
    {
        using var ledger = new ResourceManagerLedgerOwner();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var registration = ((IWorkspaceCapacityRegistry)ledger).RegisterNativeWorkspace("native-test", () =>
        {
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            return new("records=1", 320);
        });
        var reading = Task.Run(ledger.ReadWorkspaceCapacities);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        var closing = Task.Run(registration.Dispose);
        release.Set();
        await Task.WhenAll(reading, closing);
        Assert.Empty(ledger.ReadWorkspaceCapacities());
        registration.Dispose();
    }

    private static ResourceManagerWorkspaceCapacity[] NativeRows(ResourceManagerLedgerOwner ledger)
        => ledger.ReadWorkspaceCapacities().Where(row => row.WorkArea.EndsWith("-native", StringComparison.Ordinal)).ToArray();

    private static NativeAppliedOwnershipCreateConfiguration OwnershipConfiguration() => new()
    {
        AbiVersion = NativeAppliedOwnershipAbi.Version,
        StructSize = NativeAppliedOwnershipSession.SizeOf<NativeAppliedOwnershipCreateConfiguration>(),
        LedgerInstanceLow = 1, LedgerInstanceHigh = 2,
        RecordCapacity = 8192, PrimaryIndexCapacity = 16384, PayloadIndexCapacity = 16384,
        MaximumResidentBytes = 16 * 1024 * 1024,
        MaximumImageBytes = 128 + 8192 * 320
    };

    private static NativeTransactionJournalCreateConfiguration JournalConfiguration() => new()
    {
        AbiVersion = NativeTransactionJournalAbi.Version,
        StructSize = NativeTransactionJournalSession.SizeOf<NativeTransactionJournalCreateConfiguration>(),
        JournalInstanceLow = 1, JournalInstanceHigh = 2, RecordCapacity = 8192,
        MaximumResidentBytes = 16 * 1024 * 1024
    };
}
