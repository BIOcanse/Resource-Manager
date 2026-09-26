using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization;

namespace Resource_Manager_APP.Tests;

public sealed class HostManagerDecisionDiagnosticsTests
{
    [Fact]
    public void NativeProjectionPreservesExactIdentitiesAndDecisionFields()
    {
        const ulong exactIdentity = 9_007_199_254_740_993;
        var snapshot = new NativeSmartCoordinatorSnapshot
        {
            ConfigurationGeneration = exactIdentity,
            CycleSequence = 17,
            PlanEpoch = 19,
            StateRevision = 23,
            ObservedAtMilliseconds = 101,
            NextWakeAtMilliseconds = 201,
            WakeAfterMilliseconds = 100,
            ActionCount = 1,
            ProcessCount = 1,
            SoftwareCount = 0,
            SnapshotRowCount = 1,
            Flags = NativeSmartCoordinatorSnapshotFlags.EventBoostActive,
            ReasonMask = NativeSmartCoordinatorReason.AwaitingStability
        };
        var row = new NativeSmartCoordinatorSnapshotRow
        {
            RowKind = NativeSmartCoordinatorSnapshotRowKind.Process,
            TargetKey = exactIdentity,
            SoftwareKey = exactIdentity + 1,
            ProcessId = 40544,
            ProcessStartKey = exactIdentity + 2,
            SourceIndex = 7,
            SoftwareKind = NativeSmartCoordinatorSoftwareKind.Game,
            RuntimeState = NativeSmartCoordinatorRuntimeState.ForegroundFocused,
            ProtectionLevel = 1,
            BaseScore = 80,
            CpuScore = 64,
            CpuOccupancyPercent = 80,
            AdapterCpuScore = 64,
            AdapterGpuScore = 72,
            DesiredProcessGrade = NativeSmartCoordinatorProcessGrade.A1,
            AppliedProcessGrade = NativeSmartCoordinatorProcessGrade.Normal,
            PendingProcessGrade = NativeSmartCoordinatorProcessGrade.A1,
            DesiredCpuGrade = NativeSmartCoordinatorAdapterGrade.Extreme,
            AppliedCpuGrade = NativeSmartCoordinatorAdapterGrade.Normal,
            PendingCpuGrade = NativeSmartCoordinatorAdapterGrade.Extreme,
            DesiredGpuGrade = NativeSmartCoordinatorAdapterGrade.Extreme,
            AppliedGpuGrade = NativeSmartCoordinatorAdapterGrade.Normal,
            PendingGpuGrade = NativeSmartCoordinatorAdapterGrade.Extreme,
            ProcessPendingCount = 1,
            CpuPendingCount = 1,
            GpuPendingCount = 1,
            CpuCapabilityMask = 3,
            GpuCapabilityMask = 5,
            Flags = NativeSmartCoordinatorSnapshotRowFlags.ProcessInflight,
            ValidMask = NativeSmartCoordinatorInputValidity.ProcessIdentity |
                NativeSmartCoordinatorInputValidity.SoftwareIdentity,
            ReasonMask = NativeSmartCoordinatorReason.AwaitingStability,
            LastSeenCycle = exactIdentity + 3
        };

        var projected = HostManagerSmartCoordinator.ProjectNativeDiagnostics(
            snapshot,
            [row]);

        Assert.Equal("9007199254740993", projected.ConfigurationGeneration);
        var actual = Assert.Single(projected.Rows);
        Assert.Equal("9007199254740993", actual.TargetKey);
        Assert.Equal("9007199254740994", actual.SoftwareKey);
        Assert.Equal("9007199254740995", actual.ProcessStartKey);
        Assert.Equal("9007199254740996", actual.LastSeenCycle);
        Assert.Equal(40544U, actual.ProcessId);
        Assert.Equal("Game", actual.SoftwareKind);
        Assert.Equal("ForegroundFocused", actual.RuntimeState);
        Assert.Equal(64, actual.CpuScore);
        Assert.Equal(80, actual.CpuOccupancyPercent);
        Assert.Equal("A1", actual.DesiredProcessGrade);
        Assert.Equal("Normal", actual.AppliedProcessGrade);
        Assert.Equal("AwaitingStability", actual.ReasonMask);
    }

    [Fact]
    public void NativeProjectionRejectsMismatchedRowCount()
    {
        var snapshot = new NativeSmartCoordinatorSnapshot
        {
            SnapshotRowCount = 1
        };

        Assert.Throws<InvalidDataException>(() =>
            HostManagerSmartCoordinator.ProjectNativeDiagnostics(snapshot, []));
    }
}
