using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Domain.RuntimeSpecialization;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization;

public sealed class HostManagerOperationCoordinatorRuntime(
    IRuntimePlanProvider runtimePlanProvider,
    HostManagerDeploymentState deploymentState)
{
    internal CompiledHostManagerOperationCoordinatorPlan CaptureDesired()
    {
        var plan = runtimePlanProvider.Current.HostManager.RequirePublished()
            .OperationCoordinator;
        return plan.IsPublished
            ? plan
            : throw new InvalidOperationException(
                "The Host Manager operation-coordinator configuration is not published.");
    }

    internal HostManagerDeploymentAttemptToken BeginInitialCreate(CompiledHostManagerPlan plan)
        => deploymentState.BeginAttempt(
            HostManagerModuleKind.OperationCoordinator,
            plan,
            HostManagerDeploymentOperation.InitialCreate);

    internal HostManagerDeploymentAttemptToken BeginCreate(CompiledHostManagerPlan plan)
        => deploymentState.BeginAttempt(
            HostManagerModuleKind.OperationCoordinator,
            plan,
            deploymentState.Snapshot.OperationCoordinator.AppliedPlanEpoch == 0
                ? HostManagerDeploymentOperation.InitialCreate
                : HostManagerDeploymentOperation.HostRecreateAndHotPublish);

    internal HostManagerDeploymentAttemptToken BeginHotPublish(CompiledHostManagerPlan plan)
        => deploymentState.BeginAttempt(
            HostManagerModuleKind.OperationCoordinator,
            plan,
            HostManagerDeploymentOperation.HotPublish);

    internal HostManagerDeploymentAttemptToken BeginHostRecreateAndHotPublish(
        CompiledHostManagerPlan plan)
        => deploymentState.BeginAttempt(
            HostManagerModuleKind.OperationCoordinator,
            plan,
            HostManagerDeploymentOperation.HostRecreateAndHotPublish);

    internal HostManagerDeploymentAttemptSettlement CompleteSucceeded(
        HostManagerDeploymentAttemptToken token)
        => HostManagerDeploymentAttemptSettlementGuard.RequireApplied(
            deploymentState.CompleteAttemptSucceeded(token),
            HostManagerModuleKind.OperationCoordinator);

    internal HostManagerDeploymentAttemptSettlement CompleteFailed(
        HostManagerDeploymentAttemptToken token,
        string failureCode)
        => HostManagerDeploymentAttemptSettlementGuard.RequireFailed(
            deploymentState.CompleteAttemptFailed(token, failureCode),
            HostManagerModuleKind.OperationCoordinator);
}
