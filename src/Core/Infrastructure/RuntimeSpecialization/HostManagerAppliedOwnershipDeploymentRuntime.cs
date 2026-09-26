using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Domain.RuntimeSpecialization;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization;

public sealed class HostManagerAppliedOwnershipDeploymentRuntime(
    IRuntimePlanProvider runtimePlanProvider,
    HostManagerDeploymentState deploymentState)
{
    internal HostManagerAppliedOwnershipRuntimePlan CaptureDesired()
    {
        var hostPlan = runtimePlanProvider.Current.HostManager.RequirePublished();
        var recreate = hostPlan.HostRecreate.AppliedOwnership;
        var hotPublish = hostPlan.HotPublish.AppliedOwnership;
        if (!recreate.IsPublished || !hotPublish.IsPublished)
        {
            throw new InvalidOperationException(
                "The Host Manager applied-ownership configuration is not published.");
        }

        return new HostManagerAppliedOwnershipRuntimePlan(
            hostPlan,
            hostPlan.BuildSpecialize.AppliedOwnershipAbiVersion,
            recreate,
            hotPublish);
    }

    internal HostManagerDeploymentAttemptToken BeginInitialCreate(CompiledHostManagerPlan plan)
        => deploymentState.BeginAttempt(
            HostManagerModuleKind.AppliedOwnership,
            plan,
            HostManagerDeploymentOperation.InitialCreate);

    internal HostManagerDeploymentAttemptToken BeginHotPublish(CompiledHostManagerPlan plan)
        => deploymentState.BeginAttempt(
            HostManagerModuleKind.AppliedOwnership,
            plan,
            HostManagerDeploymentOperation.HotPublish);

    internal HostManagerDeploymentAttemptToken BeginHostRecreateAndHotPublish(
        CompiledHostManagerPlan plan)
        => deploymentState.BeginAttempt(
            HostManagerModuleKind.AppliedOwnership,
            plan,
            HostManagerDeploymentOperation.HostRecreateAndHotPublish);

    internal bool CanApplyHot(CompiledHostManagerPlan plan)
        => deploymentState.CanApplyHot(HostManagerModuleKind.AppliedOwnership, plan);

    internal HostManagerDeploymentAttemptSettlement CompleteSucceeded(
        HostManagerDeploymentAttemptToken token)
        => HostManagerDeploymentAttemptSettlementGuard.RequireApplied(
            deploymentState.CompleteAttemptSucceeded(token),
            HostManagerModuleKind.AppliedOwnership);

    internal HostManagerDeploymentAttemptSettlement CompleteFailed(
        HostManagerDeploymentAttemptToken token,
        string failureCode,
        HostManagerNativeResultSnapshot? nativeResult = null)
        => HostManagerDeploymentAttemptSettlementGuard.RequireFailed(
            deploymentState.CompleteAttemptFailed(token, failureCode, nativeResult),
            HostManagerModuleKind.AppliedOwnership);
}

internal sealed record HostManagerAppliedOwnershipRuntimePlan(
    CompiledHostManagerPlan HostPlan,
    uint AbiVersion,
    CompiledHostManagerAppliedOwnershipRecreatePlan Recreate,
    CompiledHostManagerAppliedOwnershipHotPublishPlan HotPublish)
{
    internal bool HasSameModuleConfiguration(
        HostManagerAppliedOwnershipRuntimePlan other)
        => AbiVersion == other.AbiVersion
            && Recreate == other.Recreate
            && HotPublish == other.HotPublish;
}
