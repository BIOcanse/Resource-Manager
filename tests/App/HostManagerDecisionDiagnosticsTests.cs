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
        Assert.Equal("game", actual.SoftwareKind);
        Assert.Equal("foreground_focused", actual.RuntimeState);
        Assert.Equal(64, actual.CpuScore);
        Assert.Equal(80, actual.CpuOccupancyPercent);
        Assert.Equal("a1", actual.DesiredProcessGrade);
        Assert.Equal("normal", actual.AppliedProcessGrade);
        Assert.Equal(["event_boost_active"], projected.Flags);
        Assert.Equal("2", projected.FlagsValue);
        Assert.Equal(["awaiting_stability"], projected.ReasonMask);
        Assert.Equal(["process_inflight"], actual.Flags);
        Assert.Equal(["process_identity", "software_identity"], actual.ValidMask);
        Assert.Equal(["awaiting_stability"], actual.ReasonMask);
    }

    [Fact]
    public void FlagArraysPreserveNamedBitsAndExactUnknownBitsOnTheWire()
    {
        const ulong unknownReason = 1UL << 63;
        var projected = HostManagerSmartCoordinator.ProjectNativeDiagnostics(
            new NativeSmartCoordinatorSnapshot
            {
                SnapshotRowCount = 1,
                Flags = NativeSmartCoordinatorSnapshotFlags.ScoreOnly | (NativeSmartCoordinatorSnapshotFlags)(1U << 31),
                ReasonMask = NativeSmartCoordinatorReason.AwaitingStability | (NativeSmartCoordinatorReason)unknownReason
            },
            [new NativeSmartCoordinatorSnapshotRow
            {
                RowKind = NativeSmartCoordinatorSnapshotRowKind.Process,
                Flags = NativeSmartCoordinatorSnapshotRowFlags.ProcessOwned | NativeSmartCoordinatorSnapshotRowFlags.CpuInflight,
                ValidMask = NativeSmartCoordinatorInputValidity.ProcessIdentity | (NativeSmartCoordinatorInputValidity)(1UL << 63)
            }]);
        var wire = System.Text.Json.JsonSerializer.SerializeToElement(projected,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

        Assert.Equal(["score_only"], wire.GetProperty("flags").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal("2147483652", wire.GetProperty("flagsValue").GetString());
        Assert.Equal(["awaiting_stability"], wire.GetProperty("reasonMask").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal("9223372036855037952", wire.GetProperty("reasonMaskValue").GetString());
        var row = Assert.Single(wire.GetProperty("rows").EnumerateArray());
        Assert.Equal(["process_owned", "cpu_inflight"], row.GetProperty("flags").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(["process_identity"], row.GetProperty("validMask").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal("9223372036854775809", row.GetProperty("validMaskValue").GetString());
        Assert.Equal(0, row.GetProperty("reasonMask").GetArrayLength());
        Assert.Equal("0", row.GetProperty("reasonMaskValue").GetString());
        var empty = HostManagerSmartCoordinator.ProjectNativeDiagnostics(new NativeSmartCoordinatorSnapshot(), []);
        Assert.Empty(empty.Flags);
        Assert.Empty(empty.ReasonMask);
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

    [Theory]
    [InlineData((int)NativeSmartCoordinatorRuntimeState.Unknown, "unknown")]
    [InlineData((int)NativeSmartCoordinatorRuntimeState.ForegroundFocused, "foreground_focused")]
    [InlineData((int)NativeSmartCoordinatorRuntimeState.ForegroundUnfocused, "foreground_unfocused")]
    [InlineData((int)NativeSmartCoordinatorRuntimeState.BackgroundWindow, "background_window")]
    [InlineData((int)NativeSmartCoordinatorRuntimeState.TrayOnly, "tray_only")]
    [InlineData((int)NativeSmartCoordinatorRuntimeState.BackgroundProcess, "background_process")]
    [InlineData((int)NativeSmartCoordinatorRuntimeState.NotRunning, "not_running")]
    public void NativeDiagnosticsUseCanonicalRuntimeStateTokens(
        int state, string expected)
    {
        var projected = HostManagerSmartCoordinator.ProjectNativeDiagnostics(
            new NativeSmartCoordinatorSnapshot { SnapshotRowCount = 1 },
            [new NativeSmartCoordinatorSnapshotRow { RuntimeState = (NativeSmartCoordinatorRuntimeState)state }]);

        Assert.Equal(expected, Assert.Single(projected.Rows).RuntimeState);
    }
}
