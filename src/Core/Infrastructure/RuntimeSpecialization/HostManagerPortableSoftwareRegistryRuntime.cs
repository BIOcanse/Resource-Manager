using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Domain.RuntimeSpecialization;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization;

public sealed class HostManagerPortableSoftwareRegistryRuntime(
    IRuntimePlanProvider runtimePlanProvider,
    HostManagerDeploymentState deploymentState)
{
    internal CompiledHostManagerPortableSoftwareRegistryPlan CaptureDesired()
    {
        var plan = runtimePlanProvider.Current.HostManager.RequirePublished().PortableSoftwareRegistry;
        return plan.IsPublished
            ? plan
            : throw new InvalidOperationException(
                "The Host Manager portable-software-registry configuration is not published.");
    }

    internal HostManagerDeploymentAttemptToken BeginInitialCreate(CompiledHostManagerPlan plan)
        => deploymentState.BeginAttempt(
            HostManagerModuleKind.PortableSoftwareRegistry,
            plan,
            HostManagerDeploymentOperation.InitialCreate);

    internal HostManagerDeploymentAttemptToken BeginHotPublish(CompiledHostManagerPlan plan)
        => deploymentState.BeginAttempt(
            HostManagerModuleKind.PortableSoftwareRegistry,
            plan,
            HostManagerDeploymentOperation.HotPublish);

    internal HostManagerDeploymentAttemptToken BeginHostRecreateAndHotPublish(CompiledHostManagerPlan plan)
        => deploymentState.BeginAttempt(
            HostManagerModuleKind.PortableSoftwareRegistry,
            plan,
            HostManagerDeploymentOperation.HostRecreateAndHotPublish);

    internal bool CanApplyHot(CompiledHostManagerPlan plan)
        => deploymentState.CanApplyHot(HostManagerModuleKind.PortableSoftwareRegistry, plan);

    internal HostManagerDeploymentAttemptSettlement CompleteSucceeded(
        HostManagerDeploymentAttemptToken token)
        => HostManagerDeploymentAttemptSettlementGuard.RequireApplied(
            deploymentState.CompleteAttemptSucceeded(token),
            HostManagerModuleKind.PortableSoftwareRegistry);

    internal HostManagerDeploymentAttemptSettlement CompleteFailed(
        HostManagerDeploymentAttemptToken token,
        string failureCode)
        => HostManagerDeploymentAttemptSettlementGuard.RequireFailed(
            deploymentState.CompleteAttemptFailed(token, failureCode),
            HostManagerModuleKind.PortableSoftwareRegistry);
}
