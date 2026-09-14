using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostManagerSmartCoordinatorScoreOnlyCompositionTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StatusCountsNativeOwnedAdapterWithoutLegacyPlacementsOrNewActions(
        bool hasCurrentProcess)
    {
        const string softwareId = "software:status-owned-adapter";
        var facts = hasCurrentProcess
            ? CreateCompleteProcessFacts(
                4_242, 132_537_600_000_000_000, softwareId, 20, 5, 5)
            : CreateProcessFacts();
        await using var fixture = await ScoreOnlyCoordinatorFixture.CreateAsync(
            warm: true,
            scoreOnlyEnabled: false,
            processFactsSnapshot: facts,
            policyExecutionEnabled: false,
            automaticMemoryCleanupEnabled: false);
        await fixture.SeedAdapterOwnershipAsync(softwareId, 501);

        await fixture.Coordinator.RunOnceAsync(CancellationToken.None);

        var owned = Assert.Single(fixture.Workspace.CurrentSnapshotRows.ToArray(),
            row => row.RowKind == NativeSmartCoordinatorSnapshotRowKind.Software &&
                row.Flags.HasFlag(NativeSmartCoordinatorSnapshotRowFlags.CpuOwned));
        Assert.True(owned.Flags.HasFlag(NativeSmartCoordinatorSnapshotRowFlags.GpuOwned));
        Assert.Empty(fixture.StateStore.Current.AppliedPlacements);
        Assert.Equal(0U, fixture.Workspace.Snapshot.ActionCount);

        var first = await fixture.Coordinator.GetStatusAsync(CancellationToken.None);
        var second = await fixture.Coordinator.GetStatusAsync(CancellationToken.None);

        Assert.Equal(1, first.AppliedTargetCount);
        Assert.Equal(1, second.AppliedTargetCount);
        Assert.Equal(0, fixture.ProcessPolicyWriter.TotalCalls);

        var invalidRows = fixture.Workspace.CurrentSnapshotRows.ToArray();
        var softwareIndex = Array.FindIndex(invalidRows,
            row => row.RowKind == NativeSmartCoordinatorSnapshotRowKind.Software);
        invalidRows[softwareIndex].ValidMask |= NativeSmartCoordinatorInputValidity.ProcessIdentity;
        Assert.Throws<InvalidDataException>(() => HostManagerSmartCoordinator.ValidateNativeSnapshotRows(
            invalidRows, fixture.Workspace.Snapshot));
    }

    [Theory]
    [InlineData((uint)NativeSmartCoordinatorSnapshotRowFlags.CpuOwned, 1)]
    [InlineData((uint)NativeSmartCoordinatorSnapshotRowFlags.GpuOwned, 1)]
    [InlineData((uint)(NativeSmartCoordinatorSnapshotRowFlags.CpuOwned |
        NativeSmartCoordinatorSnapshotRowFlags.GpuOwned), 1)]
    [InlineData((uint)NativeSmartCoordinatorSnapshotRowFlags.CpuInflight, 0)]
    [InlineData((uint)NativeSmartCoordinatorSnapshotRowFlags.GpuInflight, 0)]
    [InlineData((uint)NativeSmartCoordinatorSnapshotRowFlags.None, 0)]
    public void StatusCountsOnlyCurrentAdapterOwnership(
        uint flags,
        int expected)
    {
        var row = new NativeSmartCoordinatorSnapshotRow
        {
            RowKind = NativeSmartCoordinatorSnapshotRowKind.Software,
            TargetKey = 10_350_088_602_121_727_311,
            Flags = (NativeSmartCoordinatorSnapshotRowFlags)flags,
            DesiredCpuGrade = NativeSmartCoordinatorAdapterGrade.Optimize,
            CpuPendingCount = 1,
            FailureCount = 1
        };

        Assert.Equal(expected, HostManagerSmartCoordinator.CountAppliedTargets([], [row]));
    }

    [Fact]
    public void StatusMergesLegacyAndNativeTargetsWithoutMergingDifferentProcesses()
    {
        var first = CreateLegacyPlacementReceipt();
        var duplicate = first with { ResourceKind = "Gpu" };
        var separate = first with { TargetId = "process:legacy:2" };
        var empty = first with { TargetId = "process:empty", Records = [] };
        var rows = new[]
        {
            new NativeSmartCoordinatorSnapshotRow
            {
                RowKind = NativeSmartCoordinatorSnapshotRowKind.Process,
                TargetKey = NativeStableIdentity.CreateCaseInsensitiveKey(first.TargetId),
                Flags = NativeSmartCoordinatorSnapshotRowFlags.ProcessOwned
            },
            new NativeSmartCoordinatorSnapshotRow
            {
                RowKind = NativeSmartCoordinatorSnapshotRowKind.Process,
                TargetKey = NativeStableIdentity.CreateCaseInsensitiveKey("process:native:3"),
                Flags = NativeSmartCoordinatorSnapshotRowFlags.ProcessOwned
            },
            new NativeSmartCoordinatorSnapshotRow
            {
                RowKind = NativeSmartCoordinatorSnapshotRowKind.Software,
                TargetKey = NativeStableIdentity.CreateCaseInsensitiveKey(first.SoftwareId!),
                Flags = NativeSmartCoordinatorSnapshotRowFlags.CpuOwned |
                    NativeSmartCoordinatorSnapshotRowFlags.GpuOwned
            }
        };

        Assert.Equal(4, HostManagerSmartCoordinator.CountAppliedTargets(
            [first, duplicate, separate, empty], rows));
        Assert.Equal(2, HostManagerSmartCoordinator.CountAppliedTargets(
            [first, duplicate, separate, empty], []));
        Assert.Equal(0, HostManagerSmartCoordinator.CountAppliedTargets([], []));
    }

    [Theory]
    [InlineData((uint)NativeSmartCoordinatorSnapshotRowFlags.ProcessOwned, 1)]
    [InlineData((uint)NativeSmartCoordinatorSnapshotRowFlags.ProcessInflight, 0)]
    [InlineData((uint)NativeSmartCoordinatorSnapshotRowFlags.None, 0)]
    public void StatusRestoredProcessIsNotCounted(
        uint flags,
        int expected)
    {
        var row = new NativeSmartCoordinatorSnapshotRow
        {
            RowKind = NativeSmartCoordinatorSnapshotRowKind.Process,
            TargetKey = 91,
            Flags = (NativeSmartCoordinatorSnapshotRowFlags)flags,
            AppliedProcessGrade = NativeSmartCoordinatorProcessGrade.Normal
        };
        Assert.Equal(expected, HostManagerSmartCoordinator.CountAppliedTargets([], [row]));
    }
}
