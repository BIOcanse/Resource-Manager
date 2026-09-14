using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Domain.RuntimeSpecialization;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization;

public sealed class HostManagerMetricSnapshotRuntime(
    IRuntimePlanProvider runtimePlanProvider,
    HostManagerDeploymentState deploymentState)
{
    internal CompiledHostManagerMetricSnapshotPlan CaptureDesired()
    {
        var plan = runtimePlanProvider.Current.HostManager
            .RequirePublished()
            .MetricSnapshot;
        return plan.IsPublished
            ? plan
            : throw new InvalidOperationException(
                "The Host Manager metric-snapshot plan is not published.");
    }

    internal HostManagerDeploymentAttemptToken BeginInitialCreate(
        CompiledHostManagerPlan plan)
        => deploymentState.BeginAttempt(
            HostManagerModuleKind.MetricSnapshot,
            plan,
            HostManagerDeploymentOperation.InitialCreate);

    internal HostManagerDeploymentAttemptToken BeginHotPublish(
        CompiledHostManagerPlan plan)
        => deploymentState.BeginAttempt(
            HostManagerModuleKind.MetricSnapshot,
            plan,
            HostManagerDeploymentOperation.HotPublish);

    internal HostManagerDeploymentAttemptToken BeginHostRecreateAndHotPublish(
        CompiledHostManagerPlan plan)
        => deploymentState.BeginAttempt(
            HostManagerModuleKind.MetricSnapshot,
            plan,
            HostManagerDeploymentOperation.HostRecreateAndHotPublish);

    internal bool CanApplyHot(CompiledHostManagerPlan plan)
        => deploymentState.CanApplyHot(
            HostManagerModuleKind.MetricSnapshot,
            plan);

    internal HostManagerDeploymentAttemptSettlement CompleteSucceeded(
        HostManagerDeploymentAttemptToken token)
        => HostManagerDeploymentAttemptSettlementGuard.RequireApplied(
            deploymentState.CompleteAttemptSucceeded(token),
            HostManagerModuleKind.MetricSnapshot);

    internal HostManagerDeploymentAttemptSettlement CompleteFailed(
        HostManagerDeploymentAttemptToken token,
        string failureCode)
        => HostManagerDeploymentAttemptSettlementGuard.RequireFailed(
            deploymentState.CompleteAttemptFailed(token, failureCode),
            HostManagerModuleKind.MetricSnapshot);
}
