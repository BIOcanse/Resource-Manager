using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Domain.RuntimeSpecialization;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization;

public sealed class HostManagerMemoryCleanupRuntime(
    IRuntimePlanProvider runtimePlanProvider,
    HostManagerDeploymentState deploymentState)
{
    public MemoryCleanupRuntimePlan CaptureDesired()
    {
        var plan = runtimePlanProvider.Current.HostManager.RequirePublished();
        return new MemoryCleanupRuntimePlan(
            plan,
            plan.HostRecreate.MemoryCleanup,
            plan.HotPublish.MemoryCleanup);
    }

    public bool CanApplyHot(CompiledHostManagerPlan plan)
        => deploymentState.CanApplyHot(HostManagerModuleKind.MemoryCleanup, plan);

    internal HostManagerDeploymentAttemptToken BeginInitialCreate(CompiledHostManagerPlan plan)
        => deploymentState.BeginAttempt(
            HostManagerModuleKind.MemoryCleanup,
            plan,
            HostManagerDeploymentOperation.InitialCreate);

    internal HostManagerDeploymentAttemptToken BeginHotPublish(CompiledHostManagerPlan plan)
        => deploymentState.BeginAttempt(
            HostManagerModuleKind.MemoryCleanup,
            plan,
            HostManagerDeploymentOperation.HotPublish);

    internal HostManagerDeploymentAttemptSettlement CompleteSucceeded(
        HostManagerDeploymentAttemptToken token)
        => HostManagerDeploymentAttemptSettlementGuard.RequireApplied(
            deploymentState.CompleteAttemptSucceeded(token),
            HostManagerModuleKind.MemoryCleanup);

    internal HostManagerDeploymentAttemptSettlement CompleteFailed(
        HostManagerDeploymentAttemptToken token,
        string failureCode,
        HostManagerNativeResultSnapshot? nativeResult = null)
        => HostManagerDeploymentAttemptSettlementGuard.RequireFailed(
            deploymentState.CompleteAttemptFailed(token, failureCode, nativeResult),
            HostManagerModuleKind.MemoryCleanup);
}

public sealed record MemoryCleanupRuntimePlan(
    CompiledHostManagerPlan HostPlan,
    CompiledHostManagerMemoryCleanupRecreatePlan Recreate,
    CompiledHostManagerMemoryCleanupHotPublishPlan HotPublish);
