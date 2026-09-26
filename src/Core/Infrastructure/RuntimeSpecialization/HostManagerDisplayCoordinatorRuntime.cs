using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Domain.RuntimeSpecialization;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization;

public sealed class HostManagerDisplayCoordinatorRuntime(
    IRuntimePlanProvider runtimePlanProvider,
    HostManagerDeploymentState deploymentState)
{
    internal CompiledHostManagerDisplayCoordinatorPlan CaptureDesired()
    {
        var plan = runtimePlanProvider.Current.HostManager
            .RequirePublished()
            .DisplayCoordinator;
        return plan.IsPublished
            ? plan
            : throw new InvalidOperationException(
                "The Host Manager display-coordinator plan is not published.");
    }

    internal HostManagerDeploymentAttemptToken BeginInitialCreate(
        CompiledHostManagerPlan plan)
        => deploymentState.BeginAttempt(
            HostManagerModuleKind.DisplayCoordinator,
            plan,
            HostManagerDeploymentOperation.InitialCreate);

    internal HostManagerDeploymentAttemptToken BeginHostRecreate(
        CompiledHostManagerPlan plan)
        => deploymentState.BeginAttempt(
            HostManagerModuleKind.DisplayCoordinator,
            plan,
            HostManagerDeploymentOperation.HostRecreate);

    internal HostManagerDeploymentAttemptSettlement CompleteSucceeded(
        HostManagerDeploymentAttemptToken token)
        => HostManagerDeploymentAttemptSettlementGuard.RequireApplied(
            deploymentState.CompleteAttemptSucceeded(token),
            HostManagerModuleKind.DisplayCoordinator);

    internal HostManagerDeploymentAttemptSettlement CompleteFailed(
        HostManagerDeploymentAttemptToken token,
        string failureCode)
        => HostManagerDeploymentAttemptSettlementGuard.RequireFailed(
            deploymentState.CompleteAttemptFailed(token, failureCode),
            HostManagerModuleKind.DisplayCoordinator);
}
