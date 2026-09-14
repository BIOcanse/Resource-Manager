using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Domain.RuntimeSpecialization;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization;

public sealed class HostManagerPlacementCoordinatorRuntime(
    IRuntimePlanProvider runtimePlanProvider,
    HostManagerDeploymentState deploymentState)
{
    public PlacementCoordinatorRuntimePlan CaptureDesired()
    {
        var hostPlan = runtimePlanProvider.Current.HostManager.RequirePublished();
        return new PlacementCoordinatorRuntimePlan(
            hostPlan,
            hostPlan.BuildSpecialize.PlacementCoordinatorAbiVersion,
            hostPlan.HostRecreate.PlacementCoordinator,
            hostPlan.HotPublish.PlacementCoordinator);
    }

    internal HostManagerDeploymentAttemptToken BeginInitialCreate(CompiledHostManagerPlan plan)
        => deploymentState.BeginAttempt(
            HostManagerModuleKind.PlacementCoordinator,
            plan,
            HostManagerDeploymentOperation.InitialCreate);

    internal HostManagerDeploymentAttemptToken BeginHotPublish(CompiledHostManagerPlan plan)
        => deploymentState.BeginAttempt(
            HostManagerModuleKind.PlacementCoordinator,
            plan,
            HostManagerDeploymentOperation.HotPublish);

    internal HostManagerDeploymentAttemptToken BeginHostRecreateAndHotPublish(CompiledHostManagerPlan plan)
        => deploymentState.BeginAttempt(
            HostManagerModuleKind.PlacementCoordinator,
            plan,
            HostManagerDeploymentOperation.HostRecreateAndHotPublish);

    public bool CanApplyHot(CompiledHostManagerPlan plan)
        => deploymentState.CanApplyHot(HostManagerModuleKind.PlacementCoordinator, plan);

    internal HostManagerDeploymentAttemptSettlement CompleteSucceeded(
        HostManagerDeploymentAttemptToken token)
        => deploymentState.CompleteAttemptSucceeded(token);

    internal HostManagerDeploymentAttemptSettlement CompleteFailed(
        HostManagerDeploymentAttemptToken token,
        string failureCode,
        HostManagerNativeResultSnapshot? nativeResult = null)
        => deploymentState.CompleteAttemptFailed(token, failureCode, nativeResult);
}

public sealed record PlacementCoordinatorRuntimePlan(
    CompiledHostManagerPlan HostPlan,
    uint AbiVersion,
    CompiledHostManagerPlacementCoordinatorRecreatePlan Recreate,
    CompiledHostManagerPlacementCoordinatorHotPublishPlan HotPublish);
