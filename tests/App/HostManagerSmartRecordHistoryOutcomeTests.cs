using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Application.Optimization;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Infrastructure.Optimization;
using ResourceManager.App.Infrastructure.GpuPlacement.External;

namespace Resource_Manager_APP.Tests;

public sealed class HostManagerSmartRecordHistoryOutcomeTests
{
    [Theory]
    [InlineData(HostManagerPlacementSettlementKind.Restored, "restored")]
    [InlineData(HostManagerPlacementSettlementKind.AlreadyRestored, "restored")]
    [InlineData(HostManagerPlacementSettlementKind.OwnershipLost, "ownershipLost")]
    [InlineData(HostManagerPlacementSettlementKind.RetryableFailure, "failed")]
    [InlineData(HostManagerPlacementSettlementKind.InvalidReceipt, "failed")]
    [InlineData(HostManagerPlacementSettlementKind.RetainedActionFact, "unknown")]
    public void ExistingSettlementKindsHaveCanonicalResults(HostManagerPlacementSettlementKind kind, string expected)
    {
        var settlement = new HostManagerPlacementRecordSettlement(new("cpu_affinity", "record"), kind, 0, "private diagnostic detail");
        Assert.Equal(expected, HostManagerSmartCoordinator.HistoryResultFromSettlement(settlement));
    }

    [Theory]
    [InlineData(RecoveryReadAbsenceReason.TargetExited, "targetExited")]
    [InlineData(RecoveryReadAbsenceReason.IdentityChanged, "ownershipLost")]
    [InlineData(RecoveryReadAbsenceReason.Unspecified, "unknown")]
    public void TypedAbsenceKeepsExitAndReusedIdentityDistinct(RecoveryReadAbsenceReason reason, string expected)
        => Assert.Equal(expected, HostManagerSmartCoordinator.HistoryResultFromAbsence(reason));

    [Theory]
    [InlineData((int)HostManagerSmartCoordinator.HostManagerPlacementValueRelation.Foreign, RecoveryReadAbsenceReason.Unspecified, "ownershipLost")]
    [InlineData((int)HostManagerSmartCoordinator.HostManagerPlacementValueRelation.Unavailable, RecoveryReadAbsenceReason.Unspecified, "failed")]
    [InlineData((int)HostManagerSmartCoordinator.HostManagerPlacementValueRelation.NotFound, RecoveryReadAbsenceReason.TargetExited, "targetExited")]
    [InlineData((int)HostManagerSmartCoordinator.HostManagerPlacementValueRelation.NotFound, RecoveryReadAbsenceReason.IdentityChanged, "ownershipLost")]
    [InlineData((int)HostManagerSmartCoordinator.HostManagerPlacementValueRelation.NotFound, RecoveryReadAbsenceReason.Unspecified, "unknown")]
    public void PreparedIntentRejectedByBaselineUsesItsActualReason(
        int relation, RecoveryReadAbsenceReason reason, string expected)
        => Assert.Equal(expected, HostManagerSmartCoordinator.HistoryResultFromRejectedBaseline((HostManagerSmartCoordinator.HostManagerPlacementValueRelation)relation, reason));

    [Theory]
    [InlineData((int)HostManagerSmartCoordinator.HostManagerPlacementValueRelation.Previous, RecoveryReadAbsenceReason.Unspecified, "failed")]
    [InlineData((int)HostManagerSmartCoordinator.HostManagerPlacementValueRelation.Foreign, RecoveryReadAbsenceReason.Unspecified, "ownershipLost")]
    [InlineData((int)HostManagerSmartCoordinator.HostManagerPlacementValueRelation.NotFound, RecoveryReadAbsenceReason.TargetExited, "targetExited")]
    public void WriteReadBackRetirementUsesItsActualReason(
        int relation, RecoveryReadAbsenceReason reason, string expected)
        => Assert.Equal(expected, HostManagerSmartCoordinator.HistoryResultFromWriteReadBack((HostManagerSmartCoordinator.HostManagerPlacementValueRelation)relation, reason));

    [Theory]
    [InlineData(RunningGpuPlacementActionStatuses.Skipped, "failed")]
    [InlineData(RunningGpuPlacementActionStatuses.NotApplied, "failed")]
    [InlineData(RunningGpuPlacementActionStatuses.Unresolved, "unknown")]
    public void ExternalAttemptWithoutConfirmedTransferDoesNotClaimRestoration(string status, string expected)
        => Assert.Equal(expected, HostManagerSmartCoordinator.HistoryResultFromExternalAction(new([], "private detail", status)));

    [Fact]
    public void ReleasedControllerWithUnconfirmedResidentTransferIsUnknown()
    {
        var result = new RunningGpuPlacementActionResult([
            new("record", "external", new Dictionary<string, string>
            {
                ["residentConfirmation"] = "unconfirmed", ["controllerSucceeded"] = bool.TrueString,
                ["controllerExited"] = bool.TrueString, ["cleanupPassed"] = bool.TrueString
            })], "private detail", RunningGpuPlacementActionStatuses.NotApplied);
        Assert.Equal("unknown", HostManagerSmartCoordinator.HistoryResultFromExternalAction(result));
    }

    [Fact]
    public void ExternalIdentityOrControlChangeKeepsItsProducerReason()
    {
        var result = new RunningGpuPlacementActionResult([], "private detail", RunningGpuPlacementActionStatuses.Skipped)
            { HistoryRestoreResult = HostManagerSmartRecordRestoreResults.OwnershipLost };
        Assert.Equal("ownershipLost", HostManagerSmartCoordinator.HistoryResultFromExternalAction(result));
        var json = System.Text.Json.JsonSerializer.Serialize(result, System.Text.Json.JsonSerializerOptions.Web);
        Assert.DoesNotContain("historyRestoreResult", json);
    }

    [Theory]
    [InlineData("ownershipLost")]
    [InlineData("failed")]
    [InlineData("unknown")]
    public void ExternalRemovalReasonSurvivesSummaryCommitAndRestart(string outcome)
    {
        var record = ExternalGpuRuntimePlacementRecord.Create(123, 456, new Dictionary<string, string>
        {
            ["processId"] = "123", ["processStartKey"] = "456", ["assignedPositionId"] = "position"
        });
        var placement = new HostManagerAppliedPlacementReceipt("target", null, "gpu", [record], DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        var before = HostManagerPlacementCoordinatorProjection.CreatePayloadDigest("applied", placement, record);
        var completed = HostManagerSmartCoordinator.WithRuntimeActionResult(record,
            new([], "private detail", RunningGpuPlacementActionStatuses.Skipped) { HistoryRestoreResult = outcome });
        Assert.Equal(before, HostManagerPlacementCoordinatorProjection.CreatePayloadDigest("applied", placement, completed));
        var json = System.Text.Json.JsonSerializer.Serialize(completed, System.Text.Json.JsonSerializerOptions.Web);
        var restored = System.Text.Json.JsonSerializer.Deserialize<HostManagerAppliedRecord>(json, System.Text.Json.JsonSerializerOptions.Web)!;
        var settlement = HostManagerPlacementRollbackExecutor.SettleRuntimeRebuildRecord(restored);
        Assert.True(settlement.CanRemoveReceipt);
        Assert.Equal(outcome, HostManagerSmartCoordinator.HistoryResultFromSettlement(settlement));
    }

    [Fact]
    public void ExternalIntentWithoutSurvivingSummaryIsUnknown()
    {
        var record = ExternalGpuRuntimePlacementRecord.Create(123, 456, new Dictionary<string, string>
        {
            ["processId"] = "123", ["processStartKey"] = "456", ["assignedPositionId"] = "position"
        });
        var settlement = HostManagerPlacementRollbackExecutor.SettleRuntimeRebuildRecord(record);
        Assert.True(settlement.CanRemoveReceipt);
        Assert.Equal("unknown", HostManagerSmartCoordinator.HistoryResultFromSettlement(settlement));
    }

    [Fact]
    public void InvalidExternalReceiptStillRequiresLedgerRecovery()
    {
        var record = ExternalGpuRuntimePlacementRecord.Create(123, 456, new Dictionary<string, string>
            { ["processId"] = "123", ["processStartKey"] = "456" });
        var settlement = HostManagerPlacementRollbackExecutor.SettleRuntimeRebuildRecord(record);
        Assert.Equal(HostManagerPlacementSettlementKind.InvalidReceipt, settlement.Kind);
        Assert.False(settlement.CanRemoveReceipt);
    }

    [Fact]
    public void PartialAndBulkRetirementKeepIndividualSiblingReasons()
    {
        var first = new HostManagerAppliedRecord("cpu_affinity", "first") { Sequence = 1, RecordedAt = DateTimeOffset.UnixEpoch };
        var second = new HostManagerAppliedRecord("process_priority", "second") { Sequence = 2, RecordedAt = DateTimeOffset.UnixEpoch };
        var placement = new HostManagerAppliedPlacementReceipt("target", null, "cpu", [first, second], DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        var state = HostManagerRollbackStateDocument.Empty with { AppliedPlacements = [placement] };
        var reasons = new Dictionary<HostManagerPlacementNativeIdentity, string>
        {
            [HostManagerPlacementCoordinatorProjection.CreateIdentity(placement, first)] = HostManagerSmartCoordinator.HistoryResultFromAbsence(RecoveryReadAbsenceReason.TargetExited)
        };
        var partial = HostManagerSmartCoordinator.RemovePlacementRecords(state, reasons);
        Assert.Equal(second, Assert.Single(Assert.Single(partial.AppliedPlacements).Records));
        Assert.Equal("targetExited", Assert.Single(partial.RestoreResults!).Value);
        Assert.Equal("targetExited", partial.RecordRestoreResults![HostManagerRecordHistoryKey.Create(placement, first)]);
        reasons.Add(HostManagerPlacementCoordinatorProjection.CreateIdentity(placement, second),
            HostManagerSmartCoordinator.HistoryResultFromAbsence(RecoveryReadAbsenceReason.IdentityChanged));
        var complete = HostManagerSmartCoordinator.RemovePlacementRecords(state, reasons);
        Assert.Empty(complete.AppliedPlacements);
        Assert.Equal("targetExited", complete.RestoreResults![1]);
        Assert.Equal("ownershipLost", complete.RestoreResults[2]);
    }
}
