using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.Optimization;
using ResourceManager.App.Infrastructure.Optimization.Transactions;

namespace Resource_Manager_APP.Tests;

public sealed class HostManagerTransactionGradeMigrationTests
{
    [Fact]
    public void TerminalFailureAfterPreviousRestore_ReportsOwnershipLostWithoutActualGrade()
    {
        AssertTerminalFailure(
            NativeSmartCoordinatorActionScope.ProcessPolicy,
            HostManagerSmartCoordinator.TransactionEffectStatus.FailedUnchanged);
        AssertTerminalFailure(
            NativeSmartCoordinatorActionScope.ProcessPolicy,
            HostManagerSmartCoordinator.TransactionEffectStatus.Rejected);
    }

    [Theory]
    [InlineData(
        (int)HostManagerProcessPolicyCaptureStatus.Captured,
        (int)HostManagerSmartCoordinator.TransactionEffectStatus.Succeeded)]
    [InlineData(
        (int)HostManagerProcessPolicyCaptureStatus.InvalidRequest,
        (int)HostManagerSmartCoordinator.TransactionEffectStatus.Rejected)]
    [InlineData(
        (int)HostManagerProcessPolicyCaptureStatus.Unavailable,
        (int)HostManagerSmartCoordinator.TransactionEffectStatus.Skipped)]
    public void ProcessCaptureStatus_PreservesPreEffectFailureSemantics(
        int status,
        int expected)
    {
        Assert.Equal(
            (HostManagerSmartCoordinator.TransactionEffectStatus)expected,
            HostManagerSmartCoordinator.ResolveProcessCaptureEffectStatus(
                (HostManagerProcessPolicyCaptureStatus)status));
    }

    [Fact]
    public void SkippedCaptureFeedback_DoesNotInventActualGrades()
    {
        var action = CreateAction(NativeSmartCoordinatorActionScope.ProcessPolicy);

        var feedback = HostManagerSmartCoordinator.CreateUnchangedFeedback(
            in action,
            HostManagerSmartCoordinator.TransactionEffectStatus.Skipped,
            systemError: 0,
            completedAt: DateTimeOffset.UnixEpoch.AddSeconds(1));

        Assert.Equal(NativeSmartCoordinatorFeedbackStatus.Skipped, feedback.Status);
        Assert.Equal(NativeSmartCoordinatorFeedbackValidity.CompletedAt, feedback.ValidMask);
        Assert.Equal(NativeSmartCoordinatorFeedbackFlags.None, feedback.Flags);
        HostManagerSmartCoordinator.ValidateNativeFeedback(
            feedback,
            action,
            DateTimeOffset.UnixEpoch.ToUnixTimeMilliseconds());
    }

    [Fact]
    public void ProcessExit_RemainsExplicitUntilJournalRecoverySettlement()
    {
        var apply = new HostManagerProcessPolicyApplyResult(
            HostManagerProcessPolicyApplyStatus.ProcessExited,
            HostManagerProcessPolicyTransactionFields.All,
            HostManagerProcessPolicyTransactionFields.None,
            7,
            6);
        var restore = new HostManagerProcessPolicyRestoreResult(
            HostManagerProcessPolicyRestoreStatus.ProcessExited,
            HostManagerProcessPolicyTransactionFields.All,
            HostManagerProcessPolicyTransactionFields.None,
            7,
            6);

        Assert.Equal(
            HostManagerSmartCoordinator.TransactionEffectStatus.ProcessExited,
            HostManagerSmartCoordinator.MapProcessApplyResult(apply).Status);
        Assert.Equal(
            HostManagerSmartCoordinator.TransactionEffectStatus.ProcessExited,
            HostManagerSmartCoordinator.MapProcessRestoreResult(restore).Status);

        var action = CreateAction(NativeSmartCoordinatorActionScope.ProcessPolicy);
        var feedback = HostManagerSmartCoordinator.CreateProcessExitedFeedback(
            in action,
            systemError: 0,
            completedAt: DateTimeOffset.UnixEpoch.AddSeconds(1));
        Assert.Equal(NativeSmartCoordinatorFeedbackStatus.OwnershipLost, feedback.Status);
        Assert.Equal(NativeSmartCoordinatorFeedbackValidity.CompletedAt, feedback.ValidMask);
        Assert.Equal(NativeSmartCoordinatorFeedbackFlags.None, feedback.Flags);
        HostManagerSmartCoordinator.ValidateNativeFeedback(
            feedback,
            action,
            DateTimeOffset.UnixEpoch.ToUnixTimeMilliseconds());
    }

    private static void AssertTerminalFailure(
        NativeSmartCoordinatorActionScope scope,
        HostManagerSmartCoordinator.TransactionEffectStatus effectStatus)
    {
        var action = CreateAction(scope);
        var resolved = HostManagerSmartCoordinator.ResolveTerminalFeedbackEffectStatus(
            effectStatus,
            previousOwnershipRestored: true);
        var feedback = HostManagerSmartCoordinator.CreateUnchangedFeedback(
            in action,
            resolved,
            systemError: 123,
            completedAt: DateTimeOffset.UnixEpoch.AddSeconds(1));

        Assert.Equal(
            HostManagerSmartCoordinator.TransactionEffectStatus.OwnershipLost,
            resolved);
        Assert.Equal(NativeSmartCoordinatorFeedbackStatus.OwnershipLost, feedback.Status);
        Assert.Equal(NativeSmartCoordinatorFeedbackValidity.CompletedAt, feedback.ValidMask);
        Assert.Equal(NativeSmartCoordinatorFeedbackFlags.None, feedback.Flags);
        Assert.Equal(123U, feedback.SystemErrorCode);
    }

    private static NativeSmartCoordinatorAction CreateAction(
        NativeSmartCoordinatorActionScope scope)
        => new()
        {
            StructSize = NativeSmartCoordinatorSession.SizeOf<NativeSmartCoordinatorAction>(),
            Flags = NativeSmartCoordinatorActionFlags.RequiresFeedback,
            ValidMask = NativeSmartCoordinatorActionValidity.ProcessIdentity |
                NativeSmartCoordinatorActionValidity.ProcessGrade |
                NativeSmartCoordinatorActionValidity.CpuScore,
            ActionId = 1,
            PlanEpoch = 2,
            ConfigurationGeneration = 3,
            TargetKey = 4UL,
            SoftwareKey = 5,
            ProcessStartKey = 6UL,
            ProcessId = 7U,
            CpuScore = 8,
            Scope = scope,
            Disposition = NativeSmartCoordinatorActionDisposition.Apply,
            DomainMask = NativeSmartCoordinatorGradeDomains.Process,
            FromProcessGrade = NativeSmartCoordinatorProcessGrade.Level1,
            ToProcessGrade = NativeSmartCoordinatorProcessGrade.Level2,
        };

}
