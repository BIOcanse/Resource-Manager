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
            NativeSmartCoordinatorActionScope.AdapterSoftware,
            HostManagerSmartCoordinator.TransactionEffectStatus.Rejected);
    }

    [Fact]
    public void CpuOnlyRestore_DoesNotClaimCpuOwnershipWhenAnotherCompositeDomainRemains()
    {
        var action = CreateAction(NativeSmartCoordinatorActionScope.AdapterSoftware);
        action.Disposition = NativeSmartCoordinatorActionDisposition.Restore;
        action.DomainMask = NativeSmartCoordinatorGradeDomains.Cpu;
        action.FromCpuGrade = NativeSmartCoordinatorAdapterGrade.Optimize;
        action.ToCpuGrade = NativeSmartCoordinatorAdapterGrade.Normal;
        action.FromGpuGrade = NativeSmartCoordinatorAdapterGrade.Normal;
        action.ToGpuGrade = NativeSmartCoordinatorAdapterGrade.Normal;

        var feedback = HostManagerSmartCoordinator.CreateSucceededFeedback(
            in action,
            DateTimeOffset.UnixEpoch.AddSeconds(1));

        Assert.Equal(NativeSmartCoordinatorFeedbackFlags.None, feedback.Flags);
        Assert.Equal(
            NativeSmartCoordinatorFeedbackValidity.CompletedAt |
                NativeSmartCoordinatorFeedbackValidity.ActualCpuGrade,
            feedback.ValidMask);
        Assert.Equal(NativeSmartCoordinatorAdapterGrade.Normal, feedback.ActualCpuGrade);
    }

    [Fact]
    public void CpuOnlyApply_ClaimsOnlyTheActionDomain()
    {
        var action = CreateAction(NativeSmartCoordinatorActionScope.AdapterSoftware);
        action.DomainMask = NativeSmartCoordinatorGradeDomains.Cpu;
        action.FromCpuGrade = NativeSmartCoordinatorAdapterGrade.Normal;
        action.ToCpuGrade = NativeSmartCoordinatorAdapterGrade.Optimize;
        action.FromGpuGrade = NativeSmartCoordinatorAdapterGrade.Normal;
        action.ToGpuGrade = NativeSmartCoordinatorAdapterGrade.Normal;

        var feedback = HostManagerSmartCoordinator.CreateSucceededFeedback(
            in action,
            DateTimeOffset.UnixEpoch.AddSeconds(1));

        Assert.Equal(
            NativeSmartCoordinatorFeedbackFlags.RollbackPayloadPersisted |
                NativeSmartCoordinatorFeedbackFlags.CpuOwned,
            feedback.Flags);
        Assert.DoesNotContain(
            "OwnershipMatchesActionFrom",
            File.ReadAllText(Path.Combine(
                FindAppRoot(),
                "Infrastructure",
                "Optimization",
                "HostManagerSmartCoordinator.NativeTransactions.cs")),
            StringComparison.Ordinal);
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

    [Theory]
    [InlineData(
        (int)HostManagerAdapterSchedulingCaptureStatus.Captured,
        (int)HostManagerSmartCoordinator.TransactionEffectStatus.Succeeded)]
    [InlineData(
        (int)HostManagerAdapterSchedulingCaptureStatus.Unsupported,
        (int)HostManagerSmartCoordinator.TransactionEffectStatus.Rejected)]
    [InlineData(
        (int)HostManagerAdapterSchedulingCaptureStatus.InvalidPayload,
        (int)HostManagerSmartCoordinator.TransactionEffectStatus.Rejected)]
    [InlineData(
        (int)HostManagerAdapterSchedulingCaptureStatus.Unavailable,
        (int)HostManagerSmartCoordinator.TransactionEffectStatus.Skipped)]
    [InlineData(
        (int)HostManagerAdapterSchedulingCaptureStatus.Conflict,
        (int)HostManagerSmartCoordinator.TransactionEffectStatus.OwnershipLost)]
    public void AdapterCaptureStatus_PreservesPreEffectFailureSemantics(
        int status,
        int expected)
    {
        Assert.Equal(
            (HostManagerSmartCoordinator.TransactionEffectStatus)expected,
            HostManagerSmartCoordinator.ResolveAdapterCaptureEffectStatus(
                (HostManagerAdapterSchedulingCaptureStatus)status));
    }

    [Fact]
    public void SkippedCaptureFeedback_DoesNotInventActualGrades()
    {
        var action = CreateAction(NativeSmartCoordinatorActionScope.AdapterSoftware);

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
            ValidMask = scope == NativeSmartCoordinatorActionScope.ProcessPolicy
                ? NativeSmartCoordinatorActionValidity.ProcessIdentity |
                    NativeSmartCoordinatorActionValidity.ProcessGrade |
                    NativeSmartCoordinatorActionValidity.CpuScore
                : NativeSmartCoordinatorActionValidity.SoftwareIdentity |
                    NativeSmartCoordinatorActionValidity.CpuGrade |
                    NativeSmartCoordinatorActionValidity.GpuGrade |
                    NativeSmartCoordinatorActionValidity.CpuScore |
                    NativeSmartCoordinatorActionValidity.GpuScore,
            ActionId = 1,
            PlanEpoch = 2,
            ConfigurationGeneration = 3,
            TargetKey = scope == NativeSmartCoordinatorActionScope.ProcessPolicy ? 4UL : 0,
            SoftwareKey = 5,
            ProcessStartKey = scope == NativeSmartCoordinatorActionScope.ProcessPolicy ? 6UL : 0,
            ProcessId = scope == NativeSmartCoordinatorActionScope.ProcessPolicy ? 7U : 0,
            CpuScore = 8,
            GpuScore = scope == NativeSmartCoordinatorActionScope.AdapterSoftware ? 9 : 0,
            Scope = scope,
            Disposition = NativeSmartCoordinatorActionDisposition.Apply,
            DomainMask = scope == NativeSmartCoordinatorActionScope.ProcessPolicy
                ? NativeSmartCoordinatorGradeDomains.Process
                : NativeSmartCoordinatorGradeDomains.Cpu |
                    NativeSmartCoordinatorGradeDomains.Gpu,
            FromProcessGrade = NativeSmartCoordinatorProcessGrade.Level1,
            ToProcessGrade = NativeSmartCoordinatorProcessGrade.Level2,
            FromCpuGrade = NativeSmartCoordinatorAdapterGrade.Optimize,
            ToCpuGrade = NativeSmartCoordinatorAdapterGrade.Extreme,
            FromGpuGrade = NativeSmartCoordinatorAdapterGrade.Extreme,
            ToGpuGrade = NativeSmartCoordinatorAdapterGrade.Optimize
        };

    private static string FindAppRoot(
        [System.Runtime.CompilerServices.CallerFilePath] string sourceFilePath = "")
    {
        var sourceDirectory = Path.GetDirectoryName(sourceFilePath)!;
        var appRoot = Path.GetFullPath(Path.Combine(sourceDirectory, "..", "..", "Resource Manager", "Resource Manager-APP"));
        return File.Exists(Path.Combine(appRoot, "ResourceManager.App.csproj"))
            ? appRoot
            : throw new DirectoryNotFoundException(
                "Could not locate Resource Manager-APP from the test source path.");
    }
}
