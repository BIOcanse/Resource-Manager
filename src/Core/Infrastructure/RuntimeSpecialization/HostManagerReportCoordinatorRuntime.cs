using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Domain.RuntimeSpecialization;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization;

public sealed class HostManagerReportCoordinatorRuntime(
    HostManagerDeploymentState deploymentState)
{
    internal CompiledHostManagerReportCoordinatorPlan CaptureDesired(
        RuntimePlanPublicationLease publicationLease)
    {
        ArgumentNullException.ThrowIfNull(publicationLease);
        var plan = publicationLease.Plan.HostManager.RequirePublished().ReportCoordinator;
        return plan.IsPublished
            ? plan
            : throw new InvalidOperationException(
                "The Host Manager report-coordinator configuration is not published.");
    }

    internal HostManagerDeploymentAttemptToken BeginInitialCreate(
        CompiledHostManagerPlan plan)
        => deploymentState.BeginAttempt(
            HostManagerModuleKind.ReportCoordinator,
            plan,
            HostManagerDeploymentOperation.InitialCreate);

    internal HostManagerDeploymentAttemptToken BeginHotPublish(
        CompiledHostManagerPlan plan)
        => deploymentState.BeginAttempt(
            HostManagerModuleKind.ReportCoordinator,
            plan,
            HostManagerDeploymentOperation.HotPublish);

    internal HostManagerDeploymentAttemptToken BeginHostRecreateAndHotPublish(
        CompiledHostManagerPlan plan)
        => deploymentState.BeginAttempt(
            HostManagerModuleKind.ReportCoordinator,
            plan,
            HostManagerDeploymentOperation.HostRecreateAndHotPublish);

    internal HostManagerDeploymentAttemptSettlement CompleteSucceeded(
        HostManagerDeploymentAttemptToken token)
        => HostManagerDeploymentAttemptSettlementGuard.RequireApplied(
            deploymentState.CompleteAttemptSucceeded(token),
            HostManagerModuleKind.ReportCoordinator);

    internal HostManagerDeploymentAttemptSettlement CompleteFailed(
        HostManagerDeploymentAttemptToken token,
        string failureCode)
        => HostManagerDeploymentAttemptSettlementGuard.RequireFailed(
            deploymentState.CompleteAttemptFailed(token, failureCode),
            HostManagerModuleKind.ReportCoordinator);
}
